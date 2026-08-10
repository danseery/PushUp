using System;
using FishNet.Managing;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Transporting;
using PushUp.Core;
using PushUp.Gameplay;
using PushUp.Steam;
using Steamworks;
using UnityEngine;

namespace PushUp.Networking
{
    public enum SessionMode : byte
    {
        None,
        Offline,
        Steam,
        LocalDevelopment
    }

    public enum SessionPhase : byte
    {
        MainMenu,
        StartingOffline,
        CreatingLobby,
        JoiningLobby,
        ConnectingTransport,
        Authenticating,
        HostLobby,
        ClientLobby,
        WaitingForPlayer,
        StartingRun,
        InRun,
        Results,
        Leaving,
        Error,
        HostEnded
    }

    /// <summary>Immutable UI-facing view of the complete local session state.</summary>
    public readonly struct SessionSnapshot
    {
        public SessionSnapshot(SessionMode mode, SessionPhase phase, string message, string diagnostic,
            bool menuVisible, bool isHost, bool steamAvailable, bool usesSteamTransport, bool localPlayerReady,
            string roster, int memberCount, int capacity, bool hasPendingInvite, string pendingInviteName,
            bool canRetry, bool runComplete)
        {
            Mode = mode;
            Phase = phase;
            Message = message ?? string.Empty;
            Diagnostic = diagnostic ?? string.Empty;
            MenuVisible = menuVisible;
            IsHost = isHost;
            SteamAvailable = steamAvailable;
            UsesSteamTransport = usesSteamTransport;
            LocalPlayerReady = localPlayerReady;
            Roster = roster ?? string.Empty;
            MemberCount = memberCount;
            Capacity = capacity;
            HasPendingInvite = hasPendingInvite;
            PendingInviteName = pendingInviteName ?? string.Empty;
            CanRetry = canRetry;
            RunComplete = runComplete;
        }

        public SessionMode Mode { get; }
        public SessionPhase Phase { get; }
        public string Message { get; }
        public string Diagnostic { get; }
        public bool MenuVisible { get; }
        public bool IsHost { get; }
        public bool SteamAvailable { get; }
        public bool UsesSteamTransport { get; }
        public bool LocalPlayerReady { get; }
        public string Roster { get; }
        public int MemberCount { get; }
        public int Capacity { get; }
        public bool HasPendingInvite { get; }
        public string PendingInviteName { get; }
        public bool CanRetry { get; }
        public bool RunComplete { get; }

        public bool IsBusy => Phase is SessionPhase.StartingOffline or SessionPhase.CreatingLobby or
            SessionPhase.JoiningLobby or SessionPhase.ConnectingTransport or SessionPhase.Authenticating or
            SessionPhase.WaitingForPlayer or SessionPhase.StartingRun or SessionPhase.Leaving;
        public bool IsLobby => Phase is SessionPhase.HostLobby or SessionPhase.ClientLobby;
        public bool IsPlaying => Phase is SessionPhase.InRun or SessionPhase.Results;
        public bool IsPauseOpen => Phase == SessionPhase.InRun && MenuVisible;
        public bool RequiresInviteSwitchConfirmation => HasPendingInvite && Mode != SessionMode.None;
    }

    /// <summary>
    /// Owns the local menu/session lifecycle. Lobby membership, transport authentication, replicated run readiness,
    /// owned-player readiness, and UI visibility are deliberately separate states.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class SessionFlowController : MonoBehaviour
    {
        public const float PlayerSpawnTimeoutSeconds = 15f;

        [SerializeField] private RunDirector _runDirector;
        [SerializeField] private SteamSessionService _steamSession;
        [SerializeField] private SteamNetworkCoordinator _steamCoordinator;
        [SerializeField] private TransportSelector _transportSelector;
        [SerializeField] private NetworkManager _networkManager;

        private SessionMode _mode;
        private SessionPhase _phase = SessionPhase.MainMenu;
        private string _message = "Choose how to play.";
        private string _diagnostic = string.Empty;
        private bool _menuVisible = true;
        private bool _localPlayerReady;
        private bool _clientAuthenticated;
        private bool _intentionalLeave;
        private bool _subscribed;
        private bool _objectEventsSubscribed;
        private NetworkObject _pendingOwnedPlayer;
        private float _playerReadyDeadline = -1f;
        private string _roster = string.Empty;
        private int _memberCount;
        private int _capacity;
        private NetworkRunState _observedRunState;
        private SessionSnapshot _snapshot;

        public event Action<SessionSnapshot> SnapshotChanged;
        public event Action FriendSessionsChanged;

        public SessionSnapshot Snapshot => _snapshot;
        public SessionMode Mode => _mode;
        public SessionPhase Phase => _phase;
        public bool IsMenuVisible => _menuVisible;
        public bool IsInviteOverlayAvailable => _steamSession != null && _steamSession.IsOverlayAvailable;

        private void Awake()
        {
            EnsureReferences();
            SetPhysicsMode(PhysicsMode.Unity);
            Subscribe();
            Publish();
        }

        private void OnEnable()
        {
            EnsureReferences();
            Subscribe();
            Publish();
        }

        private void OnDisable() => Unsubscribe();

        private void Update()
        {
            EnsureObjectSubscription();
            EnsureRunStateSubscription();
            RefreshLocalPlayerReadiness();

            if (_phase == SessionPhase.WaitingForPlayer && _playerReadyDeadline > 0f &&
                Time.unscaledTime >= _playerReadyDeadline)
            {
                bool runReady = _observedRunState != null && _observedRunState.IsReady;
                string diagnostic = $"Authenticated={_clientAuthenticated}, RunReady={runReady}, " +
                                    $"OwnedPlayerReady={_localPlayerReady}.";
                _steamCoordinator?.CancelConnection();
                Fail("Your player could not spawn in time.", diagnostic);
                return;
            }

            if (_phase == SessionPhase.InRun && _runDirector != null && _runDirector.IsComplete)
                Transition(SessionPhase.Results, "Summit reached.", true);
        }

        public void Configure(RunDirector runDirector, SteamSessionService steamSession,
            SteamNetworkCoordinator steamCoordinator, TransportSelector transportSelector,
            NetworkManager networkManager)
        {
            Unsubscribe();
            _runDirector = runDirector;
            _steamSession = steamSession;
            _steamCoordinator = steamCoordinator;
            _transportSelector = transportSelector;
            _networkManager = networkManager;
            EnsureReferences();
            Subscribe();
            Publish();
        }

        public bool PlayOffline()
        {
            if (!CanStartNewSession(_mode, _phase) || _runDirector == null)
                return false;

            _mode = SessionMode.Offline;
            SetPhysicsMode(PhysicsMode.Unity);
            Transition(SessionPhase.StartingOffline, "Starting offline hill...", true);
            _runDirector.BeginOfflineRun();
            if (!_runDirector.IsRunActive)
            {
                Fail("The offline hill could not start.", _runDirector.LastStartError);
                return false;
            }

            _localPlayerReady = true;
            Transition(SessionPhase.InRun, "Offline hill started.", false);
            return true;
        }

        public bool HostSteamFriends()
        {
            if (!CanStartNewSession(_mode, _phase) || _steamSession == null)
                return false;
            if (!SteamBootstrap.IsAvailable)
            {
                Fail(string.IsNullOrWhiteSpace(SteamBootstrap.FailureReason)
                    ? "Steam is unavailable."
                    : SteamBootstrap.FailureReason);
                return false;
            }

            _mode = SessionMode.Steam;
            SetPhysicsMode(PhysicsMode.TimeManager);
            Transition(SessionPhase.CreatingLobby, "Creating Steam friends lobby...", true);
            if (_steamSession.HostFriendsGame())
                return true;

            Fail("Steam could not begin creating a friends lobby.");
            return false;
        }

        public bool HostLocalDevelopment()
        {
            if (!CanStartNewSession(_mode, _phase) || _networkManager == null)
                return false;

            _mode = SessionMode.LocalDevelopment;
            SetPhysicsMode(PhysicsMode.TimeManager);
            Transition(SessionPhase.ConnectingTransport, "Starting local development host...", true);
            bool serverStarted = _networkManager.ServerManager.Started ||
                                 _networkManager.ServerManager.StartConnection();
            bool clientStarted = _networkManager.ClientManager.Started ||
                                 _networkManager.ClientManager.StartConnection();
            if (!serverStarted || !clientStarted)
            {
                StopLocalDevelopmentNetworking();
                Fail("The local development host could not start.");
                return false;
            }

            EvaluateLocalDevelopmentLobby();
            return true;
        }

        public bool JoinFriendSession(SteamFriendSessionInfo session)
        {
            if (!session.CanJoin || !CanStartNewSession(_mode, _phase) || _steamSession == null)
                return false;

            _mode = SessionMode.Steam;
            _clientAuthenticated = false;
            _localPlayerReady = false;
            SetPhysicsMode(PhysicsMode.TimeManager);
            Transition(SessionPhase.JoiningLobby, $"Joining {session.FriendName}'s Steam lobby...", true);
            if (_steamSession.JoinLobby(session.LobbyId, SteamJoinSource.FriendSession))
                return true;

            Fail("That Steam lobby could not be joined.");
            return false;
        }

        public bool AcceptPendingInvite()
        {
            if (_mode != SessionMode.None)
                return false;
            return BeginPendingInviteJoin();
        }

        public bool ConfirmPendingInviteSwitch()
        {
            if (_steamSession?.PendingInvite is not SteamLobbyInvite)
                return false;

            _intentionalLeave = true;
            EndCurrentRun();
            if (_mode == SessionMode.LocalDevelopment)
                StopLocalDevelopmentNetworking();
            else if (_mode == SessionMode.Steam)
                _steamCoordinator?.CancelConnection();
            _intentionalLeave = false;

            _mode = SessionMode.Steam;
            _clientAuthenticated = false;
            _localPlayerReady = false;
            SetPhysicsMode(PhysicsMode.TimeManager);
            Transition(SessionPhase.JoiningLobby, "Leaving the current session and joining the invitation...", true);
            if (_steamSession.AcceptPendingInvite())
                return true;

            Fail("The Steam invitation is no longer available.");
            return false;
        }

        public void DeclinePendingInvite()
        {
            _steamSession?.DeclinePendingInvite();
            Publish();
        }

        public bool StartHill()
        {
            if (_phase != SessionPhase.HostLobby || _runDirector == null || _networkManager == null)
                return false;

            Transition(SessionPhase.StartingRun, "Starting the hill for everyone...", true);
            if (_mode == SessionMode.Steam)
            {
                if (_steamSession == null || _steamCoordinator == null || !_steamSession.IsLobbyOwner ||
                    !_steamSession.CurrentLobby.IsValid())
                {
                    Fail("The Steam host lobby is no longer available.");
                    return false;
                }

                if (!_steamSession.SetLobbyRunState(PushUpConstants.LobbyStateStarting, true, "Starting PushUp"))
                {
                    Fail("Steam could not publish the start of the hill.");
                    return false;
                }

                if (_steamCoordinator.StartHostTransport(_steamSession.CurrentLobby))
                    return true;

                _steamSession.SetLobbyRunState(PushUpConstants.LobbyStateWaiting, true, "Hosting PushUp");
                Fail("The host transport could not start.", _steamCoordinator.StatusMessage);
                return false;
            }

            if (_mode != SessionMode.LocalDevelopment || !_networkManager.ServerManager.Started)
            {
                Fail("The local development host is not ready.");
                return false;
            }

            if (!_runDirector.BeginNetworkRun())
            {
                Fail("The host could not start the hill.", _runDirector.LastStartError);
                return false;
            }

            Transition(SessionPhase.InRun, "Hill started.", false);
            return true;
        }

        public void OpenPause()
        {
            if (_phase != SessionPhase.InRun || _menuVisible)
                return;
            _menuVisible = true;
            _message = _mode == SessionMode.Offline
                ? "Offline session menu open. Physics continues."
                : "Session menu open. Multiplayer simulation continues.";
            Publish();
        }

        public void Resume()
        {
            if (_phase != SessionPhase.InRun || !_menuVisible)
                return;
            _menuVisible = false;
            Publish();
        }

        public void RestartRun()
        {
            // NetworkRunState has no synchronized restart transition yet. Do not expose a host-only visual reset as
            // a multiplayer restart; it would leave clients in Complete.
            if (_phase != SessionPhase.Results || _mode != SessionMode.Offline || _runDirector == null)
                return;
            _runDirector.ResetBoulder();
            Transition(SessionPhase.InRun, "Run restarted.", false);
        }

        public void LeaveToMainMenu()
        {
            if (_phase == SessionPhase.MainMenu || _phase == SessionPhase.Leaving)
                return;

            _intentionalLeave = true;
            Transition(SessionPhase.Leaving, "Leaving session...", true);
            try
            {
                EndCurrentRun();
                switch (_mode)
                {
                    case SessionMode.Steam:
                        _steamCoordinator?.LeaveSession();
                        break;
                    case SessionMode.LocalDevelopment:
                        StopLocalDevelopmentNetworking();
                        break;
                }
            }
            finally
            {
                ResetToMain("Choose how to play.");
                _intentionalLeave = false;
            }
        }

        public void CancelCurrentOperation() => LeaveToMainMenu();

        public bool Retry()
        {
            if (_phase != SessionPhase.Error || _steamCoordinator == null || !_steamCoordinator.CanRetry)
                return false;
            _mode = SessionMode.Steam;
            _clientAuthenticated = false;
            _localPlayerReady = false;
            SetPhysicsMode(PhysicsMode.TimeManager);
            Transition(SessionPhase.JoiningLobby, "Rejoining the Steam session...", true);
            if (_steamCoordinator.RetryLastJoin())
                return true;
            Fail("The Steam session could not be retried.");
            return false;
        }

        public void ReturnAfterError()
        {
            if (_phase is not (SessionPhase.Error or SessionPhase.HostEnded))
                return;
            _intentionalLeave = true;
            try
            {
                EndCurrentRun();
                if (_mode == SessionMode.Steam)
                    _steamCoordinator?.ReturnAfterFailure();
                else if (_mode == SessionMode.LocalDevelopment)
                    StopLocalDevelopmentNetworking();
            }
            finally
            {
                ResetToMain("Choose how to play.");
                _intentionalLeave = false;
            }
        }

        public SteamFriendSessionInfo[] GetJoinableFriendSessions() =>
            _steamSession != null
                ? _steamSession.GetCachedJoinableFriendSessions()
                : Array.Empty<SteamFriendSessionInfo>();

        public SteamFriendSessionInfo[] RefreshJoinableFriendSessions() =>
            _steamSession != null
                ? _steamSession.RefreshJoinableFriendSessions()
                : Array.Empty<SteamFriendSessionInfo>();

        public SteamFriendInfo[] GetInviteCandidates() =>
            _steamSession != null ? _steamSession.GetInviteCandidates() : Array.Empty<SteamFriendInfo>();

        public bool InviteFriend(CSteamID friendId, out string status)
        {
            if (_steamSession == null)
            {
                status = "Steam session service is unavailable.";
                return false;
            }
            bool sent = _steamSession.InviteFriend(friendId, out status);
            _message = status;
            Publish();
            return sent;
        }

        public bool OpenInviteOverlay(out string status)
        {
            if (_steamSession == null)
            {
                status = "Steam session service is unavailable.";
                return false;
            }
            bool opened = _steamSession.OpenInviteOverlay(out status);
            _message = status;
            Publish();
            return opened;
        }

        public void ResetBoulder()
        {
            if (_phase != SessionPhase.InRun || _runDirector == null ||
                (_mode == SessionMode.Steam && (_steamSession == null || !_steamSession.IsLobbyOwner)))
                return;
            _runDirector.ResetBoulder();
            _message = "Boulder reset to the base.";
            Publish();
        }

        public static bool CanStartNewSession(SessionMode mode, SessionPhase phase) =>
            mode == SessionMode.None && phase is SessionPhase.MainMenu or SessionPhase.Error or SessionPhase.HostEnded;

        public static bool CanTransition(SessionPhase from, SessionPhase to)
        {
            if (from == to)
                return true;
            return from switch
            {
                SessionPhase.MainMenu => to is SessionPhase.StartingOffline or SessionPhase.CreatingLobby or
                    SessionPhase.JoiningLobby or SessionPhase.ConnectingTransport or SessionPhase.Error,
                SessionPhase.StartingOffline => to is SessionPhase.InRun or SessionPhase.Error or SessionPhase.Leaving,
                SessionPhase.CreatingLobby => to is SessionPhase.HostLobby or SessionPhase.Error or SessionPhase.Leaving,
                SessionPhase.JoiningLobby => to is SessionPhase.ConnectingTransport or SessionPhase.ClientLobby or
                    SessionPhase.Error or SessionPhase.HostEnded or SessionPhase.Leaving,
                SessionPhase.ConnectingTransport => to is SessionPhase.Authenticating or SessionPhase.WaitingForPlayer or
                    SessionPhase.HostLobby or SessionPhase.ClientLobby or SessionPhase.Error or
                    SessionPhase.HostEnded or SessionPhase.Leaving,
                SessionPhase.Authenticating => to is SessionPhase.WaitingForPlayer or SessionPhase.Error or
                    SessionPhase.HostEnded or SessionPhase.Leaving,
                SessionPhase.HostLobby => to is SessionPhase.StartingRun or SessionPhase.JoiningLobby or
                    SessionPhase.Error or SessionPhase.Leaving,
                SessionPhase.ClientLobby => to is SessionPhase.ConnectingTransport or SessionPhase.JoiningLobby or
                    SessionPhase.Error or SessionPhase.HostEnded or SessionPhase.Leaving,
                SessionPhase.WaitingForPlayer => to is SessionPhase.InRun or SessionPhase.Results or
                    SessionPhase.JoiningLobby or SessionPhase.Error or SessionPhase.HostEnded or SessionPhase.Leaving,
                SessionPhase.StartingRun => to is SessionPhase.HostLobby or SessionPhase.InRun or SessionPhase.Error or
                    SessionPhase.Leaving,
                SessionPhase.InRun => to is SessionPhase.Results or SessionPhase.JoiningLobby or
                    SessionPhase.ConnectingTransport or SessionPhase.Error or SessionPhase.HostEnded or
                    SessionPhase.Leaving,
                SessionPhase.Results => to is SessionPhase.InRun or SessionPhase.JoiningLobby or
                    SessionPhase.ConnectingTransport or SessionPhase.Error or SessionPhase.HostEnded or
                    SessionPhase.Leaving,
                SessionPhase.Leaving => to is SessionPhase.MainMenu or SessionPhase.Error,
                SessionPhase.Error => to is SessionPhase.MainMenu or SessionPhase.JoiningLobby or
                    SessionPhase.StartingOffline or SessionPhase.CreatingLobby or SessionPhase.HostEnded or
                    SessionPhase.Leaving,
                SessionPhase.HostEnded => to is SessionPhase.MainMenu or SessionPhase.JoiningLobby or SessionPhase.Leaving,
                _ => false
            };
        }

        public static bool ShouldEnableGameplay(SessionPhase phase, bool menuVisible) =>
            phase == SessionPhase.InRun && !menuVisible;

        public static bool IsClientReadyForGameplay(bool authenticated, bool ownedPlayerReady, bool runStateReady) =>
            authenticated && ownedPlayerReady && runStateReady;

        private bool BeginPendingInviteJoin()
        {
            if (_steamSession?.PendingInvite is not SteamLobbyInvite invite)
                return false;
            _mode = SessionMode.Steam;
            _clientAuthenticated = false;
            _localPlayerReady = false;
            SetPhysicsMode(PhysicsMode.TimeManager);
            Transition(SessionPhase.JoiningLobby, $"Joining {invite.InviterName}'s Steam lobby...", true);
            if (_steamSession.AcceptPendingInvite())
                return true;
            Fail("The Steam invitation is no longer available.");
            return false;
        }

        private void EnsureReferences()
        {
            _runDirector ??= GetComponent<RunDirector>();
            _steamSession ??= GetComponent<SteamSessionService>();
            _steamCoordinator ??= GetComponent<SteamNetworkCoordinator>();
            _transportSelector ??= GetComponent<TransportSelector>();
            _networkManager ??= GetComponent<NetworkManager>();
        }

        private void Subscribe()
        {
            if (_subscribed)
                return;
            if (_steamCoordinator != null)
            {
                _steamCoordinator.StatusChanged += OnCoordinatorStatusChanged;
                _steamCoordinator.HostTransportReady += OnHostTransportReady;
                _steamCoordinator.ClientTransportReady += OnClientTransportReady;
                _steamCoordinator.Cancelled += OnCoordinatorCancelled;
                _steamCoordinator.Disconnected += OnCoordinatorDisconnected;
            }
            if (_steamSession != null)
            {
                _steamSession.LobbyJoinStarted += OnLobbyJoinStarted;
                _steamSession.LobbyJoined += OnLobbyJoined;
                _steamSession.LobbySnapshotChanged += OnLobbySnapshotChanged;
                _steamSession.PendingInviteChanged += OnPendingInviteChanged;
                _steamSession.SessionError += OnSessionError;
                _steamSession.SessionListChanged += OnFriendSessionsChanged;
            }
            if (_networkManager != null)
            {
                _networkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;
                _networkManager.ServerManager.OnServerConnectionState += OnServerConnectionState;
            }
            if (_runDirector != null)
            {
                _runDirector.RunCompleted += OnRunCompleted;
                _runDirector.RunEnded += OnRunEnded;
            }
            _subscribed = true;
            EnsureObjectSubscription();
            EnsureRunStateSubscription();
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
                return;
            if (_steamCoordinator != null)
            {
                _steamCoordinator.StatusChanged -= OnCoordinatorStatusChanged;
                _steamCoordinator.HostTransportReady -= OnHostTransportReady;
                _steamCoordinator.ClientTransportReady -= OnClientTransportReady;
                _steamCoordinator.Cancelled -= OnCoordinatorCancelled;
                _steamCoordinator.Disconnected -= OnCoordinatorDisconnected;
            }
            if (_steamSession != null)
            {
                _steamSession.LobbyJoinStarted -= OnLobbyJoinStarted;
                _steamSession.LobbyJoined -= OnLobbyJoined;
                _steamSession.LobbySnapshotChanged -= OnLobbySnapshotChanged;
                _steamSession.PendingInviteChanged -= OnPendingInviteChanged;
                _steamSession.SessionError -= OnSessionError;
                _steamSession.SessionListChanged -= OnFriendSessionsChanged;
            }
            if (_networkManager != null)
            {
                _networkManager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
                _networkManager.ServerManager.OnServerConnectionState -= OnServerConnectionState;
                if (_objectEventsSubscribed && _networkManager.ClientManager.Objects != null)
                    _networkManager.ClientManager.Objects.OnSpawnedAdd -= OnClientObjectSpawned;
            }
            if (_runDirector != null)
            {
                _runDirector.RunCompleted -= OnRunCompleted;
                _runDirector.RunEnded -= OnRunEnded;
            }
            StopObservingRunState();
            _objectEventsSubscribed = false;
            _subscribed = false;
        }

        private void EnsureObjectSubscription()
        {
            if (_objectEventsSubscribed || _networkManager?.ClientManager.Objects == null)
                return;
            _networkManager.ClientManager.Objects.OnSpawnedAdd += OnClientObjectSpawned;
            _objectEventsSubscribed = true;
        }

        private void EnsureRunStateSubscription()
        {
            NetworkRunState active = NetworkRunState.Active;
            if (_observedRunState == active)
                return;
            StopObservingRunState();
            _observedRunState = active;
            if (_observedRunState == null)
                return;
            _observedRunState.Changed += OnNetworkRunStateChanged;
            OnNetworkRunStateChanged(_observedRunState);
        }

        private void StopObservingRunState()
        {
            if (_observedRunState != null)
                _observedRunState.Changed -= OnNetworkRunStateChanged;
            _observedRunState = null;
        }

        private void OnCoordinatorStatusChanged(SteamConnectionStatus status)
        {
            _diagnostic = status.Diagnostic ?? string.Empty;
            switch (status.Phase)
            {
                case SteamConnectionPhase.InvitationReceived:
                    _message = status.Message;
                    Publish();
                    break;
                case SteamConnectionPhase.JoiningLobby:
                    _mode = SessionMode.Steam;
                    _clientAuthenticated = false;
                    _localPlayerReady = false;
                    Transition(SessionPhase.JoiningLobby, status.Message, true);
                    break;
                case SteamConnectionPhase.LobbyReady:
                    _mode = SessionMode.Steam;
                    Transition(SessionPhase.HostLobby, status.Message, true);
                    break;
                case SteamConnectionPhase.ClientLobbyReady:
                    _mode = SessionMode.Steam;
                    _clientAuthenticated = false;
                    Transition(SessionPhase.ClientLobby, status.Message, true);
                    break;
                case SteamConnectionPhase.Connecting:
                    if (_phase == SessionPhase.StartingRun && IsLocalHost())
                    {
                        _message = status.Message;
                        Publish();
                    }
                    else
                        Transition(SessionPhase.ConnectingTransport, status.Message, true);
                    break;
                case SteamConnectionPhase.Authenticating:
                    Transition(SessionPhase.Authenticating, status.Message, true);
                    break;
                case SteamConnectionPhase.HostTransportReady:
                    _message = status.Message;
                    Publish();
                    break;
                case SteamConnectionPhase.Connected:
                    _mode = SessionMode.Steam;
                    _clientAuthenticated = true;
                    if (_phase != SessionPhase.InRun && _phase != SessionPhase.Results)
                        Transition(SessionPhase.WaitingForPlayer, status.Message, true);
                    EvaluateClientGameplayReady();
                    break;
                case SteamConnectionPhase.Cancelled:
                    if (_intentionalLeave)
                        break;
                    if (_phase == SessionPhase.StartingRun && IsLocalHost())
                        Transition(SessionPhase.HostLobby, status.Message, true);
                    else
                    {
                        _message = status.Message;
                        Publish();
                    }
                    break;
                case SteamConnectionPhase.HostEnded:
                    if (!_intentionalLeave)
                        Transition(SessionPhase.HostEnded, status.Message, true);
                    break;
                case SteamConnectionPhase.Failed:
                    if (!_intentionalLeave)
                        Fail(status.Message, status.Diagnostic);
                    break;
                case SteamConnectionPhase.Idle when _phase == SessionPhase.Leaving:
                    ResetToMain(status.Message);
                    break;
                default:
                    _message = status.Message;
                    Publish();
                    break;
            }
        }

        private void OnHostTransportReady()
        {
            if (_mode != SessionMode.Steam || _phase != SessionPhase.StartingRun || !IsLocalHost())
                return;

            if (_runDirector == null || !_runDirector.BeginNetworkRun())
            {
                string diagnostic = _runDirector != null ? _runDirector.LastStartError : "Run director is unavailable.";
                _steamCoordinator?.CancelConnection();
                _steamSession?.SetLobbyRunState(PushUpConstants.LobbyStateWaiting, true, "Hosting PushUp");
                Fail("The host could not start the hill.", diagnostic);
                return;
            }

            if (_steamSession == null ||
                !_steamSession.SetLobbyRunState(PushUpConstants.LobbyStateRunning, true, "Playing PushUp"))
            {
                _runDirector.EndRun();
                _steamCoordinator?.CancelConnection();
                _steamSession?.SetLobbyRunState(PushUpConstants.LobbyStateWaiting, true, "Hosting PushUp");
                Fail("The hill started locally, but Steam could not publish the running session.");
                return;
            }

            Transition(SessionPhase.InRun, "Hill started.", false);
        }

        private void OnClientTransportReady()
        {
            if (_mode != SessionMode.Steam || IsLocalHost())
                return;
            _clientAuthenticated = true;
            if (_phase != SessionPhase.InRun && _phase != SessionPhase.Results &&
                _phase != SessionPhase.WaitingForPlayer)
                Transition(SessionPhase.WaitingForPlayer, "Authenticated. Waiting for the hill and player spawn...", true);
            EvaluateClientGameplayReady();
        }

        private void OnCoordinatorCancelled()
        {
            if (_intentionalLeave)
                return;
            _clientAuthenticated = false;
            _localPlayerReady = false;
            if (_phase == SessionPhase.StartingRun && IsLocalHost())
                Transition(SessionPhase.HostLobby, "Host start cancelled. You can try Start Hill again.", true);
            else
                Publish();
        }

        private void OnCoordinatorDisconnected(string message, string diagnostic)
        {
            if (_intentionalLeave)
                return;
            _clientAuthenticated = false;
            _localPlayerReady = false;
            _diagnostic = diagnostic ?? string.Empty;
            if (_steamCoordinator != null && _steamCoordinator.Phase == SteamConnectionPhase.HostEnded)
                Transition(SessionPhase.HostEnded, message, true);
            else
                Fail(message, diagnostic);
        }

        private void OnLobbyJoinStarted(CSteamID _)
        {
            EndCurrentRun();
            _mode = SessionMode.Steam;
            _clientAuthenticated = false;
            _localPlayerReady = false;
            SetPhysicsMode(PhysicsMode.TimeManager);
            Transition(SessionPhase.JoiningLobby, "Joining Steam lobby...", true);
        }

        private void OnLobbyJoined(CSteamID _)
        {
            if (_steamSession != null)
                OnLobbySnapshotChanged(_steamSession.GetCurrentLobbySnapshot());
        }

        private void OnLobbySnapshotChanged(SteamLobbySnapshot snapshot)
        {
            string[] names = new string[snapshot.MemberCount];
            for (int index = 0; index < names.Length; index++)
            {
                SteamLobbyMemberInfo member = snapshot.Members[index];
                names[index] = member.Name + (member.IsOwner ? " (Host)" : string.Empty);
            }

            string roster = string.Join("\n", names);
            if (roster == _roster && snapshot.MemberCount == _memberCount && snapshot.Capacity == _capacity)
                return;
            _roster = roster;
            _memberCount = snapshot.MemberCount;
            _capacity = snapshot.Capacity;
            Publish();
        }

        private void OnPendingInviteChanged(SteamLobbyInvite? invite)
        {
            if (invite.HasValue && _phase == SessionPhase.MainMenu)
                _message = $"Invitation received from {invite.Value.InviterName}.";
            Publish();
        }

        private void OnFriendSessionsChanged() => FriendSessionsChanged?.Invoke();

        private void OnSessionError(string message)
        {
            if (_intentionalLeave)
                return;
            if (message.IndexOf("host ended", StringComparison.OrdinalIgnoreCase) >= 0)
                Transition(SessionPhase.HostEnded, message, true);
            else if (_phase != SessionPhase.Error)
                Fail(message);
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                if (_mode == SessionMode.LocalDevelopment)
                    EvaluateLocalDevelopmentLobby();
                return;
            }

            if (args.ConnectionState != LocalConnectionState.Stopped || _intentionalLeave)
                return;
            _clientAuthenticated = false;
            _localPlayerReady = false;
            if (_mode == SessionMode.Steam &&
                _phase is SessionPhase.ClientLobby or SessionPhase.ConnectingTransport or SessionPhase.Authenticating or
                    SessionPhase.WaitingForPlayer or SessionPhase.InRun or SessionPhase.Results)
            {
                // SteamNetworkCoordinator distinguishes a recoverable transport interruption from the original
                // lobby owner actually leaving. Do not race it by labeling every stopped socket as HostEnded.
                Transition(SessionPhase.ConnectingTransport,
                    "Connection interrupted. Checking whether the Steam lobby is still available...", true);
            }
            else if (_mode == SessionMode.LocalDevelopment && _phase != SessionPhase.MainMenu)
                Fail("The local development client stopped.");
        }

        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                if (_mode == SessionMode.LocalDevelopment)
                    EvaluateLocalDevelopmentLobby();
                return;
            }

            if (args.ConnectionState == LocalConnectionState.Stopped && !_intentionalLeave &&
                _phase is SessionPhase.HostLobby or SessionPhase.StartingRun or SessionPhase.InRun or SessionPhase.Results)
                Fail("The host server stopped.");
        }

        private void OnClientObjectSpawned(int _, NetworkObject networkObject)
        {
            if (networkObject == null)
                return;
            if (networkObject.GetComponent<NetworkRunState>() != null)
                EnsureRunStateSubscription();
            if (!networkObject.Owner.IsLocalClient || networkObject.GetComponent<PlayerMotor>() == null)
                return;
            // FishNet invokes OnSpawnedAdd after InitializeEarly assigns Owner but before
            // client initialization completes. IsOwner is deliberately false at that
            // point, so retain the candidate and confirm it on a later frame.
            _pendingOwnedPlayer = networkObject;
            RefreshLocalPlayerReadiness();
        }

        private void RefreshLocalPlayerReadiness()
        {
            if (_localPlayerReady || !_clientAuthenticated || _mode != SessionMode.Steam || IsLocalHost() ||
                _networkManager?.ClientManager.Objects == null)
                return;

            if (IsOwnedPlayerReady(_pendingOwnedPlayer))
            {
                MarkLocalPlayerReady();
                return;
            }

            _pendingOwnedPlayer = null;
            foreach (NetworkObject candidate in _networkManager.ClientManager.Objects.Spawned.Values)
            {
                if (candidate == null || candidate.GetComponent<PlayerMotor>() == null ||
                    !candidate.Owner.IsLocalClient)
                    continue;
                _pendingOwnedPlayer = candidate;
                if (IsOwnedPlayerReady(candidate))
                    MarkLocalPlayerReady();
                return;
            }
        }

        private void MarkLocalPlayerReady()
        {
            _localPlayerReady = true;
            _playerReadyDeadline = -1f;
            EvaluateClientGameplayReady();
        }

        private static bool IsOwnedPlayerReady(NetworkObject candidate) => candidate != null &&
            candidate.IsSpawned && candidate.IsClientInitialized && candidate.Owner.IsLocalClient &&
            candidate.GetComponent<PlayerMotor>() != null;

        public static bool IsOwnedPlayerSpawnReady(bool ownerIsLocal, bool clientInitialized, bool hasPlayerMotor) =>
            ownerIsLocal && clientInitialized && hasPlayerMotor;

        private void OnNetworkRunStateChanged(NetworkRunState runState)
        {
            if (runState == null)
                return;
            if (runState.Phase == NetworkRunPhase.Complete && _phase == SessionPhase.InRun)
            {
                Transition(SessionPhase.Results, "Summit reached.", true);
                return;
            }
            if (runState.Phase == NetworkRunPhase.Ending && !_intentionalLeave &&
                _mode == SessionMode.Steam && !IsLocalHost())
            {
                Transition(SessionPhase.HostEnded, "The host ended the session.", true);
                return;
            }
            EvaluateClientGameplayReady();
        }

        private void EvaluateClientGameplayReady()
        {
            if (_mode != SessionMode.Steam || IsLocalHost())
            {
                Publish();
                return;
            }

            bool runReady = _observedRunState != null && _observedRunState.IsReady;
            if (!IsClientReadyForGameplay(_clientAuthenticated, _localPlayerReady, runReady))
            {
                if (!_clientAuthenticated)
                {
                    if (_phase is SessionPhase.ConnectingTransport or SessionPhase.Authenticating)
                    {
                        _message = "Authenticating with host...";
                        Publish();
                    }
                    return;
                }

                if (_phase is SessionPhase.ClientLobby or SessionPhase.ConnectingTransport or
                    SessionPhase.Authenticating or SessionPhase.WaitingForPlayer)
                {
                    string waitingFor = !runReady ? "the hill to replicate" : "your player to spawn";
                    Transition(SessionPhase.WaitingForPlayer, $"Connected. Waiting for {waitingFor}...", true);
                }
                else
                    Publish();
                return;
            }

            if (_observedRunState.Phase == NetworkRunPhase.Complete)
                Transition(SessionPhase.Results, "Summit reached.", true);
            else if (_phase != SessionPhase.InRun)
                Transition(SessionPhase.InRun, "Joined the hill.", false);
        }

        private void OnRunCompleted()
        {
            if (_phase == SessionPhase.InRun)
                Transition(SessionPhase.Results, "Summit reached.", true);
        }

        private void OnRunEnded()
        {
            if (_intentionalLeave || _phase is SessionPhase.Leaving or SessionPhase.MainMenu or SessionPhase.Error or
                SessionPhase.HostEnded)
                return;
            if (_mode == SessionMode.Steam && !IsLocalHost())
                Transition(SessionPhase.HostEnded, "The host ended the session.", true);
        }

        private void EvaluateLocalDevelopmentLobby()
        {
            if (_mode != SessionMode.LocalDevelopment || _networkManager == null)
                return;
            if (!_networkManager.ServerManager.Started || !_networkManager.ClientManager.Started)
                return;
            _roster = "Local Host";
            _memberCount = 1;
            _capacity = 4;
            Transition(SessionPhase.HostLobby, "Local development lobby ready. Start the hill when ready.", true);
        }

        private void StopLocalDevelopmentNetworking()
        {
            if (_networkManager == null)
                return;
            if (_networkManager.ClientManager.Started)
                _networkManager.ClientManager.StopConnection();
            if (_networkManager.ServerManager.Started)
                _networkManager.ServerManager.StopConnection(true);
        }

        private void EndCurrentRun()
        {
            if (_runDirector != null && _runDirector.IsRunActive)
                _runDirector.EndRun();
        }

        private bool IsLocalHost() =>
            _mode == SessionMode.LocalDevelopment ||
            (_mode == SessionMode.Steam && _steamSession != null && _steamSession.IsLobbyOwner);

        private void Fail(string message, string diagnostic = "")
        {
            _diagnostic = diagnostic ?? string.Empty;
            Transition(SessionPhase.Error, string.IsNullOrWhiteSpace(message) ? "Session failed." : message, true);
        }

        private void ResetToMain(string message)
        {
            SetPhysicsMode(PhysicsMode.Unity);
            StopObservingRunState();
            _mode = SessionMode.None;
            _phase = SessionPhase.MainMenu;
            _message = string.IsNullOrWhiteSpace(message) ? "Choose how to play." : message;
            _diagnostic = string.Empty;
            _menuVisible = true;
            _localPlayerReady = false;
            _pendingOwnedPlayer = null;
            _playerReadyDeadline = -1f;
            _clientAuthenticated = false;
            _roster = string.Empty;
            _memberCount = 0;
            _capacity = 0;
            Publish();
        }

        private void Transition(SessionPhase phase, string message, bool menuVisible)
        {
            SessionPhase previous = _phase;
            if (!CanTransition(_phase, phase))
                Debug.LogWarning($"Session flow recovered from unexpected transition {_phase} -> {phase}.");
            _phase = phase;
            if (phase == SessionPhase.WaitingForPlayer && previous != SessionPhase.WaitingForPlayer)
                _playerReadyDeadline = Time.unscaledTime + PlayerSpawnTimeoutSeconds;
            else if (phase != SessionPhase.WaitingForPlayer)
                _playerReadyDeadline = -1f;
            _message = message ?? string.Empty;
            _menuVisible = menuVisible;
            Publish();
        }

        private void Publish()
        {
            bool isHost = IsLocalHost();
            SteamLobbyInvite? invite = _steamSession != null ? _steamSession.PendingInvite : null;
            bool runComplete = _runDirector != null && _runDirector.IsComplete ||
                               _observedRunState != null && _observedRunState.Phase == NetworkRunPhase.Complete;
            _snapshot = new SessionSnapshot(_mode, _phase, _message, _diagnostic, _menuVisible, isHost,
                SteamBootstrap.IsAvailable, _transportSelector == null || _transportSelector.UsesSteamTransport,
                _localPlayerReady, _roster, _memberCount, _capacity, invite.HasValue,
                invite.HasValue ? invite.Value.InviterName : string.Empty,
                _steamCoordinator != null && _steamCoordinator.CanRetry, runComplete);

            bool gameplayEnabled = ShouldEnableGameplay(_phase, _menuVisible);
            PlayerInputReader.SetGameplayEnabled(gameplayEnabled);
            Cursor.lockState = gameplayEnabled ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !gameplayEnabled;
            SnapshotChanged?.Invoke(_snapshot);
        }

        private void SetPhysicsMode(PhysicsMode mode)
        {
            if (_networkManager != null && _networkManager.TimeManager != null)
                _networkManager.TimeManager.SetPhysicsMode(mode);
        }
    }
}
