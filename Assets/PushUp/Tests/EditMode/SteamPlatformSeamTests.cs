using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PushUp.Core;
using PushUp.Steam;
using Steamworks;
using UnityEngine;

namespace PushUp.Tests
{
    public sealed class SteamPlatformSeamTests
    {
        [Test]
        public void AppIdResolutionUsesExplicitPrecedenceAndStrictReleasePolicy()
        {
            Dictionary<string, string> environment = new()
            {
                [SteamAppIdResolver.AppIdEnvironmentVariable] = "222",
                ["SteamAppId"] = "333"
            };
            SteamAppIdResolution command = SteamAppIdResolver.Resolve(
                new[] { "PushUp.exe", "--steam-app-id=111" }, GetEnvironment, "444", false);
            Assert.That(command.AppId, Is.EqualTo(111));
            Assert.That(command.Source, Is.EqualTo("command line"));

            SteamAppIdResolution env = SteamAppIdResolver.Resolve(Array.Empty<string>(), GetEnvironment, "444", false);
            Assert.That(env.AppId, Is.EqualTo(222));

            SteamAppIdResolution file = SteamAppIdResolver.Resolve(Array.Empty<string>(), _ => null, " 444\r\n", false);
            Assert.That(file.AppId, Is.EqualTo(444));

            SteamAppIdResolution missing = SteamAppIdResolver.Resolve(Array.Empty<string>(), _ => null, null, false);
            Assert.That(missing.IsValid, Is.False);
            Assert.That(SteamAppIdResolver.IsAllowedForBuild(missing, false), Is.False);

            SteamAppIdResolution invalidCommand = SteamAppIdResolver.Resolve(
                new[] { "PushUp.exe", "--steam-app-id=not-a-number" }, _ => "555", "666", false);
            Assert.That(invalidCommand.IsValid, Is.False);
            Assert.That(invalidCommand.Source, Is.EqualTo("command line"));

            SteamAppIdResolution development = SteamAppIdResolver.Resolve(Array.Empty<string>(), _ => null, null, true);
            Assert.That(development.AppId, Is.EqualTo(PushUpConstants.DevelopmentSteamAppId));
            Assert.That(SteamAppIdResolver.IsAllowedForBuild(development, true), Is.True);

            SteamAppIdResolution releaseSpacewar = SteamAppIdResolver.Resolve(Array.Empty<string>(), _ => null, "480", false);
            Assert.That(SteamAppIdResolver.IsAllowedForBuild(releaseSpacewar, false), Is.False);

            SteamAppIdResolution playtest = SteamAppIdResolver.Resolve(
                new[] { "PushUp.exe", "--pushup-playtest" }, _ => null, null, false);
            Assert.That(playtest.AppId, Is.EqualTo(480));
            Assert.That(playtest.IsPlaytest, Is.True);
            Assert.That(SteamAppIdResolver.IsAllowedForBuild(playtest, false), Is.True);

            string GetEnvironment(string key) => environment.TryGetValue(key, out string value) ? value : null;
        }

        [Test]
        public void ProductionBuildConfigurationAcceptsExplicitNonDevelopmentAppId()
        {
            SteamAppIdResolution commandLine = SteamAppIdResolver.ResolveBuildConfiguration(
                new[] { "Unity.exe", "--steam-app-id=246810" }, _ => null);
            SteamAppIdResolution environment = SteamAppIdResolver.ResolveBuildConfiguration(Array.Empty<string>(),
                key => key == SteamAppIdResolver.AppIdEnvironmentVariable ? "135791" : null);

            Assert.That(SteamAppIdResolver.GetProductionBuildConfigurationError(commandLine), Is.Empty);
            Assert.That(SteamAppIdResolver.GetProductionBuildConfigurationError(environment), Is.Empty);
        }

        [Test]
        public void ProductionBuildConfigurationRejectsMissingInvalidAndSpacewarAppIds()
        {
            SteamAppIdResolution missing = SteamAppIdResolver.ResolveBuildConfiguration(
                Array.Empty<string>(), _ => null);
            SteamAppIdResolution invalid = SteamAppIdResolver.ResolveBuildConfiguration(
                new[] { "Unity.exe", "--steam-app-id=not-a-number" }, _ => null);
            SteamAppIdResolution spacewar = SteamAppIdResolver.ResolveBuildConfiguration(
                new[] { "Unity.exe", "--steam-app-id=480", "--pushup-playtest" }, _ => null);
            SteamAppIdResolution runtimeOnlySource = SteamAppIdResolver.ResolveBuildConfiguration(
                Array.Empty<string>(), key => key == "SteamAppId" ? "246810" : null);

            Assert.That(SteamAppIdResolver.GetProductionBuildConfigurationError(missing),
                Does.Contain("production Steam App ID is required"));
            Assert.That(SteamAppIdResolver.GetProductionBuildConfigurationError(invalid),
                Does.Contain("command line").And.Contain("invalid"));
            Assert.That(SteamAppIdResolver.GetProductionBuildConfigurationError(spacewar),
                Does.Contain("App ID 480").And.Contain("dedicated Steam playtest build"));
            Assert.That(SteamAppIdResolver.GetProductionBuildConfigurationError(runtimeOnlySource),
                Does.Contain("production Steam App ID is required"));
        }

        [Test]
        public void FakeBackendDrivesCachedFriendDiscoveryWithoutSteam()
        {
            FakeSteamPlatform fake = new();
            const ulong lobbyId = 109775241234567890UL;
            fake.AddFriendSession(76561198000000001UL, "Friend", lobbyId);
            GameObject gameObject = new("Steam session seam test");
            try
            {
                SteamSessionService service = gameObject.AddComponent<SteamSessionService>();
                service.SetPlatformForTests(fake);
                SteamFriendSessionInfo[] first = service.GetJoinableFriendSessions();
                SteamFriendSessionInfo[] second = service.GetJoinableFriendSessions();

                Assert.That(first, Has.Length.EqualTo(1));
                Assert.That(second, Has.Length.EqualTo(1));
                Assert.That(first[0].LobbyId.m_SteamID, Is.EqualTo(lobbyId));
                Assert.That(fake.RequestLobbyDataCount, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void FakeBackendCompletesHostOperationAndPublishesMetadata()
        {
            FakeSteamPlatform fake = new();
            GameObject gameObject = new("Steam host seam test");
            try
            {
                SteamSessionService service = gameObject.AddComponent<SteamSessionService>();
                service.SetPlatformForTests(fake);
                Assert.That(service.HostFriendsGame(), Is.True);
                Assert.That(service.IsCreatingLobby, Is.True);
                Assert.That(service.HostFriendsGame(), Is.False);

                const ulong lobbyId = 109775241234567890UL;
                fake.CompleteCreate(lobbyId, EResult.k_EResultOK);

                Assert.That(service.CurrentLobby.m_SteamID, Is.EqualTo(lobbyId));
                Assert.That(service.HasActiveOperation, Is.False);
                Assert.That(fake.GetLobbyData(new CSteamID(lobbyId), PushUpConstants.LobbyProtocolKey),
                    Is.EqualTo(PushUpConstants.ProtocolVersion));
                Assert.That(fake.GetLobbyData(new CSteamID(lobbyId), PushUpConstants.LobbyStateKey),
                    Is.EqualTo(PushUpConstants.LobbyStateWaiting));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void FakeBackendCompletesCompatibleJoinOperation()
        {
            FakeSteamPlatform fake = new();
            const ulong lobbyId = 109775241234567890UL;
            fake.ConfigureLobby(lobbyId, 76561198000000099UL, Application.version);
            GameObject gameObject = new("Steam join seam test");
            try
            {
                SteamSessionService service = gameObject.AddComponent<SteamSessionService>();
                service.SetPlatformForTests(fake);
                Assert.That(service.JoinLobby(new CSteamID(lobbyId)), Is.True);
                Assert.That(service.IsJoiningLobby, Is.True);
                Assert.That(service.JoinLobby(new CSteamID(lobbyId)), Is.False);

                fake.CompleteJoin(lobbyId, EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess);

                Assert.That(service.CurrentLobby.m_SteamID, Is.EqualTo(lobbyId));
                Assert.That(service.OriginalHostSteamId, Is.EqualTo(76561198000000099UL));
                Assert.That(service.HasActiveOperation, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void CurrentLobbyInviteReusesMembershipForTransportReconnect()
        {
            FakeSteamPlatform fake = new();
            const ulong lobbyId = 109775241234567890UL;
            fake.ConfigureLobby(lobbyId, 76561198000000099UL, Application.version);
            GameObject gameObject = new("Steam same-lobby reconnect seam test");
            try
            {
                SteamSessionService service = gameObject.AddComponent<SteamSessionService>();
                service.SetPlatformForTests(fake);
                Assert.That(service.JoinLobby(new CSteamID(lobbyId)), Is.True);
                fake.CompleteJoin(lobbyId, EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess);
                int firstJoinCalls = fake.BeginJoinLobbyCount;
                int replayedJoinedEvents = 0;
                service.LobbyJoined += _ => replayedJoinedEvents++;

                Assert.That(service.JoinLobby(new CSteamID(lobbyId), SteamJoinSource.LobbyJoinCallback), Is.True);

                Assert.That(fake.BeginJoinLobbyCount, Is.EqualTo(firstJoinCalls),
                    "An existing Steam membership must reconnect the game transport without another JoinLobby call.");
                Assert.That(replayedJoinedEvents, Is.EqualTo(1));
                Assert.That(service.CurrentLobby.m_SteamID, Is.EqualTo(lobbyId));
                Assert.That(service.HasActiveOperation, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void MidRunInviteReopensLobbyAndRefreshesJoinRichPresence()
        {
            FakeSteamPlatform fake = new();
            GameObject gameObject = new("Steam mid-run invite seam test");
            try
            {
                SteamSessionService service = gameObject.AddComponent<SteamSessionService>();
                service.SetPlatformForTests(fake);
                Assert.That(service.HostFriendsGame(), Is.True);
                const ulong lobbyId = 109775241234567890UL;
                fake.CompleteCreate(lobbyId, EResult.k_EResultOK);
                Assert.That(service.SetLobbyRunState(PushUpConstants.LobbyStateRunning, true, "Playing PushUp"),
                    Is.True);
                int joinableCallsBeforeInvite = fake.SetLobbyJoinableCount;
                CSteamID friend = new(76561198000000001UL);

                Assert.That(service.InviteFriend(friend, out string status), Is.True, status);

                Assert.That(fake.SetLobbyJoinableCount, Is.EqualTo(joinableCallsBeforeInvite + 1));
                Assert.That(fake.LastLobbyJoinable, Is.True);
                Assert.That(fake.InviteUserToLobbyCount, Is.EqualTo(1));
                Assert.That(fake.LastInvitedFriend, Is.EqualTo(friend));
                Assert.That(fake.RichPresence["connect"], Is.EqualTo($"+connect_lobby {lobbyId}"));
                Assert.That(fake.RichPresence["status"], Is.EqualTo("Playing PushUp"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void RemoteDepartureRearmsAPlayingLobbyForRejoin()
        {
            FakeSteamPlatform fake = new();
            GameObject gameObject = new("Steam departure joinability seam test");
            try
            {
                SteamSessionService service = gameObject.AddComponent<SteamSessionService>();
                service.SetPlatformForTests(fake);
                Assert.That(service.HostFriendsGame(), Is.True);
                const ulong lobbyId = 109775241234567890UL;
                fake.CompleteCreate(lobbyId, EResult.k_EResultOK);
                Assert.That(service.SetLobbyRunState(PushUpConstants.LobbyStateRunning, true, "Playing PushUp"),
                    Is.True);
                int callsBeforeDeparture = fake.SetLobbyJoinableCount;
                CSteamID departedMember = new(76561198000000001UL);
                fake.AddLobbyMember(departedMember);
                fake.RemoveLobbyMember(departedMember);
                CSteamID observedDeparture = CSteamID.Nil;
                service.LobbyMemberExited += member => observedDeparture = member;

                InvokeLobbyChatUpdate(service, new LobbyChatUpdate_t
                {
                    m_ulSteamIDLobby = lobbyId,
                    m_ulSteamIDUserChanged = 76561198000000001UL,
                    m_ulSteamIDMakingChange = 76561198000000001UL,
                    m_rgfChatMemberStateChange =
                        (uint)EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                });

                Assert.That(fake.SetLobbyJoinableCount, Is.EqualTo(callsBeforeDeparture + 1));
                Assert.That(fake.LastLobbyJoinable, Is.True);
                Assert.That(fake.RichPresence["connect"], Is.EqualTo($"+connect_lobby {lobbyId}"));
                Assert.That(observedDeparture, Is.EqualTo(departedMember));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void CurrentLobbyMembersAreNotPresentedAsSuccessfulInviteTargets()
        {
            FakeSteamPlatform fake = new();
            GameObject gameObject = new("Steam current-member invite seam test");
            try
            {
                SteamSessionService service = gameObject.AddComponent<SteamSessionService>();
                service.SetPlatformForTests(fake);
                Assert.That(service.HostFriendsGame(), Is.True);
                const ulong lobbyId = 109775241234567890UL;
                fake.CompleteCreate(lobbyId, EResult.k_EResultOK);
                const ulong friendId = 76561198000000001UL;
                fake.AddFriendSession(friendId, "Returning Friend", lobbyId);
                fake.AddLobbyMember(new CSteamID(friendId));

                SteamFriendInfo[] candidates = service.GetInviteCandidates();

                Assert.That(candidates, Has.Length.EqualTo(1));
                Assert.That(candidates[0].IsCurrentLobbyMember, Is.True);
                Assert.That(service.InviteFriend(new CSteamID(friendId), out string status), Is.False);
                Assert.That(status, Does.Contain("already in this lobby"));
                Assert.That(fake.InviteUserToLobbyCount, Is.Zero,
                    "Steam cannot use a fresh lobby invite as a transport reconnect signal for an existing member.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        private static void InvokeLobbyChatUpdate(SteamSessionService service, LobbyChatUpdate_t callback)
        {
            MethodInfo method = typeof(SteamSessionService).GetMethod("OnLobbyChatUpdated",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(service, new object[] { callback });
        }

        private sealed class FakeSteamPlatform : ISteamPlatform
        {
            private sealed class Handle : IDisposable { public void Dispose() { } }
            private readonly List<CSteamID> _friends = new();
            private readonly List<CSteamID> _lobbyMembers = new();
            private readonly Dictionary<ulong, string> _names = new();
            private readonly Dictionary<ulong, FriendGameInfo_t> _games = new();
            private readonly Dictionary<string, string> _lobbyData = new();
            private Action<LobbyCreated_t, bool> _createCompletion;
            private Action<LobbyEnter_t, bool> _joinCompletion;

            public bool IsAvailable => true;
            public uint AppId => PushUpConstants.DevelopmentSteamAppId;
            public CSteamID LocalUserId { get; } = new(76561198000000000UL);
            public bool IsOverlayEnabled => false;
            public int RequestLobbyDataCount { get; private set; }
            public int BeginJoinLobbyCount { get; private set; }
            public int SetLobbyJoinableCount { get; private set; }
            public bool LastLobbyJoinable { get; private set; }
            public int InviteUserToLobbyCount { get; private set; }
            public CSteamID LastInvitedFriend { get; private set; }
            public Dictionary<string, string> RichPresence { get; } = new();
            public CSteamID LobbyOwner { get; private set; }

            public void AddFriendSession(ulong friendId, string name, ulong lobbyId)
            {
                CSteamID friend = new(friendId);
                _friends.Add(friend);
                _names[friendId] = name;
                _games[friendId] = new FriendGameInfo_t
                {
                    m_gameID = new CGameID(new AppId_t(AppId)),
                    m_steamIDLobby = new CSteamID(lobbyId)
                };
            }

            public void CompleteCreate(ulong lobbyId, EResult result)
            {
                LobbyOwner = LocalUserId;
                _lobbyMembers.Clear();
                _lobbyMembers.Add(LocalUserId);
                Action<LobbyCreated_t, bool> completion = _createCompletion;
                _createCompletion = null;
                completion?.Invoke(new LobbyCreated_t { m_eResult = result, m_ulSteamIDLobby = lobbyId }, false);
            }

            public void ConfigureLobby(ulong lobbyId, ulong hostId, string build)
            {
                LobbyOwner = new CSteamID(hostId);
                _lobbyMembers.Clear();
                _lobbyMembers.Add(LocalUserId);
                if (LobbyOwner != LocalUserId)
                    _lobbyMembers.Add(LobbyOwner);
                _lobbyData[PushUpConstants.LobbyProtocolKey] = PushUpConstants.ProtocolVersion;
                _lobbyData[PushUpConstants.LobbyBuildKey] = build;
                _lobbyData[PushUpConstants.LobbyHostKey] = hostId.ToString();
                _lobbyData[PushUpConstants.LobbyStateKey] = PushUpConstants.LobbyStateWaiting;
            }

            public void AddLobbyMember(CSteamID member)
            {
                if (member.IsValid() && !_lobbyMembers.Contains(member))
                    _lobbyMembers.Add(member);
            }

            public void RemoveLobbyMember(CSteamID member) => _lobbyMembers.Remove(member);

            public void CompleteJoin(ulong lobbyId, EChatRoomEnterResponse response)
            {
                Action<LobbyEnter_t, bool> completion = _joinCompletion;
                _joinCompletion = null;
                completion?.Invoke(new LobbyEnter_t
                {
                    m_ulSteamIDLobby = lobbyId,
                    m_EChatRoomEnterResponse = (uint)response
                }, false);
            }

            public IDisposable BeginCreateLobby(ELobbyType lobbyType, int capacity,
                Action<LobbyCreated_t, bool> completion, out bool started)
            {
                _createCompletion = completion;
                started = true;
                return new Handle();
            }

            public IDisposable BeginJoinLobby(CSteamID lobbyId, Action<LobbyEnter_t, bool> completion,
                out bool started)
            {
                BeginJoinLobbyCount++;
                _joinCompletion = completion;
                started = true;
                return new Handle();
            }

            public CSteamID GetLobbyOwner(CSteamID lobbyId) => LobbyOwner;
            public int GetNumLobbyMembers(CSteamID lobbyId) => _lobbyMembers.Count;
            public CSteamID GetLobbyMemberByIndex(CSteamID lobbyId, int index) => _lobbyMembers[index];
            public int GetLobbyMemberLimit(CSteamID lobbyId) => PushUpConstants.MaxPlayers;
            public string GetLobbyData(CSteamID lobbyId, string key) =>
                _lobbyData.TryGetValue(key, out string value) ? value : string.Empty;
            public bool SetLobbyData(CSteamID lobbyId, string key, string value)
            {
                _lobbyData[key] = value;
                return true;
            }
            public bool SetLobbyJoinable(CSteamID lobbyId, bool joinable)
            {
                SetLobbyJoinableCount++;
                LastLobbyJoinable = joinable;
                return true;
            }
            public bool RequestLobbyData(CSteamID lobbyId) { RequestLobbyDataCount++; return true; }
            public bool InviteUserToLobby(CSteamID lobbyId, CSteamID friendId)
            {
                InviteUserToLobbyCount++;
                LastInvitedFriend = friendId;
                return true;
            }
            public void LeaveLobby(CSteamID lobbyId) { }
            public int GetFriendCount(EFriendFlags flags) => _friends.Count;
            public CSteamID GetFriendByIndex(int index, EFriendFlags flags) => _friends[index];
            public string GetFriendPersonaName(CSteamID friendId) =>
                _names.TryGetValue(friendId.m_SteamID, out string value) ? value : "Fake User";
            public EPersonaState GetFriendPersonaState(CSteamID friendId) => EPersonaState.k_EPersonaStateOnline;
            public bool GetFriendGamePlayed(CSteamID friendId, out FriendGameInfo_t gameInfo) =>
                _games.TryGetValue(friendId.m_SteamID, out gameInfo);
            public void ActivateGameOverlayInviteDialog(CSteamID lobbyId) { }
            public bool SetRichPresence(string key, string value)
            {
                RichPresence[key] = value;
                return true;
            }
            public void ClearRichPresence() => RichPresence.Clear();
            public int GetLaunchCommandLine(out string commandLine, int capacity) { commandLine = string.Empty; return 0; }
        }
    }
}
