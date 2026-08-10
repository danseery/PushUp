using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FishNet.Managing;
using FishNet.Managing.Transporting;
using FishNet.Transporting;
using FishNet.Utility.Performance;
using PushUp.Steam;
using Steamworks;
using Unity.Profiling;
using UnityEngine;

namespace PushUp.Networking
{
    public readonly struct NetworkDiagnosticsSnapshot
    {
        public NetworkDiagnosticsSnapshot(bool hasConnectionStatus, int roundTripTimeMs,
            float localQuality, float remoteQuality, int pendingReliableBytes, int pendingUnreliableBytes,
            long sentMessages, long receivedMessages, long sentBytes, long receivedBytes,
            int failedSends, int oversizeDrops, int backlogLimitHits)
        {
            HasConnectionStatus = hasConnectionStatus;
            RoundTripTimeMs = roundTripTimeMs;
            LocalQuality = localQuality;
            RemoteQuality = remoteQuality;
            PendingReliableBytes = pendingReliableBytes;
            PendingUnreliableBytes = pendingUnreliableBytes;
            SentMessages = sentMessages;
            ReceivedMessages = receivedMessages;
            SentBytes = sentBytes;
            ReceivedBytes = receivedBytes;
            FailedSends = failedSends;
            OversizeDrops = oversizeDrops;
            BacklogLimitHits = backlogLimitHits;
        }

        public bool HasConnectionStatus { get; }
        public int RoundTripTimeMs { get; }
        public float LocalQuality { get; }
        public float RemoteQuality { get; }
        public int PendingReliableBytes { get; }
        public int PendingUnreliableBytes { get; }
        public long SentMessages { get; }
        public long ReceivedMessages { get; }
        public long SentBytes { get; }
        public long ReceivedBytes { get; }
        public int FailedSends { get; }
        public int OversizeDrops { get; }
        public int BacklogLimitHits { get; }
    }

    [AddComponentMenu("PushUp/Networking/Steam Sockets Transport")]
    [DisallowMultipleComponent]
    public sealed class SteamSocketsTransport : Transport
    {
        public enum InboundConnectionDecision : byte
        {
            WaitForLobbyMembership,
            Accept,
            ReplaceExistingIdentity,
            RejectNotLobbyMember,
            RejectLobbyFull
        }

        private const int MaximumClients = 3;
        private const int MaximumMessagesPerPoll = 64;
        private const int MaximumReceiveBatchesPerIteration = 4;
        private const int MaximumPendingInboundConnections = MaximumClients * 2;
        private const int MaximumWirePayload = 1200;
        private const int ChannelTrailerSize = 1;
        private const int VirtualPort = 0;
        public const float LobbyMembershipGraceSeconds = 2f;
        public const float LobbyDepartureRecheckSeconds = 0.25f;
        public const int FishNetPayloadMtu = MaximumWirePayload - ChannelTrailerSize;

        private readonly struct PendingInboundConnection
        {
            public PendingInboundConnection(ulong steamId, float expiresAt)
            {
                SteamId = steamId;
                ExpiresAt = expiresAt;
            }

            public ulong SteamId { get; }
            public float ExpiresAt { get; }
        }

        [SerializeField] private string _hostSteamId = string.Empty;

        private readonly Dictionary<int, HSteamNetConnection> _connectionsById = new();
        private readonly Dictionary<HSteamNetConnection, int> _idsByConnection = new();
        private readonly Dictionary<HSteamNetConnection, ulong> _steamIdsByConnection = new();
        private readonly Dictionary<ulong, HSteamNetConnection> _connectionsBySteamId = new();
        private readonly Dictionary<HSteamNetConnection, PendingInboundConnection> _pendingInboundConnections = new();
        private readonly Dictionary<ulong, HSteamNetConnection> _pendingConnectionsBySteamId = new();
        private readonly Dictionary<ulong, float> _departedMembershipChecks = new();
        private readonly List<KeyValuePair<int, HSteamNetConnection>> _receiveConnections = new(MaximumClients);
        private readonly List<HSteamNetConnection> _pendingConnectionSnapshot = new(MaximumClients);
        private readonly List<ulong> _departedSteamIdSnapshot = new(MaximumClients);
        private readonly IntPtr[] _messagePointers = new IntPtr[MaximumMessagesPerPoll];
        private static readonly ProfilerMarker SendMarker = new("PushUp.Steam.Send");
        private static readonly ProfilerMarker ReceiveMarker = new("PushUp.Steam.Receive");

        private Callback<SteamNetConnectionStatusChangedCallback_t> _connectionCallback;
        private HSteamListenSocket _listenSocket = HSteamListenSocket.Invalid;
        private HSteamNetConnection _clientConnection = HSteamNetConnection.Invalid;
        private IntPtr _sendBuffer = IntPtr.Zero;
        private LocalConnectionState _serverState = LocalConnectionState.Stopped;
        private LocalConnectionState _clientState = LocalConnectionState.Stopped;
        private int _nextConnectionId;
        private float _nextDiagnosticSampleAt;
        private float _nextBacklogWarningAt;
        private int _diagnosticRttMs;
        private float _diagnosticLocalQuality;
        private float _diagnosticRemoteQuality;
        private int _diagnosticPendingReliable;
        private int _diagnosticPendingUnreliable;
        private bool _hasConnectionDiagnostics;
        private SteamSessionService _session;
        private bool _sessionSubscribed;

        public override event Action<ClientConnectionStateArgs> OnClientConnectionState;
        public override event Action<ServerConnectionStateArgs> OnServerConnectionState;
        public override event Action<RemoteConnectionStateArgs> OnRemoteConnectionState;
        public override event Action<ClientReceivedDataArgs> OnClientReceivedData;
        public override event Action<ServerReceivedDataArgs> OnServerReceivedData;
        public event Action<string> ClientProblem;
        public string LastClientError { get; private set; } = string.Empty;
        public long SentMessageCount { get; private set; }
        public long ReceivedMessageCount { get; private set; }
        public long SentByteCount { get; private set; }
        public long ReceivedByteCount { get; private set; }
        public int FailedSendCount { get; private set; }
        public int DroppedOversizeCount { get; private set; }
        public int ReceiveBacklogLimitHitCount { get; private set; }
        public NetworkDiagnosticsSnapshot Diagnostics => new(_hasConnectionDiagnostics,
            _diagnosticRttMs, _diagnosticLocalQuality, _diagnosticRemoteQuality, _diagnosticPendingReliable,
            _diagnosticPendingUnreliable, SentMessageCount, ReceivedMessageCount, SentByteCount, ReceivedByteCount, FailedSendCount,
            DroppedOversizeCount, ReceiveBacklogLimitHitCount);

        public override void Initialize(NetworkManager networkManager, int transportIndex)
        {
            base.Initialize(networkManager, transportIndex);
            EnsureSessionSubscription();
        }

        private void OnDestroy()
        {
            RemoveSessionSubscription();
            Shutdown();
        }

        public override string GetConnectionAddress(int connectionId)
        {
            if (!_connectionsById.TryGetValue(connectionId, out HSteamNetConnection connection))
                return string.Empty;
            return _steamIdsByConnection.TryGetValue(connection, out ulong steamId)
                ? steamId.ToString()
                : GetRemoteSteamId(connection).ToString();
        }

        public override void HandleClientConnectionState(ClientConnectionStateArgs connectionStateArgs)
        {
            OnClientConnectionState?.Invoke(connectionStateArgs);
        }

        public override void HandleServerConnectionState(ServerConnectionStateArgs connectionStateArgs)
        {
            OnServerConnectionState?.Invoke(connectionStateArgs);
        }

        public override void HandleRemoteConnectionState(RemoteConnectionStateArgs connectionStateArgs)
        {
            OnRemoteConnectionState?.Invoke(connectionStateArgs);
        }

        public override void HandleClientReceivedDataArgs(ClientReceivedDataArgs receivedDataArgs)
        {
            OnClientReceivedData?.Invoke(receivedDataArgs);
        }

        public override void HandleServerReceivedDataArgs(ServerReceivedDataArgs receivedDataArgs)
        {
            OnServerReceivedData?.Invoke(receivedDataArgs);
        }

        public override LocalConnectionState GetConnectionState(bool server)
        {
            return server ? _serverState : _clientState;
        }

        public override RemoteConnectionState GetConnectionState(int connectionId)
        {
            return _connectionsById.ContainsKey(connectionId) ? RemoteConnectionState.Started : RemoteConnectionState.Stopped;
        }

        public override int GetMaximumClients() => MaximumClients;

        public override void SetClientAddress(string address)
        {
            _hostSteamId = address;
        }

        public override string GetClientAddress() => _hostSteamId;

        public override bool StartConnection(bool server)
        {
            return server ? StartServer() : StartClient();
        }

        public override bool StopConnection(bool server)
        {
            return server ? StopServer() : StopClient();
        }

        public override bool StopConnection(int connectionId, bool immediately)
        {
            if (!_connectionsById.TryGetValue(connectionId, out HSteamNetConnection connection))
                return false;

            SteamNetworkingSockets.CloseConnection(connection, 0, "Host disconnected", !immediately);
            RemoveRemoteConnection(connection);
            return true;
        }

        public override void Shutdown()
        {
            StopConnection(false);
            StopConnection(true);
            DisposeCallback();
            ReleaseSendBuffer();
        }

        public override void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            Send(_clientConnection, channelId, segment);
        }

        public override void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
        {
            if (_connectionsById.TryGetValue(connectionId, out HSteamNetConnection connection))
                Send(connection, channelId, segment);
        }

        public override void IterateIncoming(bool asServer)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            SampleConnectionDiagnostics();
#endif
            if (asServer)
            {
                ProcessDepartedLobbyMembers(Time.unscaledTime);
                ProcessPendingInboundConnections(Time.unscaledTime);
                // FishNet may synchronously stop a connection while parsing a received packet. Iterate a stable,
                // allocation-free snapshot so that cleanup cannot invalidate the dictionary enumerator.
                _receiveConnections.Clear();
                foreach (KeyValuePair<int, HSteamNetConnection> entry in _connectionsById)
                    _receiveConnections.Add(entry);
                for (int index = 0; index < _receiveConnections.Count; index++)
                {
                    KeyValuePair<int, HSteamNetConnection> entry = _receiveConnections[index];
                    if (_connectionsById.TryGetValue(entry.Key, out HSteamNetConnection current) &&
                        current == entry.Value)
                        Receive(entry.Value, entry.Key, true);
                }
            }
            else if (_clientConnection != HSteamNetConnection.Invalid)
            {
                Receive(_clientConnection, -1, false);
            }
        }

        public override void IterateOutgoing(bool asServer)
        {
            // FishNet already batches writes per tick and Send uses NoNagle, so there is no second native flush pass.
        }

        public override int GetMTU(byte channel) => FishNetPayloadMtu;

        public static bool IsPayloadWithinMtu(int count) => count >= 0 && count <= FishNetPayloadMtu;

        public static InboundConnectionDecision DecideInboundConnection(bool isLobbyMember,
            bool identityAlreadyReserved, int reservedConnectionCount, float now, float membershipDeadline)
        {
            if (!isLobbyMember)
                return now < membershipDeadline
                    ? InboundConnectionDecision.WaitForLobbyMembership
                    : InboundConnectionDecision.RejectNotLobbyMember;
            if (identityAlreadyReserved)
                return InboundConnectionDecision.ReplaceExistingIdentity;
            return reservedConnectionCount >= MaximumClients
                ? InboundConnectionDecision.RejectLobbyFull
                : InboundConnectionDecision.Accept;
        }

        public static float PreserveMembershipDeadline(float existingDeadline, float now) =>
            existingDeadline > 0f
                ? Mathf.Min(existingDeadline, now + LobbyMembershipGraceSeconds)
                : now + LobbyMembershipGraceSeconds;

        public static bool CanQueuePendingIdentity(int pendingIdentityCount, bool replacesPendingIdentity) =>
            replacesPendingIdentity || pendingIdentityCount < MaximumPendingInboundConnections;

        private bool StartServer()
        {
            EnsureSessionSubscription();
            if (!EnsureSteam())
                return false;
            if (_serverState != LocalConnectionState.Stopped)
                return false;

            EnsureCallback();
            _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, Array.Empty<SteamNetworkingConfigValue_t>());
            if (_listenSocket == HSteamListenSocket.Invalid)
            {
                NetworkManager?.LogError("Steam Networking Sockets could not create the host listen socket.");
                return false;
            }

            SetServerState(LocalConnectionState.Started);
            return true;
        }

        private bool StartClient()
        {
            LastClientError = string.Empty;
            if (!EnsureSteam() || _clientState != LocalConnectionState.Stopped)
                return false;
            if (!ulong.TryParse(_hostSteamId, out ulong steamId))
            {
                ReportClientProblem("Steam client address must be the host's 64-bit Steam ID.");
                return false;
            }

            EnsureCallback();
            SteamNetworkingIdentity identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(steamId));
            _clientConnection = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, Array.Empty<SteamNetworkingConfigValue_t>());
            if (_clientConnection == HSteamNetConnection.Invalid)
            {
                ReportClientProblem($"Steam ConnectP2P returned an invalid connection for host {steamId}.");
                return false;
            }

            SetClientState(LocalConnectionState.Starting);
            return true;
        }

        private bool StopServer()
        {
            if (_serverState == LocalConnectionState.Stopped)
                return false;

            foreach (HSteamNetConnection connection in _connectionsById.Values)
                SteamNetworkingSockets.CloseConnection(connection, 0, "Host stopped", false);
            foreach (HSteamNetConnection connection in _pendingInboundConnections.Keys)
                SteamNetworkingSockets.CloseConnection(connection, 0, "Host stopped", false);
            foreach (HSteamNetConnection connection in _steamIdsByConnection.Keys)
            {
                if (!_idsByConnection.ContainsKey(connection))
                    SteamNetworkingSockets.CloseConnection(connection, 0, "Host stopped", false);
            }
            _connectionsById.Clear();
            _idsByConnection.Clear();
            _steamIdsByConnection.Clear();
            _connectionsBySteamId.Clear();
            _pendingInboundConnections.Clear();
            _pendingConnectionsBySteamId.Clear();
            _departedMembershipChecks.Clear();
            _nextConnectionId = 0;

            if (_listenSocket != HSteamListenSocket.Invalid)
                SteamNetworkingSockets.CloseListenSocket(_listenSocket);
            _listenSocket = HSteamListenSocket.Invalid;
            SetServerState(LocalConnectionState.Stopped);
            return true;
        }

        private bool StopClient()
        {
            if (_clientState == LocalConnectionState.Stopped)
                return false;

            if (_clientConnection != HSteamNetConnection.Invalid)
                SteamNetworkingSockets.CloseConnection(_clientConnection, 0, "Client stopped", false);
            _clientConnection = HSteamNetConnection.Invalid;
            SetClientState(LocalConnectionState.Stopped);
            return true;
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t callback)
        {
            bool clientConnection = callback.m_hConn == _clientConnection;
            bool inbound = _listenSocket != HSteamListenSocket.Invalid &&
                           callback.m_info.m_hListenSocket == _listenSocket;
            ESteamNetworkingConnectionState state = callback.m_info.m_eState;

            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting && inbound)
            {
                ulong remoteSteamId = callback.m_info.m_identityRemote.GetSteamID64();
                HandleInboundConnecting(callback.m_hConn, remoteSteamId, Time.unscaledTime);
                return;
            }

            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
            {
                if (clientConnection)
                    SetClientState(LocalConnectionState.Started);
                else if (inbound)
                    AddRemoteConnection(callback.m_hConn, callback.m_info.m_identityRemote.GetSteamID64());
                return;
            }

            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ||
                state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
            {
                if (clientConnection)
                {
                    string diagnostic = $"reason={callback.m_info.m_eEndReason}, debug={callback.m_info.m_szEndDebug}";
                    SteamNetworkingSockets.CloseConnection(callback.m_hConn, 0, string.Empty, false);
                    _clientConnection = HSteamNetConnection.Invalid;
                    LastClientError = diagnostic;
                    NetworkManager?.LogError($"Steam Networking Sockets: {LastClientError}");
                    SetClientState(LocalConnectionState.Stopped);
                    ClientProblem?.Invoke(LastClientError);
                }

                if (_idsByConnection.ContainsKey(callback.m_hConn))
                {
                    SteamNetworkingSockets.CloseConnection(callback.m_hConn, 0, string.Empty, false);
                    RemoveRemoteConnection(callback.m_hConn);
                }
                else
                    RemoveInboundReservation(callback.m_hConn);
            }
        }

        private void HandleInboundConnecting(HSteamNetConnection connection, ulong steamId, float now)
        {
            if (steamId == 0)
            {
                RejectInboundConnection(connection, steamId, "Steam identity is invalid");
                return;
            }
            if (_steamIdsByConnection.TryGetValue(connection, out ulong admittedSteamId))
            {
                if (admittedSteamId != steamId)
                    RejectInboundConnection(connection, steamId, "Steam identity changed during admission");
                return;
            }

            if (!_pendingInboundConnections.TryGetValue(connection, out PendingInboundConnection pending))
            {
                float existingDeadline = 0f;
                bool replacesPendingIdentity = false;
                if (_pendingConnectionsBySteamId.TryGetValue(steamId, out HSteamNetConnection olderPending) &&
                    olderPending != connection)
                {
                    replacesPendingIdentity = true;
                    if (_pendingInboundConnections.TryGetValue(olderPending,
                        out PendingInboundConnection olderPendingState))
                        existingDeadline = olderPendingState.ExpiresAt;
                    SteamNetworkingSockets.CloseConnection(olderPending, 0,
                        "Superseded by a newer connection", false);
                    RemovePendingInboundConnection(olderPending);
                }

                if (!CanQueuePendingIdentity(_pendingInboundConnections.Count, replacesPendingIdentity))
                {
                    RejectInboundConnection(connection, steamId,
                        "Too many connections are waiting for lobby membership");
                    return;
                }

                pending = new PendingInboundConnection(steamId,
                    PreserveMembershipDeadline(existingDeadline, now));
                _pendingInboundConnections.Add(connection, pending);
                _pendingConnectionsBySteamId[steamId] = connection;
            }

            bool isLobbyMember = _session != null && _session.IsCurrentLobbyMember(steamId);
            if (isLobbyMember)
                _departedMembershipChecks.Remove(steamId);
            ResolvePendingInboundConnection(connection, pending, isLobbyMember, now);
        }

        private void ProcessPendingInboundConnections(float now)
        {
            if (_pendingInboundConnections.Count == 0)
                return;

            _pendingConnectionSnapshot.Clear();
            foreach (HSteamNetConnection connection in _pendingInboundConnections.Keys)
                _pendingConnectionSnapshot.Add(connection);
            for (int index = 0; index < _pendingConnectionSnapshot.Count; index++)
            {
                HSteamNetConnection connection = _pendingConnectionSnapshot[index];
                if (!_pendingInboundConnections.TryGetValue(connection, out PendingInboundConnection pending))
                    continue;
                bool isLobbyMember = _session != null && _session.IsCurrentLobbyMember(pending.SteamId);
                if (isLobbyMember)
                    _departedMembershipChecks.Remove(pending.SteamId);
                ResolvePendingInboundConnection(connection, pending, isLobbyMember, now);
            }
        }

        private void ProcessDepartedLobbyMembers(float now)
        {
            if (_departedMembershipChecks.Count == 0)
                return;
            _departedSteamIdSnapshot.Clear();
            foreach (KeyValuePair<ulong, float> check in _departedMembershipChecks)
            {
                if (now >= check.Value)
                    _departedSteamIdSnapshot.Add(check.Key);
            }
            for (int index = 0; index < _departedSteamIdSnapshot.Count; index++)
            {
                ulong steamId = _departedSteamIdSnapshot[index];
                _departedMembershipChecks.Remove(steamId);
                // A leave callback may arrive after a fast rejoin. The current lobby roster wins over the stale
                // callback so the replacement reservation is not accidentally closed.
                if (_session != null && _session.IsCurrentLobbyMember(steamId))
                    continue;
                CloseConnectionsForSteamId(steamId, "Steam user left the lobby");
            }
        }

        private void ResolvePendingInboundConnection(HSteamNetConnection connection,
            PendingInboundConnection pending, bool isLobbyMember, float now)
        {
            bool identityAlreadyReserved = _connectionsBySteamId.TryGetValue(pending.SteamId,
                out HSteamNetConnection existingIdentity) && existingIdentity != connection;
            InboundConnectionDecision decision = DecideInboundConnection(isLobbyMember,
                identityAlreadyReserved, _steamIdsByConnection.Count, now, pending.ExpiresAt);
            switch (decision)
            {
                case InboundConnectionDecision.WaitForLobbyMembership:
                    return;
                case InboundConnectionDecision.RejectNotLobbyMember:
                    RejectInboundConnection(connection, pending.SteamId,
                        "Steam identity is not in this lobby");
                    return;
                case InboundConnectionDecision.RejectLobbyFull:
                    RejectInboundConnection(connection, pending.SteamId, "Lobby is full");
                    return;
            }

            bool replacingIdentity = decision == InboundConnectionDecision.ReplaceExistingIdentity;
            if (!replacingIdentity && _steamIdsByConnection.Count >= MaximumClients)
            {
                RejectInboundConnection(connection, pending.SteamId, "Lobby is full");
                return;
            }

            EResult accepted = SteamNetworkingSockets.AcceptConnection(connection);
            if (accepted != EResult.k_EResultOK)
            {
                Debug.LogWarning($"Steam could not accept inbound connection {connection}: {accepted}.");
                RejectInboundConnection(connection, pending.SteamId, "Could not accept connection");
                return;
            }

            RemovePendingInboundConnection(connection);
            _steamIdsByConnection[connection] = pending.SteamId;
            _connectionsBySteamId[pending.SteamId] = connection;
            // Commit the replacement only after Steam has accepted the new socket. A failed AcceptConnection leaves
            // the healthy old player intact; a successful replacement emits Stopped(old) before Started(new).
            if (replacingIdentity)
                CloseReservedConnection(existingIdentity,
                    "Superseded by a reconnect from the same Steam user");
        }

        private void RejectInboundConnection(HSteamNetConnection connection, ulong steamId, string reason)
        {
            Debug.LogWarning($"Rejected inbound Steam connection from {steamId}: {reason}.");
            SteamNetworkingSockets.CloseConnection(connection, 0, reason, false);
            RemovePendingInboundConnection(connection);
            RemoveInboundReservation(connection);
        }

        private void CloseReservedConnection(HSteamNetConnection connection, string reason)
        {
            if (connection == HSteamNetConnection.Invalid)
                return;
            SteamNetworkingSockets.CloseConnection(connection, 0, reason, false);
            if (_idsByConnection.ContainsKey(connection))
                RemoveRemoteConnection(connection);
            else
                RemoveInboundReservation(connection);
        }

        private void RemovePendingInboundConnection(HSteamNetConnection connection)
        {
            if (!_pendingInboundConnections.Remove(connection, out PendingInboundConnection pending))
                return;
            if (_pendingConnectionsBySteamId.TryGetValue(pending.SteamId, out HSteamNetConnection current) &&
                current == connection)
                _pendingConnectionsBySteamId.Remove(pending.SteamId);
        }

        private void RemoveInboundReservation(HSteamNetConnection connection)
        {
            RemovePendingInboundConnection(connection);
            if (!_steamIdsByConnection.Remove(connection, out ulong steamId))
                return;
            if (_connectionsBySteamId.TryGetValue(steamId, out HSteamNetConnection current) && current == connection)
                _connectionsBySteamId.Remove(steamId);
        }

        private void OnLobbyMemberExited(CSteamID steamId)
        {
            if (_serverState != LocalConnectionState.Started || !steamId.IsValid())
                return;
            _departedMembershipChecks[steamId.m_SteamID] = Time.unscaledTime + LobbyDepartureRecheckSeconds;
        }

        private void CloseConnectionsForSteamId(ulong steamId, string reason)
        {
            if (_pendingConnectionsBySteamId.TryGetValue(steamId,
                out HSteamNetConnection pendingConnection))
            {
                SteamNetworkingSockets.CloseConnection(pendingConnection, 0, reason, false);
                RemovePendingInboundConnection(pendingConnection);
            }
            if (_connectionsBySteamId.TryGetValue(steamId, out HSteamNetConnection connection))
                CloseReservedConnection(connection, reason);
        }

        private void EnsureSessionSubscription()
        {
            if (_sessionSubscribed)
                return;
            _session = GetComponent<SteamSessionService>();
            if (_session == null)
                _session = SteamSessionService.Active;
            if (_session == null)
                return;
            _session.LobbyMemberExited += OnLobbyMemberExited;
            _sessionSubscribed = true;
        }

        private void RemoveSessionSubscription()
        {
            if (!_sessionSubscribed || _session == null)
                return;
            _session.LobbyMemberExited -= OnLobbyMemberExited;
            _sessionSubscribed = false;
        }

        private void AddRemoteConnection(HSteamNetConnection connection, ulong steamId)
        {
            if (_idsByConnection.ContainsKey(connection))
                return;

            RemovePendingInboundConnection(connection);
            if (steamId == 0 && _steamIdsByConnection.TryGetValue(connection, out ulong reservedSteamId))
                steamId = reservedSteamId;
            if (steamId == 0 || !_connectionsBySteamId.TryGetValue(steamId, out HSteamNetConnection reserved) ||
                reserved != connection)
            {
                Debug.LogWarning($"Closed unreserved inbound Steam connection {connection} for identity {steamId}.");
                SteamNetworkingSockets.CloseConnection(connection, 0, "Connection was not admitted", false);
                RemoveInboundReservation(connection);
                return;
            }

            int connectionId = _nextConnectionId++;
            _connectionsById.Add(connectionId, connection);
            _idsByConnection.Add(connection, connectionId);
            HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Started, connectionId, Index));
        }

        private void RemoveRemoteConnection(HSteamNetConnection connection)
        {
            if (!_idsByConnection.TryGetValue(connection, out int connectionId))
                return;

            _idsByConnection.Remove(connection);
            _connectionsById.Remove(connectionId);
            RemoveInboundReservation(connection);
            HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Stopped, connectionId, Index));
        }

        private void Receive(HSteamNetConnection connection, int connectionId, bool asServer)
        {
            using ProfilerMarker.AutoScope profilerScope = ReceiveMarker.Auto();
            for (int batch = 0; batch < MaximumReceiveBatchesPerIteration; batch++)
            {
                int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(connection, _messagePointers,
                    _messagePointers.Length);
                if (count < 0)
                {
                    NetworkManager?.LogWarning($"Steam receive failed for connection {connection}.");
                    return;
                }
                for (int index = 0; index < count; index++)
                    ReceiveMessage(_messagePointers[index], connectionId, asServer);
                if (count < _messagePointers.Length)
                    return;
            }
            ReceiveBacklogLimitHitCount++;
            if (Time.unscaledTime >= _nextBacklogWarningAt)
            {
                _nextBacklogWarningAt = Time.unscaledTime + 1f;
                NetworkManager?.LogWarning($"Steam receive backlog exceeded " +
                                           $"{MaximumMessagesPerPoll * MaximumReceiveBatchesPerIteration} messages in one iteration.");
            }
        }

        private void ReceiveMessage(IntPtr pointer, int connectionId, bool asServer)
        {
            byte[] bytes = null;
            try
            {
                SteamNetworkingMessage_t message = Marshal.PtrToStructure<SteamNetworkingMessage_t>(pointer);
                int wireByteCount = message.m_cbSize;
                if (wireByteCount < ChannelTrailerSize || wireByteCount > MaximumWirePayload)
                {
                    NetworkManager?.LogWarning($"Dropped malformed Steam message of {wireByteCount} bytes.");
                    return;
                }

                int payloadByteCount = wireByteCount - ChannelTrailerSize;
                byte channel = Marshal.ReadByte(message.m_pData, payloadByteCount);
                if (channel != (byte)Channel.Reliable && channel != (byte)Channel.Unreliable)
                {
                    NetworkManager?.LogWarning($"Dropped Steam message with unsupported FishNet channel {channel}.");
                    return;
                }

                bytes = ByteArrayPool.Retrieve(payloadByteCount);
                Marshal.Copy(message.m_pData, bytes, 0, payloadByteCount);
                ArraySegment<byte> segment = new(bytes, 0, payloadByteCount);
                ReceivedMessageCount++;
                ReceivedByteCount += payloadByteCount;
                if (asServer)
                    HandleServerReceivedDataArgs(new ServerReceivedDataArgs(segment, (Channel)channel, connectionId, Index));
                else
                    HandleClientReceivedDataArgs(new ClientReceivedDataArgs(segment, (Channel)channel, Index));
            }
            finally
            {
                SteamNetworkingMessage_t.Release(pointer);
                if (bytes != null)
                    ByteArrayPool.Store(bytes);
            }
        }

        private void Send(HSteamNetConnection connection, byte channelId, ArraySegment<byte> segment)
        {
            using ProfilerMarker.AutoScope profilerScope = SendMarker.Auto();
            if (connection == HSteamNetConnection.Invalid)
                return;
            if (channelId != (byte)Channel.Reliable && channelId != (byte)Channel.Unreliable)
            {
                FailedSendCount++;
                NetworkManager?.LogError($"Dropped Steam send with unsupported FishNet channel {channelId}.");
                return;
            }
            if (!IsPayloadWithinMtu(segment.Count))
            {
                DroppedOversizeCount++;
                NetworkManager?.LogError($"Dropped Steam send of {segment.Count} bytes; FishNet payload MTU is {FishNetPayloadMtu}.");
                return;
            }
            if (segment.Count > 0 && segment.Array == null)
            {
                FailedSendCount++;
                NetworkManager?.LogError("Dropped Steam send because its ArraySegment has no backing array.");
                return;
            }

            EnsureSendBuffer();
            int wireByteCount = segment.Count + ChannelTrailerSize;
            if (segment.Count > 0)
                Marshal.Copy(segment.Array, segment.Offset, _sendBuffer, segment.Count);
            Marshal.WriteByte(_sendBuffer, segment.Count, channelId);
            int flags = channelId == (byte)Channel.Unreliable
                ? Constants.k_nSteamNetworkingSend_UnreliableNoNagle
                : Constants.k_nSteamNetworkingSend_ReliableNoNagle;
            EResult result = SteamNetworkingSockets.SendMessageToConnection(connection, _sendBuffer,
                (uint)wireByteCount, flags, out _);
            if (result == EResult.k_EResultOK)
            {
                SentMessageCount++;
                SentByteCount += segment.Count;
            }
            else
            {
                FailedSendCount++;
                NetworkManager?.LogWarning($"Steam send failed for connection {connection}: {result}.");
            }
        }

        private void EnsureSendBuffer()
        {
            if (_sendBuffer == IntPtr.Zero)
                _sendBuffer = Marshal.AllocHGlobal(MaximumWirePayload);
        }

        private void ReleaseSendBuffer()
        {
            if (_sendBuffer == IntPtr.Zero)
                return;
            Marshal.FreeHGlobal(_sendBuffer);
            _sendBuffer = IntPtr.Zero;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void SampleConnectionDiagnostics()
        {
            if (!SteamBootstrap.IsAvailable || Time.unscaledTime < _nextDiagnosticSampleAt)
                return;
            _nextDiagnosticSampleAt = Time.unscaledTime + 1f;
            _hasConnectionDiagnostics = false;
            _diagnosticRttMs = 0;
            _diagnosticLocalQuality = 1f;
            _diagnosticRemoteQuality = 1f;
            _diagnosticPendingReliable = 0;
            _diagnosticPendingUnreliable = 0;

            if (_clientConnection != HSteamNetConnection.Invalid)
                AccumulateConnectionDiagnostics(_clientConnection);
            else
            {
                foreach (HSteamNetConnection connection in _connectionsById.Values)
                    AccumulateConnectionDiagnostics(connection);
            }
        }

        private void AccumulateConnectionDiagnostics(HSteamNetConnection connection)
        {
            SteamNetConnectionRealTimeStatus_t status = default;
            SteamNetConnectionRealTimeLaneStatus_t unusedLane = default;
            EResult result = SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0,
                ref unusedLane);
            if (result != EResult.k_EResultOK)
                return;
            if (!_hasConnectionDiagnostics)
            {
                _diagnosticLocalQuality = status.m_flConnectionQualityLocal;
                _diagnosticRemoteQuality = status.m_flConnectionQualityRemote;
            }
            else
            {
                _diagnosticLocalQuality = Mathf.Min(_diagnosticLocalQuality, status.m_flConnectionQualityLocal);
                _diagnosticRemoteQuality = Mathf.Min(_diagnosticRemoteQuality, status.m_flConnectionQualityRemote);
            }
            _hasConnectionDiagnostics = true;
            _diagnosticRttMs = Mathf.Max(_diagnosticRttMs, status.m_nPing);
            _diagnosticPendingReliable += Mathf.Max(0, status.m_cbPendingReliable);
            _diagnosticPendingUnreliable += Mathf.Max(0, status.m_cbPendingUnreliable);
        }
#endif

        private void SetClientState(LocalConnectionState state)
        {
            if (_clientState == state)
                return;
            _clientState = state;
            HandleClientConnectionState(new ClientConnectionStateArgs(state, Index));
        }

        private void SetServerState(LocalConnectionState state)
        {
            if (_serverState == state)
                return;
            _serverState = state;
            HandleServerConnectionState(new ServerConnectionStateArgs(state, Index));
        }

        private void EnsureCallback()
        {
            _connectionCallback ??= Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
        }

        private void DisposeCallback()
        {
            _connectionCallback?.Dispose();
            _connectionCallback = null;
        }

        private bool EnsureSteam()
        {
            if (SteamBootstrap.IsAvailable)
                return true;
            NetworkManager.LogError("Steam transport requires a running Steam client and a valid Steam App ID.");
            return false;
        }

        private void ReportClientProblem(string diagnostic)
        {
            LastClientError = string.IsNullOrWhiteSpace(diagnostic) ? "Unknown Steam transport error." : diagnostic;
            NetworkManager?.LogError($"Steam Networking Sockets: {LastClientError}");
            ClientProblem?.Invoke(LastClientError);
        }

        private static ulong GetRemoteSteamId(HSteamNetConnection connection)
        {
            SteamNetConnectionInfo_t info = new SteamNetConnectionInfo_t();
            return SteamNetworkingSockets.GetConnectionInfo(connection, out info) ? info.m_identityRemote.GetSteamID64() : 0;
        }
    }
}
