using System;
using Steamworks;

namespace PushUp.Steam
{
    /// <summary>
    /// Narrow adapter over the Steam APIs used by session orchestration. Keeping the seam here makes lobby state
    /// deterministic in EditMode tests without pretending that Steam callbacks or relay behavior can be validated
    /// without a real Steam client.
    /// </summary>
    public interface ISteamPlatform
    {
        bool IsAvailable { get; }
        uint AppId { get; }
        CSteamID LocalUserId { get; }
        bool IsOverlayEnabled { get; }

        IDisposable BeginCreateLobby(ELobbyType lobbyType, int capacity,
            Action<LobbyCreated_t, bool> completion, out bool started);
        IDisposable BeginJoinLobby(CSteamID lobbyId, Action<LobbyEnter_t, bool> completion, out bool started);

        CSteamID GetLobbyOwner(CSteamID lobbyId);
        int GetNumLobbyMembers(CSteamID lobbyId);
        CSteamID GetLobbyMemberByIndex(CSteamID lobbyId, int index);
        int GetLobbyMemberLimit(CSteamID lobbyId);
        string GetLobbyData(CSteamID lobbyId, string key);
        bool SetLobbyData(CSteamID lobbyId, string key, string value);
        bool SetLobbyJoinable(CSteamID lobbyId, bool joinable);
        bool RequestLobbyData(CSteamID lobbyId);
        bool InviteUserToLobby(CSteamID lobbyId, CSteamID friendId);
        void LeaveLobby(CSteamID lobbyId);

        int GetFriendCount(EFriendFlags flags);
        CSteamID GetFriendByIndex(int index, EFriendFlags flags);
        string GetFriendPersonaName(CSteamID friendId);
        EPersonaState GetFriendPersonaState(CSteamID friendId);
        bool GetFriendGamePlayed(CSteamID friendId, out FriendGameInfo_t gameInfo);
        void ActivateGameOverlayInviteDialog(CSteamID lobbyId);
        bool SetRichPresence(string key, string value);
        void ClearRichPresence();
        int GetLaunchCommandLine(out string commandLine, int capacity);
    }

    public sealed class SteamworksPlatform : ISteamPlatform
    {
        public bool IsAvailable => SteamBootstrap.IsAvailable;
        public uint AppId => SteamBootstrap.EffectiveAppId;
        public CSteamID LocalUserId => SteamUser.GetSteamID();
        public bool IsOverlayEnabled => SteamUtils.IsOverlayEnabled();

        public IDisposable BeginCreateLobby(ELobbyType lobbyType, int capacity,
            Action<LobbyCreated_t, bool> completion, out bool started)
        {
            CallResult<LobbyCreated_t> result = CallResult<LobbyCreated_t>.Create(
                (callback, ioFailure) => completion(callback, ioFailure));
            SteamAPICall_t call = SteamMatchmaking.CreateLobby(lobbyType, capacity);
            started = call != SteamAPICall_t.Invalid;
            if (!started)
            {
                result.Dispose();
                return null;
            }
            result.Set(call);
            return result;
        }

        public IDisposable BeginJoinLobby(CSteamID lobbyId, Action<LobbyEnter_t, bool> completion,
            out bool started)
        {
            CallResult<LobbyEnter_t> result = CallResult<LobbyEnter_t>.Create(
                (callback, ioFailure) => completion(callback, ioFailure));
            SteamAPICall_t call = SteamMatchmaking.JoinLobby(lobbyId);
            started = call != SteamAPICall_t.Invalid;
            if (!started)
            {
                result.Dispose();
                return null;
            }
            result.Set(call);
            return result;
        }

        public CSteamID GetLobbyOwner(CSteamID lobbyId) => SteamMatchmaking.GetLobbyOwner(lobbyId);
        public int GetNumLobbyMembers(CSteamID lobbyId) => SteamMatchmaking.GetNumLobbyMembers(lobbyId);
        public CSteamID GetLobbyMemberByIndex(CSteamID lobbyId, int index) =>
            SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, index);
        public int GetLobbyMemberLimit(CSteamID lobbyId) => SteamMatchmaking.GetLobbyMemberLimit(lobbyId);
        public string GetLobbyData(CSteamID lobbyId, string key) => SteamMatchmaking.GetLobbyData(lobbyId, key);
        public bool SetLobbyData(CSteamID lobbyId, string key, string value) =>
            SteamMatchmaking.SetLobbyData(lobbyId, key, value);
        public bool SetLobbyJoinable(CSteamID lobbyId, bool joinable) =>
            SteamMatchmaking.SetLobbyJoinable(lobbyId, joinable);
        public bool RequestLobbyData(CSteamID lobbyId) => SteamMatchmaking.RequestLobbyData(lobbyId);
        public bool InviteUserToLobby(CSteamID lobbyId, CSteamID friendId) =>
            SteamMatchmaking.InviteUserToLobby(lobbyId, friendId);
        public void LeaveLobby(CSteamID lobbyId) => SteamMatchmaking.LeaveLobby(lobbyId);
        public int GetFriendCount(EFriendFlags flags) => SteamFriends.GetFriendCount(flags);
        public CSteamID GetFriendByIndex(int index, EFriendFlags flags) => SteamFriends.GetFriendByIndex(index, flags);
        public string GetFriendPersonaName(CSteamID friendId) => SteamFriends.GetFriendPersonaName(friendId);
        public EPersonaState GetFriendPersonaState(CSteamID friendId) => SteamFriends.GetFriendPersonaState(friendId);
        public bool GetFriendGamePlayed(CSteamID friendId, out FriendGameInfo_t gameInfo) =>
            SteamFriends.GetFriendGamePlayed(friendId, out gameInfo);
        public void ActivateGameOverlayInviteDialog(CSteamID lobbyId) =>
            SteamFriends.ActivateGameOverlayInviteDialog(lobbyId);
        public bool SetRichPresence(string key, string value) => SteamFriends.SetRichPresence(key, value);
        public void ClearRichPresence() => SteamFriends.ClearRichPresence();
        public int GetLaunchCommandLine(out string commandLine, int capacity) =>
            SteamApps.GetLaunchCommandLine(out commandLine, capacity);
    }
}
