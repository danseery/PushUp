using System;
using FishNet.Managing;
using FishNet.Transporting;
using PushUp.Core;
using PushUp.Steam;
using Steamworks;
using UnityEngine;

namespace PushUp.Networking
{
    public enum SteamConnectionPhase : byte
    {
        Idle,
        InvitationReceived,
        JoiningLobby,
        LobbyReady,
        ClientLobbyReady,
        Connecting,
        Authenticating,
        HostTransportReady,
        Connected,
        Cancelled,
        HostEnded,
        Failed
    }

    public readonly struct SteamConnectionStatus
    {
        public SteamConnectionStatus(SteamConnectionPhase phase, string message, string diagnostic = "")
        {
            Phase = phase;
            Message = message;
            Diagnostic = diagnostic;
        }

        public SteamConnectionPhase Phase { get; }
        public string Message { get; }
        public string Diagnostic { get; }
    }

    /// <summary>
    /// Converts explicit Steam lobby intent into transport lifecycle events. Joining a lobby does not imply that
    /// gameplay is ready: the host starts its server from Start Hill, clients connect only after the lobby enters
    /// starting/playing, and authentication completes before Connected is emitted.
    /// </summary>
    [RequireComponent(typeof(SteamSessionService), typeof(NetworkManager), typeof(SteamSocketsTransport))]
    [RequireComponent(typeof(SteamLobbyAuthenticator))]
    public sealed class SteamNetworkCoordinator : MonoBehaviour
    {
        public const float LobbyOperationTimeoutSeconds = 20f;
        public const float ConnectionTimeoutSeconds = 15f;

        private SteamSessionService _session;
        private NetworkManager _networkManager;
        private SteamSocketsTransport _transport;
        private SteamLobbyAuthenticator _authenticator;
        private CSteamID _lastLobby = CSteamID.Nil;
        private float _phaseDeadline;
        private bool _intentionalStop;
        private bool _hostTransportRequested;
        private bool _clientTransportRequested;

        public event Action<SteamConnectionStatus> StatusChanged;
        public event Action HostTransportReady;
        public event Action ClientTransportReady;
        public event Action Cancelled;
        public event Action<string, string> Disconnected;

        public SteamConnectionPhase Phase { get; private set; } = SteamConnectionPhase.Idle;
        public string StatusMessage { get; private set; } = "Choose how to play.";
        public string LastDiagnostic { get; private set; } = string.Empty;
        public bool IsJoinInProgress => Phase is SteamConnectionPhase.JoiningLobby or
            SteamConnectionPhase.Connecting or SteamConnectionPhase.Authenticating;
        public bool CanRetry => Phase == SteamConnectionPhase.Failed && _lastLobby.IsValid();
        public bool CanEnterHill => Phase is SteamConnectionPhase.Connected or SteamConnectionPhase.HostTransportReady;

        private void Awake()
        {
            _session = GetComponent<SteamSessionService>();
            _networkManager = GetComponent<NetworkManager>();
            _transport = GetComponent<SteamSocketsTransport>();
            _authenticator = GetComponent<SteamLobbyAuthenticator>();

            _session.LobbyJoinStarted += OnLobbyJoinStarted;
            _session.LobbyJoined += OnLobbyJoined;
            _session.LobbySnapshotChanged += OnLobbySnapshotChanged;
            _session.PendingInviteChanged += OnPendingInviteChanged;
            _session.SessionError += OnSessionError;
            _session.OriginalHostLeft += OnOriginalHostLeft;
            _networkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;
            _networkManager.ClientManager.OnAuthenticated += OnClientAuthenticated;
            _networkManager.ServerManager.OnServerConnectionState += OnServerConnectionState;
            _transport.ClientProblem += OnClientProblem;
            _authenticator.ClientAuthenticationResult += OnClientAuthenticationResult;
        }

        private void Update()
        {
            if (_session != null && _session.IsOperationTimedOut(Time.unscaledTime, LobbyOperationTimeoutSeconds))
            {
                _session.CancelActiveOperation();
                Fail("Steam did not finish the lobby operation within 20 seconds.");
                return;
            }

            if (_phaseDeadline > 0f && Time.unscaledTime >= _phaseDeadline &&
                Phase is SteamConnectionPhase.Connecting or SteamConnectionPhase.Authenticating)
                Fail("Connection timed out after 15 seconds.", _transport.LastClientError);
        }

        private void OnDestroy()
        {
            if (_session != null)
            {
                _session.LobbyJoinStarted -= OnLobbyJoinStarted;
                _session.LobbyJoined -= OnLobbyJoined;
                _session.LobbySnapshotChanged -= OnLobbySnapshotChanged;
                _session.PendingInviteChanged -= OnPendingInviteChanged;
                _session.SessionError -= OnSessionError;
                _session.OriginalHostLeft -= OnOriginalHostLeft;
            }
            if (_networkManager != null)
            {
                _networkManager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
                _networkManager.ClientManager.OnAuthenticated -= OnClientAuthenticated;
                _networkManager.ServerManager.OnServerConnectionState -= OnServerConnectionState;
            }
            if (_transport != null)
                _transport.ClientProblem -= OnClientProblem;
            if (_authenticator != null)
                _authenticator.ClientAuthenticationResult -= OnClientAuthenticationResult;
        }

        /// <summary>Starts the host socket only after the host explicitly chooses Start Hill.</summary>
        public bool StartHostTransport(CSteamID lobby)
        {
            if (!lobby.IsValid() || _session == null || lobby != _session.CurrentLobby || !_session.IsLobbyOwner)
                return FailAndReturn("Only the current Steam lobby owner can start the host transport.");

            _intentionalStop = false;
            _lastLobby = lobby;
            _hostTransportRequested = true;
            _clientTransportRequested = false;
            _phaseDeadline = Time.unscaledTime + ConnectionTimeoutSeconds;
            SetStatus(SteamConnectionPhase.Connecting, "Starting host transport...");

            if (_networkManager.ServerManager.Started)
            {
                OnHostTransportStarted();
                return true;
            }
            if (_networkManager.ServerManager.StartConnection())
                return true;

            _hostTransportRequested = false;
            return FailAndReturn("FishNet could not start the host.", _transport.LastClientError);
        }

        /// <summary>Connects a lobby member to the immutable original host after the lobby begins playing.</summary>
        public bool ConnectJoinedLobby(CSteamID lobby)
        {
            if (!lobby.IsValid() || _session == null || lobby != _session.CurrentLobby || _session.IsLobbyOwner)
                return FailAndReturn("The joined Steam lobby is not a valid client session.");
            if (_clientTransportRequested && Phase is SteamConnectionPhase.Connecting or
                SteamConnectionPhase.Authenticating or SteamConnectionPhase.Connected)
                return true;

            SteamLobbySnapshot snapshot = _session.GetCurrentLobbySnapshot();
            if (!snapshot.OriginalHostIsOwner || snapshot.OriginalHostSteamId == 0)
            {
                HandleUnexpectedDisconnect("The host ended the session.",
                    "The original Steam lobby owner is no longer present.", true);
                return false;
            }

            _intentionalStop = false;
            _lastLobby = lobby;
            _clientTransportRequested = true;
            _hostTransportRequested = false;
            _transport.SetClientAddress(snapshot.OriginalHostSteamId.ToString());
            _phaseDeadline = Time.unscaledTime + ConnectionTimeoutSeconds;
            SetStatus(SteamConnectionPhase.Connecting, "Connecting to host...");
            if (_networkManager.ClientManager.Started || _networkManager.ClientManager.StartConnection())
                return true;

            _clientTransportRequested = false;
            return FailAndReturn("FishNet could not start the Steam client.", _transport.LastClientError);
        }

        public void CancelConnection()
        {
            _intentionalStop = true;
            _phaseDeadline = 0f;
            StopNetworking();
            _hostTransportRequested = false;
            _clientTransportRequested = false;
            SetStatus(SteamConnectionPhase.Cancelled, "Connection cancelled.");
            Cancelled?.Invoke();
        }

        public void LeaveSession()
        {
            _intentionalStop = true;
            _phaseDeadline = 0f;
            if (_session != null && _session.IsLobbyOwner && _session.CurrentLobby.IsValid())
                _session.SetLobbyRunState(PushUpConstants.LobbyStateEnding, false, "Session ending");
            StopNetworking();
            _session?.LeaveLobby();
            _lastLobby = CSteamID.Nil;
            _hostTransportRequested = false;
            _clientTransportRequested = false;
            SetStatus(SteamConnectionPhase.Idle, "Left the Steam session.");
        }

        public bool RetryLastJoin()
        {
            if (!CanRetry)
                return false;
            CSteamID lobby = _lastLobby;
            _intentionalStop = false;
            SetStatus(SteamConnectionPhase.Idle, "Retrying Steam session...");
            bool started;
            if (ShouldReconnectCurrentLobby(_session?.CurrentLobby.m_SteamID ?? 0UL, lobby.m_SteamID,
                    _session != null && _session.IsLobbyOwner))
                started = ConnectJoinedLobby(lobby);
            else
                started = _session != null && _session.JoinLobby(lobby, SteamJoinSource.Direct);
            if (!started && Phase != SteamConnectionPhase.Failed)
                Fail("Steam could not start rejoining the previous game.");
            return started;
        }

        public void ReturnAfterFailure()
        {
            _intentionalStop = true;
            StopNetworking();
            _session?.LeaveLobby();
            _lastLobby = CSteamID.Nil;
            _hostTransportRequested = false;
            _clientTransportRequested = false;
            SetStatus(SteamConnectionPhase.Idle, "Choose how to play.");
        }

        public static bool HasTimedOut(float startedAt, float now, float timeoutSeconds) =>
            startedAt >= 0f && timeoutSeconds > 0f && now - startedAt >= timeoutSeconds;

        public static bool CanEnter(SteamConnectionPhase phase, bool clientStarted, bool serverStarted) =>
            phase is SteamConnectionPhase.Connected or SteamConnectionPhase.HostTransportReady;

        private void OnLobbyJoinStarted(CSteamID lobby)
        {
            _lastLobby = lobby;
            _intentionalStop = true;
            StopNetworking();
            _intentionalStop = false;
            _phaseDeadline = 0f;
            _hostTransportRequested = false;
            _clientTransportRequested = false;
            SetStatus(SteamConnectionPhase.JoiningLobby, "Joining Steam lobby...");
        }

        private void OnLobbyJoined(CSteamID lobby)
        {
            _lastLobby = lobby;
            _phaseDeadline = 0f;
            SteamLobbySnapshot snapshot = _session.GetCurrentLobbySnapshot();
            if (_session.IsLobbyOwner)
                SetStatus(SteamConnectionPhase.LobbyReady, "Friends lobby ready. Invite friends, then start the hill.");
            else if (snapshot.RunState is PushUpConstants.LobbyStateStarting or PushUpConstants.LobbyStateRunning)
                ConnectJoinedLobby(lobby);
            else
                SetStatus(SteamConnectionPhase.ClientLobbyReady, "Joined lobby. Waiting for the host to start the hill...");
        }

        private void OnLobbySnapshotChanged(SteamLobbySnapshot snapshot)
        {
            if (!snapshot.LobbyId.IsValid() || snapshot.LobbyId != _session.CurrentLobby || _session.IsLobbyOwner)
                return;

            if (!snapshot.OriginalHostIsOwner)
            {
                HandleUnexpectedDisconnect("The host ended the session.", "Steam lobby ownership changed.", true);
                return;
            }

            if (snapshot.RunState is PushUpConstants.LobbyStateStarting or PushUpConstants.LobbyStateRunning)
                ConnectJoinedLobby(snapshot.LobbyId);
            else if (snapshot.RunState == PushUpConstants.LobbyStateEnding)
                HandleUnexpectedDisconnect("The host ended the session.", string.Empty, true);
            else if (!_clientTransportRequested)
                SetStatus(SteamConnectionPhase.ClientLobbyReady, "Waiting for the host to start the hill...");
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started && _clientTransportRequested)
            {
                _phaseDeadline = Time.unscaledTime + ConnectionTimeoutSeconds;
                SetStatus(SteamConnectionPhase.Authenticating, "Authenticating with host...");
                return;
            }

            if (args.ConnectionState != LocalConnectionState.Stopped || _intentionalStop || !_clientTransportRequested)
                return;

            bool hostEnded = HasOriginalHostEnded();
            string message = hostEnded
                ? "The host ended the session."
                : Phase == SteamConnectionPhase.Connected
                    ? "The connection to the host was lost. You can rejoin the same game."
                    : "Could not connect to the host. You can retry.";
            HandleUnexpectedDisconnect(message, _transport.LastClientError, hostEnded);
        }

        private void OnClientAuthenticated()
        {
            if (!_clientTransportRequested)
                return;
            _phaseDeadline = 0f;
            SetStatus(SteamConnectionPhase.Connected, "Connected. Waiting for the hill and player spawn...");
            ClientTransportReady?.Invoke();
        }

        private void OnClientAuthenticationResult(bool passed, string reason)
        {
            if (!passed && _clientTransportRequested)
                Fail(string.IsNullOrWhiteSpace(reason) ? "The host rejected this connection." : reason);
        }

        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started && _hostTransportRequested)
            {
                OnHostTransportStarted();
                return;
            }

            if (args.ConnectionState == LocalConnectionState.Stopped && !_intentionalStop && _hostTransportRequested)
                HandleUnexpectedDisconnect("The host server stopped.", _transport.LastClientError, false);
        }

        private void OnHostTransportStarted()
        {
            _phaseDeadline = 0f;
            SetStatus(SteamConnectionPhase.HostTransportReady, "Host transport ready.");
            HostTransportReady?.Invoke();
        }

        private void OnPendingInviteChanged(SteamLobbyInvite? invite)
        {
            if (invite.HasValue && Phase is SteamConnectionPhase.Idle or SteamConnectionPhase.InvitationReceived)
                SetStatus(SteamConnectionPhase.InvitationReceived,
                    $"Invitation received from {invite.Value.InviterName}.");
            else if (!invite.HasValue && Phase == SteamConnectionPhase.InvitationReceived)
                SetStatus(SteamConnectionPhase.Idle, "Choose how to play.");
        }

        private void OnSessionError(string message)
        {
            if (_intentionalStop)
                return;
            if (message.IndexOf("host ended", StringComparison.OrdinalIgnoreCase) >= 0)
                HandleUnexpectedDisconnect(message, string.Empty, true);
            else
                Fail(message);
        }

        private void OnOriginalHostLeft(CSteamID _) =>
            HandleUnexpectedDisconnect("The host ended the session.", "The original Steam lobby owner left.", true);

        private void OnClientProblem(string diagnostic)
        {
            if (_clientTransportRequested && !_intentionalStop)
            {
                bool hostEnded = HasOriginalHostEnded();
                HandleUnexpectedDisconnect(hostEnded
                    ? "The host ended the session."
                    : Phase == SteamConnectionPhase.Connected
                        ? "The connection to the host was lost. You can rejoin the same game."
                        : "Steam Networking Sockets could not connect to the host. You can retry.", diagnostic,
                    hostEnded);
            }
        }

        private bool HasOriginalHostEnded()
        {
            SteamLobbySnapshot snapshot = _session != null
                ? _session.GetCurrentLobbySnapshot()
                : default;
            return IsDefinitiveHostLoss(snapshot.LobbyId.IsValid(), snapshot.OriginalHostIsOwner,
                snapshot.RunState);
        }

        public static bool ShouldReconnectCurrentLobby(ulong currentLobby, ulong retryLobby, bool isLobbyOwner) =>
            currentLobby != 0UL && currentLobby == retryLobby && !isLobbyOwner;

        public static bool IsDefinitiveHostLoss(bool lobbyIsValid, bool originalHostIsOwner, string runState) =>
            !lobbyIsValid || !originalHostIsOwner || runState == PushUpConstants.LobbyStateEnding;

        public static bool ShouldRetainLobbyAfterDisconnect(bool hostEnded) => !hostEnded;

        public static bool ShouldProcessDisconnect(SteamConnectionPhase currentPhase, bool hostEnded) =>
            currentPhase != SteamConnectionPhase.HostEnded &&
            (currentPhase != SteamConnectionPhase.Failed || hostEnded);

        private void HandleUnexpectedDisconnect(string message, string diagnostic, bool hostEnded)
        {
            // A later lobby-owner callback is stronger evidence than an earlier transport stop. Allow it to upgrade
            // recoverable Failed -> HostEnded so Retry is removed and the stale lobby is left.
            if (_intentionalStop || !ShouldProcessDisconnect(Phase, hostEnded))
                return;

            _phaseDeadline = 0f;
            LastDiagnostic = string.IsNullOrWhiteSpace(diagnostic) ? _transport.LastClientError : diagnostic;
            _intentionalStop = true;
            StopNetworking();
            // Keep a healthy Steam lobby membership across a transient FishNet/SDR drop. Retry can then reconnect
            // directly to the immutable original host without a second lobby round trip. True host departure still
            // clears the lobby immediately.
            if (!ShouldRetainLobbyAfterDisconnect(hostEnded))
            {
                _session?.LeaveLobby();
                _lastLobby = CSteamID.Nil;
            }
            _intentionalStop = false;
            _hostTransportRequested = false;
            _clientTransportRequested = false;
            SteamConnectionPhase phase = hostEnded ? SteamConnectionPhase.HostEnded : SteamConnectionPhase.Failed;
            SetStatus(phase, message, LastDiagnostic);
            Disconnected?.Invoke(message, LastDiagnostic);
        }

        private bool FailAndReturn(string message, string diagnostic = "")
        {
            Fail(message, diagnostic);
            return false;
        }

        private void Fail(string message, string diagnostic = "")
        {
            if (Phase == SteamConnectionPhase.Failed)
                return;
            _phaseDeadline = 0f;
            LastDiagnostic = string.IsNullOrWhiteSpace(diagnostic) ? _transport.LastClientError : diagnostic;
            Debug.LogError(string.IsNullOrWhiteSpace(LastDiagnostic)
                ? message
                : $"{message} Steam diagnostic: {LastDiagnostic}");
            _intentionalStop = true;
            StopNetworking();
            _session?.LeaveLobby();
            _intentionalStop = false;
            _hostTransportRequested = false;
            _clientTransportRequested = false;
            SetStatus(SteamConnectionPhase.Failed, message, LastDiagnostic);
        }

        private void StopNetworking()
        {
            if (_networkManager == null)
                return;
            if (_networkManager.ClientManager.Started ||
                _transport.GetConnectionState(false) != LocalConnectionState.Stopped)
                _networkManager.ClientManager.StopConnection();
            if (_networkManager.ServerManager.Started ||
                _transport.GetConnectionState(true) != LocalConnectionState.Stopped)
                _networkManager.ServerManager.StopConnection(true);
        }

        private void SetStatus(SteamConnectionPhase phase, string message, string diagnostic = "")
        {
            Phase = phase;
            StatusMessage = message;
            if (!string.IsNullOrWhiteSpace(diagnostic))
                LastDiagnostic = diagnostic;
            StatusChanged?.Invoke(new SteamConnectionStatus(phase, message, diagnostic));
        }
    }
}
