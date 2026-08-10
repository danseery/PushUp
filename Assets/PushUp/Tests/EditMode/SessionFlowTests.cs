using NUnit.Framework;
using PushUp.Gameplay;
using PushUp.Networking;
using PushUp.UI;

namespace PushUp.Tests
{
    public sealed class SessionFlowTests
    {
        [Test]
        public void NewSessionRequiresNoExistingMode()
        {
            Assert.That(SessionFlowController.CanStartNewSession(SessionMode.None, SessionPhase.MainMenu), Is.True);
            Assert.That(SessionFlowController.CanStartNewSession(SessionMode.Offline, SessionPhase.InRun), Is.False);
            Assert.That(SessionFlowController.CanStartNewSession(SessionMode.Steam, SessionPhase.HostLobby), Is.False);
            Assert.That(SessionFlowController.CanStartNewSession(SessionMode.LocalDevelopment,
                SessionPhase.ConnectingTransport), Is.False);
        }

        [Test]
        public void DevelopmentBuildBadgeIncludesTheCompatibleVersion()
        {
            Assert.That(PushUpMenu.FormatDevelopmentBuildLabel("0.4.6"), Is.EqualTo("v0.4.6"));
        }

        [Test]
        public void GameplayInputRequiresAnEnteredRunAndClosedMenu()
        {
            Assert.That(SessionFlowController.ShouldEnableGameplay(SessionPhase.MainMenu, true), Is.False);
            Assert.That(SessionFlowController.ShouldEnableGameplay(SessionPhase.ClientLobby, true), Is.False);
            Assert.That(SessionFlowController.ShouldEnableGameplay(SessionPhase.ConnectingTransport, true), Is.False);
            Assert.That(SessionFlowController.ShouldEnableGameplay(SessionPhase.Authenticating, true), Is.False);
            Assert.That(SessionFlowController.ShouldEnableGameplay(SessionPhase.WaitingForPlayer, true), Is.False);
            Assert.That(SessionFlowController.ShouldEnableGameplay(SessionPhase.InRun, true), Is.False);
            Assert.That(SessionFlowController.ShouldEnableGameplay(SessionPhase.InRun, false), Is.True);
            Assert.That(SessionFlowController.ShouldEnableGameplay(SessionPhase.Results, false), Is.False);
        }

        [Test]
        public void HostUsesAnExplicitLobbyThenStartTransition()
        {
            Assert.That(SessionFlowController.CanTransition(SessionPhase.MainMenu, SessionPhase.CreatingLobby), Is.True);
            Assert.That(SessionFlowController.CanTransition(SessionPhase.CreatingLobby, SessionPhase.HostLobby), Is.True);
            Assert.That(SessionFlowController.CanTransition(SessionPhase.HostLobby, SessionPhase.StartingRun), Is.True);
            Assert.That(SessionFlowController.CanTransition(SessionPhase.StartingRun, SessionPhase.InRun), Is.True);
            Assert.That(SessionFlowController.CanTransition(SessionPhase.CreatingLobby, SessionPhase.InRun), Is.False,
                "Creating a lobby must never implicitly enter gameplay");
        }

        [Test]
        public void ClientWaitsForOwnedPlayerBeforeEnteringRun()
        {
            Assert.That(SessionFlowController.CanTransition(SessionPhase.JoiningLobby,
                SessionPhase.ConnectingTransport), Is.True);
            Assert.That(SessionFlowController.CanTransition(SessionPhase.ConnectingTransport,
                SessionPhase.Authenticating), Is.True);
            Assert.That(SessionFlowController.CanTransition(SessionPhase.Authenticating,
                SessionPhase.WaitingForPlayer), Is.True);
            Assert.That(SessionFlowController.CanTransition(SessionPhase.WaitingForPlayer,
                SessionPhase.InRun), Is.True);
            Assert.That(SessionFlowController.CanTransition(SessionPhase.JoiningLobby, SessionPhase.InRun), Is.False,
                "Lobby membership alone is not match readiness");
        }

        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(false, false, false)]
        public void ClientCannotEnterUntilAuthenticationRunAndOwnedPlayerAreReady(bool authenticated,
            bool runReady, bool playerReady)
        {
            Assert.That(SessionFlowController.IsClientReadyForGameplay(authenticated, playerReady, runReady),
                Is.False);
        }

        [Test]
        public void AuthenticatedClientWithRunAndOwnedPlayerMayEnter()
        {
            Assert.That(SessionFlowController.IsClientReadyForGameplay(true, true, true), Is.True);
        }

        [Test]
        public void OwnedPlayerReadinessWaitsForFishNetClientInitialization()
        {
            Assert.That(SessionFlowController.IsOwnedPlayerSpawnReady(true, false, true), Is.False,
                "OnSpawnedAdd occurs before FishNet permits IsOwner to become true");
            Assert.That(SessionFlowController.IsOwnedPlayerSpawnReady(true, true, true), Is.True);
            Assert.That(SessionFlowController.IsOwnedPlayerSpawnReady(false, true, true), Is.False);
            Assert.That(SessionFlowController.IsOwnedPlayerSpawnReady(true, true, false), Is.False);
            Assert.That(SessionFlowController.PlayerSpawnTimeoutSeconds, Is.EqualTo(15f));
        }

        [Test]
        public void ClientLobbyMayExplicitlyEnterTransportConnectingState()
        {
            Assert.That(SessionFlowController.CanTransition(SessionPhase.ClientLobby,
                SessionPhase.ConnectingTransport), Is.True);
            Assert.That(SessionFlowController.CanTransition(SessionPhase.ClientLobby,
                SessionPhase.InRun), Is.False,
                "A waiting lobby cannot skip transport, authentication, and owned-player readiness");
        }

        [Test]
        public void ActiveClientMayEnterVisibleReconnectGateAfterTransportLoss()
        {
            Assert.That(SessionFlowController.CanTransition(SessionPhase.InRun,
                SessionPhase.ConnectingTransport), Is.True);
            Assert.That(SessionFlowController.CanTransition(SessionPhase.Results,
                SessionPhase.ConnectingTransport), Is.True);
            Assert.That(SessionFlowController.ShouldEnableGameplay(SessionPhase.ConnectingTransport, true), Is.False);
        }

        [Test]
        public void CancelledHostStartReturnsToLobby()
        {
            Assert.That(SessionFlowController.CanTransition(SessionPhase.StartingRun,
                SessionPhase.HostLobby), Is.True);
        }

        [Test]
        public void EverySessionStateHasAnExplicitExit()
        {
            SessionPhase[] activeStates =
            {
                SessionPhase.StartingOffline,
                SessionPhase.CreatingLobby,
                SessionPhase.JoiningLobby,
                SessionPhase.ConnectingTransport,
                SessionPhase.Authenticating,
                SessionPhase.HostLobby,
                SessionPhase.ClientLobby,
                SessionPhase.WaitingForPlayer,
                SessionPhase.StartingRun,
                SessionPhase.InRun,
                SessionPhase.Results,
                SessionPhase.Error,
                SessionPhase.HostEnded
            };

            foreach (SessionPhase state in activeStates)
            {
                bool hasExit = SessionFlowController.CanTransition(state, SessionPhase.Leaving) ||
                               SessionFlowController.CanTransition(state, SessionPhase.MainMenu);
                Assert.That(hasExit, Is.True, $"{state} has no explicit leave/return transition");
            }
        }

        [Test]
        public void SnapshotSeparatesPauseFromSessionPhase()
        {
            SessionSnapshot playing = new(SessionMode.Steam, SessionPhase.InRun, "", "", false,
                false, true, true, true, "", 2, 4, false, "", false, false);
            SessionSnapshot paused = new(SessionMode.Steam, SessionPhase.InRun, "", "", true,
                false, true, true, true, "", 2, 4, false, "", false, false);
            SessionSnapshot invited = new(SessionMode.Steam, SessionPhase.InRun, "", "", true,
                false, true, true, true, "", 2, 4, true, "Friend", false, false);

            Assert.That(playing.IsPlaying, Is.True);
            Assert.That(playing.IsPauseOpen, Is.False);
            Assert.That(paused.IsPauseOpen, Is.True);
            Assert.That(invited.RequiresInviteSwitchConfirmation, Is.True);
        }

        [Test]
        public void RunningSteamSessionCanExposeInvitesWithoutChangingSessionPhase()
        {
            SessionSnapshot pausedHost = new(SessionMode.Steam, SessionPhase.InRun,
                "Invite friends directly from the list.", string.Empty, true, true, true, true, true,
                "Host", 1, 4, false, string.Empty, false, false);

            Assert.That(pausedHost.IsPauseOpen, Is.True);
            Assert.That(pausedHost.IsHost, Is.True);
            Assert.That(pausedHost.IsBusy, Is.False);
            Assert.That(SessionFlowController.ShouldEnableGameplay(pausedHost.Phase, pausedHost.MenuVisible), Is.False);
        }

        [Test]
        public void RetainedInteractionHudRequiresActiveGameplayAndVisibleSource()
        {
            Assert.That(GameplayHudPresenter.ShouldShowInteractionHud(true, true), Is.True);
            Assert.That(GameplayHudPresenter.ShouldShowInteractionHud(false, true), Is.False);
            Assert.That(GameplayHudPresenter.ShouldShowInteractionHud(true, false), Is.False);
        }

        [Test]
        public void PerformanceOverlayDistinguishesLocalAndUnavailableNetworkPing()
        {
            Assert.That(PerformanceDebugOverlay.FormatPing(SessionMode.Offline, default), Is.EqualTo("LOCAL"));
            Assert.That(PerformanceDebugOverlay.FormatPing(SessionMode.LocalDevelopment, default),
                Is.EqualTo("LOCAL"));
            Assert.That(PerformanceDebugOverlay.FormatPing(SessionMode.Steam, default), Is.EqualTo("WAIT"));
            Assert.That(PerformanceDebugOverlay.FormatPing(SessionMode.None, default), Is.EqualTo("--"));
            NetworkSmoothingDiagnosticsSnapshot smoothing = new() { SamplesReceived = 10u, BufferedTicks = 5f,
                TargetBufferedTicks = 5f, ArrivalJitterMilliseconds = 2f };
            Assert.That(PerformanceDebugOverlay.FormatSmoothing(smoothing), Does.Contain("5.0/5t"));
        }

        [Test]
        public void ThreatMarkerRejectsMenusOffscreenAndBehindCamera()
        {
            Assert.That(GameplayHudPresenter.ShouldShowThreatMarker(true, true, true, 1f, true), Is.True);
            Assert.That(GameplayHudPresenter.ShouldShowThreatMarker(false, true, true, 1f, true), Is.False);
            Assert.That(GameplayHudPresenter.ShouldShowThreatMarker(true, false, true, 1f, true), Is.False);
            Assert.That(GameplayHudPresenter.ShouldShowThreatMarker(true, true, false, 1f, true), Is.False);
            Assert.That(GameplayHudPresenter.ShouldShowThreatMarker(true, true, true, -1f, true), Is.False);
            Assert.That(GameplayHudPresenter.ShouldShowThreatMarker(true, true, true, 1f, false), Is.False);
        }
    }
}
