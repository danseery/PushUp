using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PushUp.Core;
using Steamworks;
using UnityEngine;

namespace PushUp.Steam
{
    public readonly struct SteamFriendInfo
    {
        public SteamFriendInfo(CSteamID steamId, string name, EPersonaState state, bool isCurrentLobbyMember = false)
        {
            SteamId = steamId;
            Name = name;
            State = state;
            IsCurrentLobbyMember = isCurrentLobbyMember;
        }

        public CSteamID SteamId { get; }
        public string Name { get; }
        public EPersonaState State { get; }
        public bool IsOnline => State != EPersonaState.k_EPersonaStateOffline;
        public bool IsCurrentLobbyMember { get; }
    }

    public readonly struct SteamLobbyInvite
    {
        public SteamLobbyInvite(CSteamID inviterId, string inviterName, CSteamID lobbyId)
        {
            InviterId = inviterId;
            InviterName = inviterName;
            LobbyId = lobbyId;
        }

        public CSteamID InviterId { get; }
        public string InviterName { get; }
        public CSteamID LobbyId { get; }
        public bool IsValid => LobbyId.IsValid();
    }

    public enum SteamLobbyCompatibility : byte
    {
        Unknown,
        Compatible,
        Incompatible
    }

    public readonly struct SteamFriendSessionInfo
    {
        public SteamFriendSessionInfo(CSteamID friendId, string friendName, CSteamID lobbyId,
            SteamLobbyCompatibility compatibility, int members, int capacity,
            string runState = PushUpConstants.LobbyStateWaiting)
        {
            FriendId = friendId;
            FriendName = friendName;
            LobbyId = lobbyId;
            Compatibility = compatibility;
            Members = members;
            Capacity = capacity;
            RunState = runState ?? string.Empty;
        }

        public CSteamID FriendId { get; }
        public string FriendName { get; }
        public CSteamID LobbyId { get; }
        public SteamLobbyCompatibility Compatibility { get; }
        public int Members { get; }
        public int Capacity { get; }
        public string RunState { get; }
        public bool IsFull => Capacity > 0 && Members >= Capacity;
        public bool CanJoin => LobbyId.IsValid() && Compatibility == SteamLobbyCompatibility.Compatible && !IsFull &&
                               RunState is PushUpConstants.LobbyStateWaiting or PushUpConstants.LobbyStateRunning;
    }

    public enum SteamJoinSource : byte
    {
        Direct,
        PendingInvite,
        FriendSession,
        LobbyJoinCallback,
        RichPresence,
        LaunchCommand
    }

    public enum SteamSessionOperationKind : byte
    {
        None,
        CreatingLobby,
        JoiningLobby
    }

    public readonly struct SteamSessionOperation
    {
        public SteamSessionOperation(SteamSessionOperationKind kind, int generation, CSteamID lobbyId,
            float startedAt)
        {
            Kind = kind;
            Generation = generation;
            LobbyId = lobbyId;
            StartedAt = startedAt;
        }

        public SteamSessionOperationKind Kind { get; }
        public int Generation { get; }
        public CSteamID LobbyId { get; }
        public float StartedAt { get; }
        public bool IsActive => Kind != SteamSessionOperationKind.None;
    }

    public readonly struct SteamLobbyMemberInfo
    {
        public SteamLobbyMemberInfo(CSteamID steamId, string name, bool isOwner)
        {
            SteamId = steamId;
            Name = name;
            IsOwner = isOwner;
        }

        public CSteamID SteamId { get; }
        public string Name { get; }
        public bool IsOwner { get; }
    }

    public readonly struct SteamLobbySnapshot
    {
        public SteamLobbySnapshot(CSteamID lobbyId, CSteamID ownerId, ulong originalHostSteamId,
            string runState, int capacity, SteamLobbyMemberInfo[] members)
        {
            LobbyId = lobbyId;
            OwnerId = ownerId;
            OriginalHostSteamId = originalHostSteamId;
            RunState = runState;
            Capacity = capacity;
            Members = members ?? Array.Empty<SteamLobbyMemberInfo>();
        }

        public CSteamID LobbyId { get; }
        public CSteamID OwnerId { get; }
        public ulong OriginalHostSteamId { get; }
        public string RunState { get; }
        public int Capacity { get; }
        public SteamLobbyMemberInfo[] Members { get; }
        public int MemberCount => Members?.Length ?? 0;
        public bool IsFull => Capacity > 0 && MemberCount >= Capacity;
        public bool OriginalHostIsOwner => OriginalHostSteamId != 0 && OwnerId.m_SteamID == OriginalHostSteamId;
    }

    public sealed class SteamSessionService : MonoBehaviour
    {
        private const int LaunchCommandCapacity = 4096;
        public const float FriendDataRefreshIntervalSeconds = 5f;
        public const float FriendDataRequestTimeoutSeconds = 15f;

        private sealed class FriendLobbyCacheEntry
        {
            public CSteamID FriendId;
            public string FriendName;
            public CSteamID LobbyId;
            public SteamLobbyCompatibility Compatibility;
            public int Members;
            public int Capacity;
            public string RunState = string.Empty;
            public float LastRequestedAt = float.NegativeInfinity;
            public bool RequestPending;
            public bool HasMetadata;
            public int DiscoveryGeneration;
        }

        private static readonly Regex LobbyConnectPattern = new(
            @"(?:^|\s)\+connect_lobby\s+[""']?(?<lobby>\d+)[""']?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static SteamSessionService Active { get; private set; }
        public event Action<CSteamID> LobbyJoinStarted;
        public event Action LobbyCreateStarted;
        public event Action<CSteamID> LobbyJoined;
        public event Action<SteamLobbyInvite?> PendingInviteChanged;
        public event Action SessionListChanged;
        public event Action<string> SessionError;
        public event Action<SteamSessionOperation> OperationChanged;
        public event Action<SteamLobbySnapshot> LobbySnapshotChanged;
        public event Action<CSteamID, CSteamID> LobbyOwnerChanged;
        public event Action<CSteamID> OriginalHostLeft;
        public event Action<CSteamID> LobbyMemberExited;

        public CSteamID CurrentLobby { get; private set; } = CSteamID.Nil;
        public CSteamID JoiningLobby { get; private set; } = CSteamID.Nil;
        public SteamLobbyInvite? PendingInvite { get; private set; }
        public SteamSessionOperation CurrentOperation { get; private set; }
        public ulong OriginalHostSteamId { get; private set; }
        public bool IsJoiningLobby => JoiningLobby.IsValid();
        public bool IsCreatingLobby => CurrentOperation.Kind == SteamSessionOperationKind.CreatingLobby;
        public bool HasActiveOperation => CurrentOperation.IsActive;
        public bool IsLobbyOwner => CurrentLobby.IsValid() && Platform.GetLobbyOwner(CurrentLobby) == Platform.LocalUserId;
        public bool IsOverlayAvailable => Platform.IsAvailable && Platform.IsOverlayEnabled;

        private readonly Dictionary<int, IDisposable> _pendingLobbyCreates = new();
        private readonly Dictionary<int, IDisposable> _pendingLobbyJoins = new();
        private readonly Dictionary<ulong, FriendLobbyCacheEntry> _friendLobbyCache = new();
        private Callback<GameLobbyJoinRequested_t> _joinRequested;
        private Callback<GameRichPresenceJoinRequested_t> _richPresenceJoinRequested;
        private Callback<LobbyInvite_t> _lobbyInvite;
        private Callback<LobbyDataUpdate_t> _lobbyDataUpdated;
        private Callback<LobbyChatUpdate_t> _lobbyChatUpdated;
        private Callback<NewUrlLaunchParameters_t> _newUrlLaunchParameters;
        private int _operationGeneration;
        private int _friendDiscoveryGeneration;
        private bool _coldLaunchProcessed;
        private CSteamID _lastObservedLobbyOwner = CSteamID.Nil;
        private bool _originalHostDepartureReported;
        private bool _ownerAllowsJoining;
        private ISteamPlatform _platform;
        private ISteamPlatform Platform => _platform ??= new SteamworksPlatform();

        private void Awake()
        {
            Active = this;
            _platform ??= new SteamworksPlatform();
            if (!Platform.IsAvailable)
                return;

            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
            _richPresenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoinRequested);
            _lobbyInvite = Callback<LobbyInvite_t>.Create(OnLobbyInvite);
            _lobbyDataUpdated = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdated);
            _lobbyChatUpdated = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdated);
            _newUrlLaunchParameters = Callback<NewUrlLaunchParameters_t>.Create(_ => TryJoinSteamLaunchCommand());
        }

        // Start runs after every component Awake, so the coordinator can subscribe before a cold-launch join begins.
        private void Start() => ProcessDeferredLaunchJoin();

        private void OnDestroy()
        {
            DisposeSteamCallbacks();
            if (Active == this)
                Active = null;
        }

        /// <summary>Injects a deterministic backend before invoking session APIs in EditMode tests.</summary>
        public void SetPlatformForTests(ISteamPlatform platform)
        {
            if (platform == null)
                throw new ArgumentNullException(nameof(platform));
            if (HasActiveOperation || CurrentLobby.IsValid())
                throw new InvalidOperationException("Cannot replace the Steam platform during an active session.");
            _platform = platform;
        }

        public bool HostFriendsGame()
        {
            if (!RequireSteam() || CurrentLobby.IsValid() || !TryBeginOperation(SteamSessionOperationKind.CreatingLobby, CSteamID.Nil,
                    out int generation))
                return false;

            IDisposable result = Platform.BeginCreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly,
                PushUpConstants.MaxPlayers,
                (callback, ioFailure) => OnLobbyCreated(callback, ioFailure, generation), out bool started);
            if (!started || result == null)
            {
                CompleteOperation(generation);
                SessionError?.Invoke("Steam could not begin creating a friends lobby.");
                return false;
            }
            if (IsCurrentOperation(generation, SteamSessionOperationKind.CreatingLobby))
                _pendingLobbyCreates.Add(generation, result);
            else
                result.Dispose();
            LobbyCreateStarted?.Invoke();
            return true;
        }

        public bool JoinLobby(CSteamID lobbyId, SteamJoinSource source = SteamJoinSource.Direct)
        {
            if (!RequireSteam())
                return false;

            // A Steam invite may arrive while this client still belongs to the lobby but its game transport has
            // dropped. Joining the lobby again is neither necessary nor accepted by Steam in that state. Re-publish
            // the existing lobby as joined so the network coordinator can reconnect to the original host. This is
            // also harmless when the transport is already healthy because the coordinator treats it idempotently.
            if (CurrentLobby.IsValid() && lobbyId == CurrentLobby && !IsLobbyOwner)
            {
                if (IsCurrentLobbyMember(Platform.LocalUserId.m_SteamID))
                {
                    ClearPendingInviteForLobby(lobbyId);
                    Debug.Log($"Reusing current Steam lobby {lobbyId.m_SteamID} for a transport reconnect from {source}.");
                    PublishLobbySnapshot();
                    LobbyJoined?.Invoke(CurrentLobby);
                    return true;
                }

                // Steam can report the local member leaving before the game has cleared its cached lobby ID. Clear
                // only the local bookkeeping and perform a real JoinLobby below.
                ResetCurrentLobbyState(clearRichPresence: true);
            }
            if (!CanBeginJoin(CurrentLobby.m_SteamID, JoiningLobby.m_SteamID, lobbyId.m_SteamID) ||
                HasActiveOperation)
            {
                Debug.Log($"Ignored duplicate or invalid Steam lobby join request for {lobbyId.m_SteamID} from {source}.");
                return false;
            }
            CSteamID knownOwner = Platform.GetLobbyOwner(lobbyId);
            if (knownOwner.IsValid() && knownOwner == Platform.LocalUserId)
            {
                Debug.LogWarning($"Ignored self-owned Steam lobby join request for {lobbyId.m_SteamID} from {source}.");
                return false;
            }
            if (CurrentLobby.IsValid())
                LeaveLobby();
            if (!TryBeginOperation(SteamSessionOperationKind.JoiningLobby, lobbyId, out int generation))
                return false;

            JoiningLobby = lobbyId;
            Debug.Log($"Joining Steam lobby {lobbyId.m_SteamID} from {source}.");
            LobbyJoinStarted?.Invoke(lobbyId);
            IDisposable result = Platform.BeginJoinLobby(lobbyId,
                (callback, ioFailure) => OnLobbyEntered(callback, ioFailure, generation, lobbyId), out bool started);
            if (!started || result == null)
            {
                CompleteOperation(generation);
                JoiningLobby = CSteamID.Nil;
                SessionError?.Invoke("Steam could not begin joining that lobby.");
                return false;
            }
            // Steam callbacks are asynchronous in production, but keeping this correct for a synchronous test
            // backend also prevents a completed handle from being retained forever.
            if (IsCurrentOperation(generation, SteamSessionOperationKind.JoiningLobby))
                _pendingLobbyJoins.Add(generation, result);
            else
                result.Dispose();
            return true;
        }

        public bool CancelActiveOperation()
        {
            if (!CurrentOperation.IsActive)
                return false;
            int generation = CurrentOperation.Generation;
            JoiningLobby = CSteamID.Nil;
            CompleteOperation(generation);
            return true;
        }

        public bool IsOperationTimedOut(float now, float timeoutSeconds) =>
            CurrentOperation.IsActive && timeoutSeconds > 0f && now - CurrentOperation.StartedAt >= timeoutSeconds;

        public bool AcceptPendingInvite()
        {
            if (PendingInvite is not SteamLobbyInvite invite || !invite.IsValid)
                return false;
            return JoinLobby(invite.LobbyId, SteamJoinSource.PendingInvite);
        }

        public void DeclinePendingInvite() => ClearPendingInvite();

        public bool OpenInviteOverlay(out string status)
        {
            if (!PrepareLobbyForInvitation(out status))
                return false;

            if (!Platform.IsOverlayEnabled)
            {
                status = "Steam Overlay is not active. Use the in-game friend buttons below.";
                return false;
            }

            Platform.ActivateGameOverlayInviteDialog(CurrentLobby);
            status = "Steam invite overlay requested. You can also invite directly below.";
            Debug.Log($"Requested Steam invite overlay for lobby {CurrentLobby.m_SteamID}.");
            return true;
        }

        public SteamFriendInfo[] GetInviteCandidates()
        {
            if (!Platform.IsAvailable)
                return Array.Empty<SteamFriendInfo>();

            int friendCount = Mathf.Max(0, Platform.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate));
            List<SteamFriendInfo> friends = new(friendCount);
            HashSet<ulong> lobbyMembers = new();
            if (CurrentLobby.IsValid())
            {
                int memberCount = Mathf.Max(0, Platform.GetNumLobbyMembers(CurrentLobby));
                for (int memberIndex = 0; memberIndex < memberCount; memberIndex++)
                    lobbyMembers.Add(Platform.GetLobbyMemberByIndex(CurrentLobby, memberIndex).m_SteamID);
            }
            for (int index = 0; index < friendCount; index++)
            {
                CSteamID steamId = Platform.GetFriendByIndex(index, EFriendFlags.k_EFriendFlagImmediate);
                if (!steamId.IsValid())
                    continue;

                string name = Platform.GetFriendPersonaName(steamId);
                friends.Add(new SteamFriendInfo(steamId,
                    string.IsNullOrWhiteSpace(name) ? steamId.m_SteamID.ToString() : name,
                    Platform.GetFriendPersonaState(steamId), lobbyMembers.Contains(steamId.m_SteamID)));
            }

            friends.Sort((left, right) =>
            {
                int onlineOrder = right.IsOnline.CompareTo(left.IsOnline);
                return onlineOrder != 0 ? onlineOrder : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });
            return friends.ToArray();
        }

        public SteamFriendSessionInfo[] GetJoinableFriendSessions()
        {
            if (!Platform.IsAvailable)
                return Array.Empty<SteamFriendSessionInfo>();

            int discoveryGeneration = ++_friendDiscoveryGeneration;
            int friendCount = Mathf.Max(0, Platform.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate));
            HashSet<ulong> seenLobbies = new();
            for (int index = 0; index < friendCount; index++)
            {
                CSteamID friendId = Platform.GetFriendByIndex(index, EFriendFlags.k_EFriendFlagImmediate);
                if (!friendId.IsValid() || !Platform.GetFriendGamePlayed(friendId, out FriendGameInfo_t game) ||
                    !IsJoinableFriendLobby(game.m_gameID.AppID().m_AppId, game.m_steamIDLobby.m_SteamID,
                        Platform.AppId) || !seenLobbies.Add(game.m_steamIDLobby.m_SteamID))
                    continue;

                if (!_friendLobbyCache.TryGetValue(game.m_steamIDLobby.m_SteamID, out FriendLobbyCacheEntry entry))
                {
                    entry = new FriendLobbyCacheEntry
                    {
                        FriendId = friendId,
                        FriendName = Platform.GetFriendPersonaName(friendId),
                        LobbyId = game.m_steamIDLobby,
                        Compatibility = SteamLobbyCompatibility.Unknown
                    };
                    _friendLobbyCache.Add(game.m_steamIDLobby.m_SteamID, entry);
                }
                else
                {
                    entry.FriendId = friendId;
                    entry.FriendName = Platform.GetFriendPersonaName(friendId);
                    entry.LobbyId = game.m_steamIDLobby;
                }

                entry.DiscoveryGeneration = discoveryGeneration;
                if (ShouldRequestFriendLobby(entry.RequestPending, entry.HasMetadata, entry.LastRequestedAt,
                        Time.unscaledTime, FriendDataRefreshIntervalSeconds))
                {
                    entry.LastRequestedAt = Time.unscaledTime;
                    entry.RequestPending = Platform.RequestLobbyData(entry.LobbyId);
                }
            }

            List<ulong> staleLobbies = new();
            foreach (KeyValuePair<ulong, FriendLobbyCacheEntry> pair in _friendLobbyCache)
            {
                if (pair.Value.DiscoveryGeneration != discoveryGeneration)
                    staleLobbies.Add(pair.Key);
            }
            foreach (ulong lobbyId in staleLobbies)
                _friendLobbyCache.Remove(lobbyId);

            return GetCachedJoinableFriendSessions();
        }

        /// <summary>Returns the last completed friend-session snapshot without issuing Steam requests.</summary>
        public SteamFriendSessionInfo[] GetCachedJoinableFriendSessions()
        {
            List<SteamFriendSessionInfo> sessions = new(_friendLobbyCache.Count);
            foreach (FriendLobbyCacheEntry entry in _friendLobbyCache.Values)
            {
                sessions.Add(new SteamFriendSessionInfo(entry.FriendId,
                    string.IsNullOrWhiteSpace(entry.FriendName) ? entry.FriendId.m_SteamID.ToString() : entry.FriendName,
                    entry.LobbyId, entry.Compatibility, entry.Members, entry.Capacity, entry.RunState));
            }
            sessions.Sort((left, right) => string.Compare(left.FriendName, right.FriendName,
                StringComparison.OrdinalIgnoreCase));
            return sessions.ToArray();
        }

        public SteamFriendSessionInfo[] RefreshJoinableFriendSessions()
        {
            foreach (FriendLobbyCacheEntry entry in _friendLobbyCache.Values)
            {
                if (entry.RequestPending)
                    continue;
                entry.HasMetadata = false;
                entry.LastRequestedAt = float.NegativeInfinity;
            }
            return GetJoinableFriendSessions();
        }

        public bool InviteFriend(CSteamID friendId, out string status)
        {
            if (!PrepareLobbyForInvitation(out status))
                return false;

            if (friendId.IsValid() && IsCurrentLobbyMember(friendId.m_SteamID))
            {
                string currentMemberName = Platform.GetFriendPersonaName(friendId);
                status = $"{currentMemberName} is already in this lobby. If their connection dropped, " +
                         "they can use Rejoin Game.";
                return false;
            }

            bool sent = friendId.IsValid() && Platform.InviteUserToLobby(CurrentLobby, friendId);
            string friendName = friendId.IsValid() ? Platform.GetFriendPersonaName(friendId) : "that friend";
            status = sent ? $"Steam lobby invite sent to {friendName}." : $"Steam could not send an invite to {friendName}.";
            if (sent)
                Debug.Log($"Sent Steam lobby {CurrentLobby.m_SteamID} invite to {friendId.m_SteamID} ({friendName}).");
            else
                Debug.LogWarning(status);
            return sent;
        }

        public void LeaveLobby()
        {
            CancelActiveOperation();
            if (CurrentLobby.IsValid() && Platform.IsAvailable)
                Platform.LeaveLobby(CurrentLobby);
            ResetCurrentLobbyState(clearRichPresence: true);
            LobbySnapshotChanged?.Invoke(new SteamLobbySnapshot(CSteamID.Nil, CSteamID.Nil, 0,
                PushUpConstants.LobbyStateWaiting, 0, Array.Empty<SteamLobbyMemberInfo>()));
        }

        public bool SetLobbyRunState(string runState, bool joinable, string richPresenceStatus = null)
        {
            if (!Platform.IsAvailable || !CurrentLobby.IsValid() || !IsLobbyOwner ||
                !IsKnownRunState(runState))
                return false;

            SteamLobbySnapshot snapshot = GetCurrentLobbySnapshot();
            _ownerAllowsJoining = joinable;
            bool effectiveJoinable = joinable && !snapshot.IsFull;
            bool metadataSet = Platform.SetLobbyData(CurrentLobby, PushUpConstants.LobbyStateKey, runState);
            bool joinableSet = Platform.SetLobbyJoinable(CurrentLobby, effectiveJoinable);
            if (!string.IsNullOrWhiteSpace(richPresenceStatus))
                SetLobbyRichPresence(CurrentLobby, richPresenceStatus);
            PublishLobbySnapshot();
            return metadataSet && joinableSet;
        }

        public SteamLobbySnapshot GetCurrentLobbySnapshot()
        {
            if (!Platform.IsAvailable || !CurrentLobby.IsValid())
            {
                return new SteamLobbySnapshot(CSteamID.Nil, CSteamID.Nil, 0,
                    PushUpConstants.LobbyStateWaiting, 0, Array.Empty<SteamLobbyMemberInfo>());
            }

            CSteamID owner = Platform.GetLobbyOwner(CurrentLobby);
            int memberCount = Mathf.Max(0, Platform.GetNumLobbyMembers(CurrentLobby));
            SteamLobbyMemberInfo[] members = new SteamLobbyMemberInfo[memberCount];
            for (int index = 0; index < memberCount; index++)
            {
                CSteamID member = Platform.GetLobbyMemberByIndex(CurrentLobby, index);
                string name = Platform.GetFriendPersonaName(member);
                members[index] = new SteamLobbyMemberInfo(member,
                    string.IsNullOrWhiteSpace(name) ? member.m_SteamID.ToString() : name, member == owner);
            }
            string runState = Platform.GetLobbyData(CurrentLobby, PushUpConstants.LobbyStateKey);
            int capacity = Mathf.Max(0, Platform.GetLobbyMemberLimit(CurrentLobby));
            return new SteamLobbySnapshot(CurrentLobby, owner, OriginalHostSteamId,
                string.IsNullOrWhiteSpace(runState) ? PushUpConstants.LobbyStateWaiting : runState, capacity, members);
        }

        public bool IsCurrentLobbyMember(ulong steamId)
        {
            if (!CurrentLobby.IsValid() || !Platform.IsAvailable)
                return false;

            int count = Platform.GetNumLobbyMembers(CurrentLobby);
            for (int index = 0; index < count; index++)
            {
                if (Platform.GetLobbyMemberByIndex(CurrentLobby, index).m_SteamID == steamId)
                    return true;
            }
            return false;
        }

        public static bool TryParseLobbyConnect(string command, out ulong lobbyId)
        {
            lobbyId = 0;
            if (string.IsNullOrWhiteSpace(command))
                return false;
            Match match = LobbyConnectPattern.Match(command);
            return match.Success && ulong.TryParse(match.Groups["lobby"].Value, out lobbyId) && lobbyId != 0;
        }

        public static bool CanBeginJoin(ulong currentLobby, ulong joiningLobby, ulong requestedLobby) =>
            requestedLobby != 0 && requestedLobby != currentLobby && joiningLobby == 0;

        public static bool CanBeginOperation(SteamSessionOperationKind current,
            SteamSessionOperationKind requested) =>
            current == SteamSessionOperationKind.None && requested != SteamSessionOperationKind.None;

        public static bool ShouldLeaveStaleJoin(bool ioFailure, ulong staleLobby, ulong currentLobby,
            SteamSessionOperationKind activeKind, ulong activeLobby) =>
            !ioFailure && staleLobby != 0 && staleLobby != currentLobby &&
            !(activeKind == SteamSessionOperationKind.JoiningLobby && activeLobby == staleLobby);

        public static bool ShouldRequestFriendLobby(bool requestPending, bool hasMetadata, float lastRequestedAt,
            float now, float refreshInterval, float requestTimeout = FriendDataRequestTimeoutSeconds)
        {
            float elapsed = now - lastRequestedAt;
            if (requestPending)
                return elapsed >= Mathf.Max(1f, requestTimeout);
            // A failed LobbyDataUpdate leaves metadata unavailable. Respect the refresh interval instead of
            // immediately requesting again when a UI redraw observes the cache-miss.
            return float.IsNegativeInfinity(lastRequestedAt) || elapsed >= Mathf.Max(0.1f, refreshInterval);
        }

        public static bool IsKnownRunState(string state) =>
            state == PushUpConstants.LobbyStateWaiting || state == PushUpConstants.LobbyStateStarting ||
            state == PushUpConstants.LobbyStateRunning ||
            state == PushUpConstants.LobbyStateEnding;

        public static bool IsJoinableFriendLobby(uint appId, ulong lobbyId, uint expectedAppId) =>
            appId == expectedAppId && lobbyId != 0;

        private void OnLobbyCreated(LobbyCreated_t callback, bool ioFailure, int generation)
        {
            DisposePendingCreate(generation);
            if (!IsCurrentOperation(generation, SteamSessionOperationKind.CreatingLobby))
            {
                if (!ioFailure && callback.m_eResult == EResult.k_EResultOK && callback.m_ulSteamIDLobby != 0 &&
                    Platform.IsAvailable)
                    Platform.LeaveLobby(new CSteamID(callback.m_ulSteamIDLobby));
                return;
            }
            CompleteOperation(generation);
            if (ioFailure || callback.m_eResult != EResult.k_EResultOK)
            {
                SessionError?.Invoke($"Steam could not create a friends lobby ({callback.m_eResult}).");
                return;
            }

            CurrentLobby = new CSteamID(callback.m_ulSteamIDLobby);
            JoiningLobby = CSteamID.Nil;
            OriginalHostSteamId = Platform.LocalUserId.m_SteamID;
            _originalHostDepartureReported = false;
            _ownerAllowsJoining = true;
            bool metadataSet =
                Platform.SetLobbyData(CurrentLobby, PushUpConstants.LobbyProtocolKey, PushUpConstants.ProtocolVersion) &&
                Platform.SetLobbyData(CurrentLobby, PushUpConstants.LobbyBuildKey, Application.version) &&
                Platform.SetLobbyData(CurrentLobby, PushUpConstants.LobbyHostKey, OriginalHostSteamId.ToString()) &&
                Platform.SetLobbyData(CurrentLobby, PushUpConstants.LobbyStateKey, PushUpConstants.LobbyStateWaiting);
            if (!metadataSet || !Platform.SetLobbyJoinable(CurrentLobby, true))
            {
                Platform.LeaveLobby(CurrentLobby);
                CurrentLobby = CSteamID.Nil;
                OriginalHostSteamId = 0;
                SessionError?.Invoke("Steam created the lobby but could not publish its compatibility metadata.");
                return;
            }
            SetLobbyRichPresence(CurrentLobby, "Hosting PushUp");
            Debug.Log($"Created Steam lobby {CurrentLobby.m_SteamID} for build {Application.version}.");
            PublishLobbySnapshot();
            LobbyJoined?.Invoke(CurrentLobby);
        }

        private void OnLobbyEntered(LobbyEnter_t callback, bool ioFailure, int generation, CSteamID requestedLobby)
        {
            DisposePendingJoin(generation);
            if (!IsCurrentOperation(generation, SteamSessionOperationKind.JoiningLobby))
            {
                CSteamID staleLobby = callback.m_ulSteamIDLobby != 0
                    ? new CSteamID(callback.m_ulSteamIDLobby)
                    : requestedLobby;
                if (ShouldLeaveStaleJoin(ioFailure, staleLobby.m_SteamID, CurrentLobby.m_SteamID,
                        CurrentOperation.Kind, CurrentOperation.LobbyId.m_SteamID) && Platform.IsAvailable)
                    Platform.LeaveLobby(staleLobby);
                return;
            }
            CompleteOperation(generation);
            requestedLobby = callback.m_ulSteamIDLobby != 0
                ? new CSteamID(callback.m_ulSteamIDLobby)
                : requestedLobby;
            JoiningLobby = CSteamID.Nil;
            if (ioFailure || callback.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Debug.LogError($"Steam lobby enter failed for {requestedLobby.m_SteamID}: IO={ioFailure}, response={callback.m_EChatRoomEnterResponse}.");
                SessionError?.Invoke("Steam could not join that lobby.");
                return;
            }

            CurrentLobby = requestedLobby;
            if (!TryReadMetadata(CurrentLobby, out LobbyMetadata metadata) || !metadata.IsCompatible(Application.version))
            {
                Debug.LogError($"Rejected incompatible Steam lobby {CurrentLobby.m_SteamID}. Local build={Application.version}, protocol={PushUpConstants.ProtocolVersion}.");
                Platform.LeaveLobby(CurrentLobby);
                CurrentLobby = CSteamID.Nil;
                SessionError?.Invoke("That lobby uses an incompatible build.");
                return;
            }

            CSteamID owner = Platform.GetLobbyOwner(CurrentLobby);
            if (!owner.IsValid() || owner.m_SteamID != metadata.HostSteamId)
            {
                Debug.LogError($"Rejected orphaned Steam lobby {CurrentLobby.m_SteamID}. " +
                               $"Recorded host={metadata.HostSteamId}, current owner={owner.m_SteamID}.");
                Platform.LeaveLobby(CurrentLobby);
                CurrentLobby = CSteamID.Nil;
                SessionError?.Invoke("That game's host has already left the lobby.");
                return;
            }

            OriginalHostSteamId = metadata.HostSteamId;
            _originalHostDepartureReported = false;
            ClearPendingInviteForLobby(CurrentLobby);
            SetLobbyRichPresence(CurrentLobby, "Playing PushUp");
            Debug.Log($"Entered Steam lobby {CurrentLobby.m_SteamID}; host={metadata.HostSteamId}.");
            PublishLobbySnapshot();
            LobbyJoined?.Invoke(CurrentLobby);
        }

        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t callback)
        {
            Debug.Log($"Received GameLobbyJoinRequested for lobby {callback.m_steamIDLobby.m_SteamID} via friend {callback.m_steamIDFriend.m_SteamID}.");
            JoinLobby(callback.m_steamIDLobby, SteamJoinSource.LobbyJoinCallback);
        }

        private void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t callback)
        {
            Debug.Log($"Received GameRichPresenceJoinRequested from {callback.m_steamIDFriend.m_SteamID}: {callback.m_rgchConnect}");
            TryJoinConnectString(callback.m_rgchConnect, SteamJoinSource.RichPresence);
        }

        private void OnLobbyInvite(LobbyInvite_t callback)
        {
            CSteamID inviter = new(callback.m_ulSteamIDUser);
            CSteamID lobby = new(callback.m_ulSteamIDLobby);
            if (!lobby.IsValid())
                return;
            if (CurrentLobby.IsValid() && lobby == CurrentLobby)
            {
                Debug.Log($"Received another invite for current Steam lobby {lobby.m_SteamID}; treating it as a reconnect request.");
                JoinLobby(lobby, SteamJoinSource.PendingInvite);
                return;
            }
            string name = Platform.GetFriendPersonaName(inviter);
            SteamLobbyInvite pending = new(inviter,
                string.IsNullOrWhiteSpace(name) ? inviter.m_SteamID.ToString() : name, lobby);
            if (PendingInvite is SteamLobbyInvite existing && existing.InviterId == pending.InviterId &&
                existing.LobbyId == pending.LobbyId)
                return;
            PendingInvite = pending;
            Debug.Log($"Received Steam lobby invitation from {inviter.m_SteamID} for lobby {lobby.m_SteamID}.");
            PendingInviteChanged?.Invoke(PendingInvite);
        }

        public bool ProcessDeferredLaunchJoin()
        {
            if (_coldLaunchProcessed)
                return false;
            _coldLaunchProcessed = true;
            string commandLine = string.Join(" ", Environment.GetCommandLineArgs());
            if (TryJoinConnectString(commandLine, SteamJoinSource.LaunchCommand))
                return true;
            return TryJoinSteamLaunchCommand();
        }

        private bool TryJoinSteamLaunchCommand()
        {
            if (!Platform.IsAvailable)
                return false;
            int length = Platform.GetLaunchCommandLine(out string commandLine, LaunchCommandCapacity);
            return length > 0 && TryJoinConnectString(commandLine, SteamJoinSource.LaunchCommand);
        }

        private bool TryJoinConnectString(string command, SteamJoinSource source)
        {
            if (!TryParseLobbyConnect(command, out ulong lobbyId))
                return false;
            return JoinLobby(new CSteamID(lobbyId), source);
        }

        private SteamLobbyCompatibility GetCompatibility(CSteamID lobby)
        {
            string protocol = Platform.GetLobbyData(lobby, PushUpConstants.LobbyProtocolKey);
            string build = Platform.GetLobbyData(lobby, PushUpConstants.LobbyBuildKey);
            string host = Platform.GetLobbyData(lobby, PushUpConstants.LobbyHostKey);
            string state = Platform.GetLobbyData(lobby, PushUpConstants.LobbyStateKey);
            if (string.IsNullOrWhiteSpace(protocol) && string.IsNullOrWhiteSpace(build))
                return SteamLobbyCompatibility.Unknown;
            return LobbyMetadata.TryParse(protocol, build, host, state, out LobbyMetadata metadata) &&
                   metadata.IsCompatible(Application.version)
                ? SteamLobbyCompatibility.Compatible
                : SteamLobbyCompatibility.Incompatible;
        }

        private bool TryReadMetadata(CSteamID lobby, out LobbyMetadata metadata) =>
            LobbyMetadata.TryParse(
                Platform.GetLobbyData(lobby, PushUpConstants.LobbyProtocolKey),
                Platform.GetLobbyData(lobby, PushUpConstants.LobbyBuildKey),
                Platform.GetLobbyData(lobby, PushUpConstants.LobbyHostKey),
                Platform.GetLobbyData(lobby, PushUpConstants.LobbyStateKey), out metadata);

        private void SetLobbyRichPresence(CSteamID lobby, string status)
        {
            Platform.SetRichPresence("status", status);
            Platform.SetRichPresence("connect", $"+connect_lobby {lobby.m_SteamID}");
            Platform.SetRichPresence("steam_player_group", lobby.m_SteamID.ToString());
            Platform.SetRichPresence("steam_player_group_size",
                Mathf.Max(1, Platform.GetNumLobbyMembers(lobby)).ToString());
        }

        private void OnLobbyDataUpdated(LobbyDataUpdate_t callback)
        {
            CSteamID lobby = new(callback.m_ulSteamIDLobby);
            bool roomMetadata = callback.m_ulSteamIDMember == callback.m_ulSteamIDLobby;
            if (roomMetadata && _friendLobbyCache.TryGetValue(callback.m_ulSteamIDLobby,
                    out FriendLobbyCacheEntry entry))
            {
                entry.RequestPending = false;
                entry.HasMetadata = callback.m_bSuccess != 0;
                if (entry.HasMetadata)
                {
                    entry.Compatibility = GetCompatibility(lobby);
                    entry.RunState = Platform.GetLobbyData(lobby, PushUpConstants.LobbyStateKey);
                    entry.Members = Mathf.Max(0, Platform.GetNumLobbyMembers(lobby));
                    entry.Capacity = Mathf.Max(0, Platform.GetLobbyMemberLimit(lobby));
                }
                else
                {
                    entry.Compatibility = SteamLobbyCompatibility.Unknown;
                    entry.RunState = string.Empty;
                    entry.Members = 0;
                    entry.Capacity = 0;
                }
                SessionListChanged?.Invoke();
            }

            if (CurrentLobby.IsValid() && lobby == CurrentLobby && callback.m_bSuccess != 0)
                PublishLobbySnapshot();
        }

        private void OnLobbyChatUpdated(LobbyChatUpdate_t callback)
        {
            CSteamID lobby = new(callback.m_ulSteamIDLobby);
            if (!CurrentLobby.IsValid() || lobby != CurrentLobby)
                return;

            EChatMemberStateChange change = (EChatMemberStateChange)callback.m_rgfChatMemberStateChange;
            bool memberExited = (change & (EChatMemberStateChange.k_EChatMemberStateChangeLeft |
                                           EChatMemberStateChange.k_EChatMemberStateChangeDisconnected |
                                           EChatMemberStateChange.k_EChatMemberStateChangeKicked |
                                           EChatMemberStateChange.k_EChatMemberStateChangeBanned)) != 0;
            if (memberExited && callback.m_ulSteamIDUserChanged == Platform.LocalUserId.m_SteamID)
            {
                Debug.LogWarning($"Local Steam user left lobby {CurrentLobby.m_SteamID} ({change}).");
                ResetCurrentLobbyState(clearRichPresence: true);
                LobbySnapshotChanged?.Invoke(new SteamLobbySnapshot(CSteamID.Nil, CSteamID.Nil, 0,
                    PushUpConstants.LobbyStateWaiting, 0, Array.Empty<SteamLobbyMemberInfo>()));
                SessionError?.Invoke("The Steam lobby connection was lost. You can retry joining the same game.");
                return;
            }

            if (memberExited)
                LobbyMemberExited?.Invoke(new CSteamID(callback.m_ulSteamIDUserChanged));

            SteamLobbySnapshot snapshot = GetCurrentLobbySnapshot();
            if (IsLobbyOwner)
            {
                bool shouldJoin = _ownerAllowsJoining && !snapshot.IsFull &&
                                  snapshot.RunState != PushUpConstants.LobbyStateEnding;
                if (!Platform.SetLobbyJoinable(CurrentLobby, shouldJoin))
                    Debug.LogWarning($"Steam could not update joinability for lobby {CurrentLobby.m_SteamID} after {change}.");
            }
            SetLobbyRichPresence(CurrentLobby,
                snapshot.RunState == PushUpConstants.LobbyStateRunning ? "Playing PushUp" :
                IsLobbyOwner ? "Hosting PushUp" : "Waiting for host");
            PublishLobbySnapshot(snapshot);
        }

        private void PublishLobbySnapshot() => PublishLobbySnapshot(GetCurrentLobbySnapshot());

        private void PublishLobbySnapshot(SteamLobbySnapshot snapshot)
        {
            CSteamID previousOwner = _lastObservedLobbyOwner;
            _lastObservedLobbyOwner = snapshot.OwnerId;
            if (previousOwner.IsValid() && snapshot.OwnerId.IsValid() && previousOwner != snapshot.OwnerId)
                LobbyOwnerChanged?.Invoke(previousOwner, snapshot.OwnerId);

            LobbySnapshotChanged?.Invoke(snapshot);
            if (!_originalHostDepartureReported && snapshot.LobbyId.IsValid() && OriginalHostSteamId != 0 &&
                snapshot.OwnerId.IsValid() && snapshot.OwnerId.m_SteamID != OriginalHostSteamId)
            {
                _originalHostDepartureReported = true;
                OriginalHostLeft?.Invoke(new CSteamID(OriginalHostSteamId));
                SessionError?.Invoke("The host ended the Steam session.");
            }
        }

        private bool TryBeginOperation(SteamSessionOperationKind kind, CSteamID lobbyId, out int generation)
        {
            generation = 0;
            if (!CanBeginOperation(CurrentOperation.Kind, kind))
                return false;
            generation = ++_operationGeneration;
            CurrentOperation = new SteamSessionOperation(kind, generation, lobbyId, Time.unscaledTime);
            OperationChanged?.Invoke(CurrentOperation);
            return true;
        }

        private bool IsCurrentOperation(int generation, SteamSessionOperationKind kind) =>
            CurrentOperation.Generation == generation && CurrentOperation.Kind == kind;

        private void CompleteOperation(int generation)
        {
            if (CurrentOperation.Generation != generation)
                return;
            CurrentOperation = new SteamSessionOperation(SteamSessionOperationKind.None, generation,
                CSteamID.Nil, 0f);
            OperationChanged?.Invoke(CurrentOperation);
        }

        private void DisposePendingCreate(int generation)
        {
            if (!_pendingLobbyCreates.Remove(generation, out IDisposable result))
                return;
            result.Dispose();
        }

        private void DisposePendingJoin(int generation)
        {
            if (!_pendingLobbyJoins.Remove(generation, out IDisposable result))
                return;
            result.Dispose();
        }

        private void DisposeSteamCallbacks()
        {
            foreach (IDisposable result in _pendingLobbyCreates.Values)
                result.Dispose();
            foreach (IDisposable result in _pendingLobbyJoins.Values)
                result.Dispose();
            _pendingLobbyCreates.Clear();
            _pendingLobbyJoins.Clear();

            _joinRequested?.Dispose();
            _richPresenceJoinRequested?.Dispose();
            _lobbyInvite?.Dispose();
            _lobbyDataUpdated?.Dispose();
            _lobbyChatUpdated?.Dispose();
            _newUrlLaunchParameters?.Dispose();
            _joinRequested = null;
            _richPresenceJoinRequested = null;
            _lobbyInvite = null;
            _lobbyDataUpdated = null;
            _lobbyChatUpdated = null;
            _newUrlLaunchParameters = null;
        }

        private void ClearPendingInvite()
        {
            PendingInvite = null;
            PendingInviteChanged?.Invoke(null);
        }

        private void ClearPendingInviteForLobby(CSteamID lobby)
        {
            if (PendingInvite is SteamLobbyInvite invite && invite.LobbyId == lobby)
                ClearPendingInvite();
        }

        private bool PrepareLobbyForInvitation(out string status)
        {
            if (!RequireSteam() || !CurrentLobby.IsValid())
            {
                status = "Create or join a Steam lobby before inviting friends.";
                return false;
            }

            SteamLobbySnapshot snapshot = GetCurrentLobbySnapshot();
            if (snapshot.RunState == PushUpConstants.LobbyStateEnding)
            {
                status = "This Steam session is ending and cannot accept invitations.";
                return false;
            }
            if (snapshot.IsFull)
            {
                status = "This Steam lobby is full.";
                return false;
            }

            if (IsLobbyOwner)
            {
                // SetLobbyJoinable may have been disabled while the lobby was full. Reassert it immediately before
                // every invite so a departed player can use either a fresh invite or an old join link mid-run.
                if (!_ownerAllowsJoining)
                {
                    status = "This Steam session is not accepting new players.";
                    return false;
                }
                if (!Platform.SetLobbyJoinable(CurrentLobby, true))
                {
                    status = "Steam could not reopen this lobby for joining. Try again in a moment.";
                    return false;
                }
            }

            SetLobbyRichPresence(CurrentLobby,
                snapshot.RunState == PushUpConstants.LobbyStateRunning ? "Playing PushUp" :
                IsLobbyOwner ? "Hosting PushUp" : "Waiting for host");
            status = string.Empty;
            return true;
        }

        private void ResetCurrentLobbyState(bool clearRichPresence)
        {
            CurrentLobby = CSteamID.Nil;
            JoiningLobby = CSteamID.Nil;
            OriginalHostSteamId = 0;
            _lastObservedLobbyOwner = CSteamID.Nil;
            _originalHostDepartureReported = false;
            _ownerAllowsJoining = false;
            if (clearRichPresence && Platform.IsAvailable)
                Platform.ClearRichPresence();
        }

        private bool RequireSteam()
        {
            if (Platform.IsAvailable)
                return true;
            SessionError?.Invoke(string.IsNullOrWhiteSpace(SteamBootstrap.FailureReason)
                ? "Steam is unavailable."
                : SteamBootstrap.FailureReason);
            return false;
        }
    }
}
