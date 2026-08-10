using NUnit.Framework;
using PushUp.Core;
using PushUp.Networking;
using PushUp.Steam;
using Steamworks;

namespace PushUp.Tests
{
    public sealed class SteamSessionJoinTests
    {
        [TestCase("+connect_lobby 109775241234567890", 109775241234567890UL)]
        [TestCase("PushUp.exe +connect_lobby \"109775241234567890\"", 109775241234567890UL)]
        [TestCase("-screen-fullscreen 0 +CONNECT_LOBBY '109775241234567890'", 109775241234567890UL)]
        public void ConnectStringParsesEverySupportedLaunchShape(string command, ulong expected)
        {
            Assert.That(SteamSessionService.TryParseLobbyConnect(command, out ulong lobby), Is.True);
            Assert.That(lobby, Is.EqualTo(expected));
        }

        [TestCase("")]
        [TestCase("+connect_lobby")]
        [TestCase("+connect_lobby nope")]
        [TestCase("+connect_lobby 0")]
        public void ConnectStringRejectsInvalidLobbyIds(string command)
        {
            Assert.That(SteamSessionService.TryParseLobbyConnect(command, out _), Is.False);
        }

        [Test]
        public void DuplicateCurrentOrPendingJoinIsIdempotentlyRejected()
        {
            Assert.That(SteamSessionService.CanBeginJoin(0, 0, 42), Is.True);
            Assert.That(SteamSessionService.CanBeginJoin(42, 0, 42), Is.False);
            Assert.That(SteamSessionService.CanBeginJoin(0, 42, 42), Is.False);
            Assert.That(SteamSessionService.CanBeginJoin(0, 42, 99), Is.False);
            Assert.That(SteamSessionService.CanBeginJoin(0, 0, 0), Is.False);
        }

        [Test]
        public void FriendSessionRequiresMatchingAppAndLobby()
        {
            Assert.That(SteamSessionService.IsJoinableFriendLobby(480, 42, 480), Is.True);
            Assert.That(SteamSessionService.IsJoinableFriendLobby(481, 42, 480), Is.False);
            Assert.That(SteamSessionService.IsJoinableFriendLobby(480, 0, 480), Is.False);
        }

        [Test]
        public void PendingInviteCarriesInviterAndLobby()
        {
            SteamLobbyInvite invite = new(new CSteamID(76561198000000000UL), "Friend", new CSteamID(109775241234567890UL));
            Assert.That(invite.IsValid, Is.True);
            Assert.That(invite.InviterName, Is.EqualTo("Friend"));
            Assert.That(invite.LobbyId.m_SteamID, Is.EqualTo(109775241234567890UL));
        }

        [Test]
        public void FriendSessionRejectsIncompatibleOrFullLobby()
        {
            CSteamID friend = new(76561198000000000UL);
            CSteamID lobby = new(109775241234567890UL);
            SteamFriendSessionInfo compatible = new(friend, "Friend", lobby,
                SteamLobbyCompatibility.Compatible, 2, 4);
            SteamFriendSessionInfo incompatible = new(friend, "Friend", lobby,
                SteamLobbyCompatibility.Incompatible, 2, 4);
            SteamFriendSessionInfo full = new(friend, "Friend", lobby,
                SteamLobbyCompatibility.Compatible, 4, 4);
            SteamFriendSessionInfo pendingMetadata = new(friend, "Friend", lobby,
                SteamLobbyCompatibility.Unknown, 2, 4);
            SteamFriendSessionInfo starting = new(friend, "Friend", lobby,
                SteamLobbyCompatibility.Compatible, 2, 4, PushUpConstants.LobbyStateStarting);
            SteamFriendSessionInfo ending = new(friend, "Friend", lobby,
                SteamLobbyCompatibility.Compatible, 2, 4, PushUpConstants.LobbyStateEnding);
            Assert.That(compatible.CanJoin, Is.True);
            Assert.That(incompatible.CanJoin, Is.False);
            Assert.That(full.CanJoin, Is.False);
            Assert.That(pendingMetadata.CanJoin, Is.False);
            Assert.That(starting.CanJoin, Is.False);
            Assert.That(ending.CanJoin, Is.False);
        }

        [Test]
        public void TimeoutAndEntryUseExplicitConnectionState()
        {
            Assert.That(SteamNetworkCoordinator.HasTimedOut(10f, 24.9f, 15f), Is.False);
            Assert.That(SteamNetworkCoordinator.HasTimedOut(10f, 25f, 15f), Is.True);
            Assert.That(SteamNetworkCoordinator.CanEnter(SteamConnectionPhase.Connecting, false, false), Is.False);
            Assert.That(SteamNetworkCoordinator.CanEnter(SteamConnectionPhase.Authenticating, true, false), Is.False);
            Assert.That(SteamNetworkCoordinator.CanEnter(SteamConnectionPhase.Connected, false, false), Is.True);
            Assert.That(SteamNetworkCoordinator.CanEnter(SteamConnectionPhase.HostTransportReady, false, true), Is.True);
            Assert.That(SteamNetworkCoordinator.CanEnter(SteamConnectionPhase.Idle, true, false), Is.False);
            Assert.That(SteamNetworkCoordinator.CanEnter(SteamConnectionPhase.Idle, false, true), Is.False);
        }

        [Test]
        public void RetryReusesCurrentClientLobbyButNeverHostLobby()
        {
            Assert.That(SteamNetworkCoordinator.ShouldReconnectCurrentLobby(42, 42, false), Is.True);
            Assert.That(SteamNetworkCoordinator.ShouldReconnectCurrentLobby(42, 42, true), Is.False);
            Assert.That(SteamNetworkCoordinator.ShouldReconnectCurrentLobby(0, 42, false), Is.False);
            Assert.That(SteamNetworkCoordinator.ShouldReconnectCurrentLobby(42, 99, false), Is.False);
        }

        [Test]
        public void TransportLossIsRecoverableWhileOriginalHostStillOwnsPlayingLobby()
        {
            Assert.That(SteamNetworkCoordinator.IsDefinitiveHostLoss(true, true,
                PushUpConstants.LobbyStateRunning), Is.False);
            Assert.That(SteamNetworkCoordinator.IsDefinitiveHostLoss(true, true,
                PushUpConstants.LobbyStateWaiting), Is.False);
            Assert.That(SteamNetworkCoordinator.IsDefinitiveHostLoss(false, true,
                PushUpConstants.LobbyStateRunning), Is.True);
            Assert.That(SteamNetworkCoordinator.IsDefinitiveHostLoss(true, false,
                PushUpConstants.LobbyStateRunning), Is.True);
            Assert.That(SteamNetworkCoordinator.IsDefinitiveHostLoss(true, true,
                PushUpConstants.LobbyStateEnding), Is.True);
            Assert.That(SteamNetworkCoordinator.ShouldRetainLobbyAfterDisconnect(false), Is.True);
            Assert.That(SteamNetworkCoordinator.ShouldRetainLobbyAfterDisconnect(true), Is.False);
            Assert.That(SteamNetworkCoordinator.ShouldProcessDisconnect(SteamConnectionPhase.Connected, false),
                Is.True);
            Assert.That(SteamNetworkCoordinator.ShouldProcessDisconnect(SteamConnectionPhase.Failed, false),
                Is.False);
            Assert.That(SteamNetworkCoordinator.ShouldProcessDisconnect(SteamConnectionPhase.Failed, true),
                Is.True, "A confirmed host departure must upgrade an earlier recoverable transport failure.");
            Assert.That(SteamNetworkCoordinator.ShouldProcessDisconnect(SteamConnectionPhase.HostEnded, true),
                Is.False);
        }

        [Test]
        public void InboundMembershipGraceAndIdentityReplacementAreBounded()
        {
            Assert.That(SteamSocketsTransport.DecideInboundConnection(false, false, 0, 1.99f, 2f),
                Is.EqualTo(SteamSocketsTransport.InboundConnectionDecision.WaitForLobbyMembership));
            Assert.That(SteamSocketsTransport.DecideInboundConnection(false, false, 0, 2f, 2f),
                Is.EqualTo(SteamSocketsTransport.InboundConnectionDecision.RejectNotLobbyMember));
            Assert.That(SteamSocketsTransport.DecideInboundConnection(true, false, 0, 0f, 2f),
                Is.EqualTo(SteamSocketsTransport.InboundConnectionDecision.Accept));
            Assert.That(SteamSocketsTransport.DecideInboundConnection(true, false, 3, 0f, 2f),
                Is.EqualTo(SteamSocketsTransport.InboundConnectionDecision.RejectLobbyFull));
            Assert.That(SteamSocketsTransport.DecideInboundConnection(true, true, 3, 0f, 2f),
                Is.EqualTo(SteamSocketsTransport.InboundConnectionDecision.ReplaceExistingIdentity),
                "A returning Steam identity must replace its old reservation even while all slots are occupied.");
            Assert.That(SteamSocketsTransport.LobbyMembershipGraceSeconds, Is.GreaterThan(0f));
            Assert.That(SteamSocketsTransport.LobbyDepartureRecheckSeconds,
                Is.GreaterThan(0f).And.LessThan(SteamSocketsTransport.LobbyMembershipGraceSeconds));
            Assert.That(SteamSocketsTransport.PreserveMembershipDeadline(102f, 101f), Is.EqualTo(102f));
            Assert.That(SteamSocketsTransport.PreserveMembershipDeadline(102f, 103f), Is.EqualTo(102f),
                "Superseding pending handles must not extend one Steam identity's grace indefinitely.");
            Assert.That(SteamSocketsTransport.CanQueuePendingIdentity(5, false), Is.True);
            Assert.That(SteamSocketsTransport.CanQueuePendingIdentity(6, false), Is.False);
            Assert.That(SteamSocketsTransport.CanQueuePendingIdentity(6, true), Is.True,
                "Replacing the same pending identity does not consume another pending slot.");
        }

        [Test]
        public void OfflineEntryIsBlockedDuringSteamOrNetworkActivity()
        {
            Assert.That(UI.PushUpMenu.CanStartOffline(SteamConnectionPhase.Idle, false, false), Is.True);
            Assert.That(UI.PushUpMenu.CanStartOffline(SteamConnectionPhase.JoiningLobby, false, false), Is.False);
            Assert.That(UI.PushUpMenu.CanStartOffline(SteamConnectionPhase.Idle, true, false), Is.False);
            Assert.That(UI.PushUpMenu.CanStartOffline(SteamConnectionPhase.Idle, false, true), Is.False);
        }

        [Test]
        public void SessionOperationsAreSingleFlightAndGenerationFriendly()
        {
            Assert.That(SteamSessionService.CanBeginOperation(SteamSessionOperationKind.None,
                SteamSessionOperationKind.CreatingLobby), Is.True);
            Assert.That(SteamSessionService.CanBeginOperation(SteamSessionOperationKind.None,
                SteamSessionOperationKind.JoiningLobby), Is.True);
            Assert.That(SteamSessionService.CanBeginOperation(SteamSessionOperationKind.CreatingLobby,
                SteamSessionOperationKind.JoiningLobby), Is.False);
            Assert.That(SteamSessionService.CanBeginOperation(SteamSessionOperationKind.JoiningLobby,
                SteamSessionOperationKind.CreatingLobby), Is.False);
            Assert.That(SteamSessionService.CanBeginOperation(SteamSessionOperationKind.None,
                SteamSessionOperationKind.None), Is.False);
        }

        [Test]
        public void LateJoinResultCannotCancelARetryForTheSameLobby()
        {
            Assert.That(SteamSessionService.ShouldLeaveStaleJoin(false, 42, 0,
                SteamSessionOperationKind.JoiningLobby, 42), Is.False);
            Assert.That(SteamSessionService.ShouldLeaveStaleJoin(false, 42, 0,
                SteamSessionOperationKind.JoiningLobby, 99), Is.True);
            Assert.That(SteamSessionService.ShouldLeaveStaleJoin(false, 42, 42,
                SteamSessionOperationKind.None, 0), Is.False);
            Assert.That(SteamSessionService.ShouldLeaveStaleJoin(true, 42, 0,
                SteamSessionOperationKind.None, 0), Is.False);
        }

        [Test]
        public void FriendLobbyMetadataRequestsAreCachedUntilStale()
        {
            Assert.That(SteamSessionService.ShouldRequestFriendLobby(false, false, float.NegativeInfinity, 0f, 5f), Is.True);
            Assert.That(SteamSessionService.ShouldRequestFriendLobby(false, false, 0f, 4.99f, 5f), Is.False);
            Assert.That(SteamSessionService.ShouldRequestFriendLobby(false, false, 0f, 5f, 5f), Is.True);
            Assert.That(SteamSessionService.ShouldRequestFriendLobby(true, false, 0f, 10f, 5f), Is.False);
            Assert.That(SteamSessionService.ShouldRequestFriendLobby(true, false, 0f, 15f, 5f), Is.True);
            Assert.That(SteamSessionService.ShouldRequestFriendLobby(false, true, 10f, 14.99f, 5f), Is.False);
            Assert.That(SteamSessionService.ShouldRequestFriendLobby(false, true, 10f, 15f, 5f), Is.True);
        }

        [TestCase("waiting", true)]
        [TestCase("starting", true)]
        [TestCase("running", true)]
        [TestCase("ending", true)]
        [TestCase("lobby", false)]
        [TestCase("", false)]
        public void LobbyRunStatesAreExplicit(string state, bool expected)
        {
            Assert.That(SteamSessionService.IsKnownRunState(state), Is.EqualTo(expected));
        }

        [Test]
        public void SteamTransportAdvertisesPayloadAfterItsChannelTrailer()
        {
            Assert.That(SteamSocketsTransport.FishNetPayloadMtu, Is.EqualTo(1199));
            Assert.That(SteamSocketsTransport.IsPayloadWithinMtu(1199), Is.True);
            Assert.That(SteamSocketsTransport.IsPayloadWithinMtu(1200), Is.False);
            Assert.That(SteamSocketsTransport.IsPayloadWithinMtu(-1), Is.False);
        }
    }
}
