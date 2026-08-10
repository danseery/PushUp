using System;
using FishNet.Managing;
using PushUp.Gameplay;
using PushUp.Networking;
using PushUp.Steam;
using Steamworks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace PushUp.UI
{
    /// <summary>State-driven uGUI front end. It never infers match readiness from menu visibility.</summary>
    public sealed class PushUpMenu : MonoBehaviour
    {
        [SerializeField] private RunDirector _runDirector;
        [SerializeField] private SteamSessionService _steamSession;
        [SerializeField] private SteamNetworkCoordinator _steamCoordinator;
        [SerializeField] private TransportSelector _transportSelector;
        [SerializeField] private NetworkManager _networkManager;
        [SerializeField] private SessionFlowController _flow;

        private PerformanceDebugOverlay _performanceOverlay;
        private Canvas _canvas;
        private GameObject _backdrop;
        private Font _font;
        private InputAction _pauseAction;
        private GameObject _mainPanel;
        private GameObject _lobbyPanel;
        private GameObject _connectingPanel;
        private GameObject _pausePanel;
        private GameObject _errorPanel;
        private GameObject _resultsPanel;
        private GameObject _settingsPanel;
        private GameObject _hudPanel;
        private GameObject _inviteToast;
        private GameObject _confirmationPanel;
        private GameObject _friendSessionList;
        private GameObject _inviteFriendList;
        private GameObject _pauseInviteFriendList;
        private Text _mainStatus;
        private Text _lobbyTitle;
        private Text _lobbyStatus;
        private Text _rosterText;
        private Text _connectingTitle;
        private Text _connectingStatus;
        private Text _pauseStatus;
        private Text _errorTitle;
        private Text _errorStatus;
        private Text _errorDiagnostic;
        private Text _hudText;
        private Text _inviteText;
        private Text _confirmationText;
        private Text _developmentBuildLabel;
        private Text _mouseSensitivityText;
        private Text _controllerSensitivityText;
        private Slider _mouseSensitivitySlider;
        private Slider _controllerSensitivitySlider;
        private Button _playOfflineButton;
        private Button _hostSteamButton;
        private Button _hostLocalButton;
        private Button _joinFriendButton;
        private Button _hostStartButton;
        private Button _lobbyInviteButton;
        private Button _pauseInviteButton;
        private Button _pauseResetButton;
        private Button _pausePerformanceButton;
        private Button _settingsPerformanceButton;
        private Button _retryButton;
        private Button _restartButton;
        private Button _inviteJoinButton;
        private Button _confirmationButton;
        private Button _firstVisibleButton;
        private Action _confirmedAction;
        private float _nextHudRefresh;
        private bool _settingsOpen;

        private const float MenuScrollSensitivity = 40f;
        private const float FriendRowHeight = 40f;

        private static readonly Color Backdrop = new(0.025f, 0.045f, 0.065f, 0.94f);
        private static readonly Color Panel = new(0.075f, 0.12f, 0.16f, 0.98f);
        private static readonly Color ButtonNormal = new(0.12f, 0.36f, 0.43f, 1f);
        private static readonly Color ButtonHighlight = new(0.18f, 0.58f, 0.67f, 1f);
        private static readonly Color TextPrimary = new(0.92f, 0.98f, 1f, 1f);
        private static readonly Color TextMuted = new(0.68f, 0.82f, 0.87f, 1f);
        private static readonly Color Warning = new(1f, 0.56f, 0.3f, 1f);

        private void Awake()
        {
            EnsureFlow();
            EnsureEventSystem();
            _performanceOverlay = GetComponent<PerformanceDebugOverlay>();
            _performanceOverlay ??= gameObject.AddComponent<PerformanceDebugOverlay>();
            _performanceOverlay.VisibilityChanged += OnPerformanceVisibilityChanged;
            BuildUi();
            if (GetComponent<GameplayHudPresenter>() == null)
                gameObject.AddComponent<GameplayHudPresenter>();

            _pauseAction = new InputAction("Pause Menu", InputActionType.Button);
            _pauseAction.AddBinding("<Keyboard>/escape");
            _pauseAction.AddBinding("<Gamepad>/start");
            _pauseAction.Enable();

            _flow.SnapshotChanged += OnSnapshotChanged;
            _flow.FriendSessionsChanged += OnFriendSessionsChanged;
            PlayerLookSettings.Changed += RefreshLookSettings;
            OnSnapshotChanged(_flow.Snapshot);
        }

        private void Update()
        {
            if (_pauseAction != null && _pauseAction.WasPressedThisFrame())
            {
                if (_settingsOpen)
                    HideSettings();
                else if (_confirmationPanel != null && _confirmationPanel.activeSelf)
                    HideConfirmation();
                else if (_flow.Phase == SessionPhase.InRun)
                {
                    if (_flow.IsMenuVisible)
                        _flow.Resume();
                    else
                        _flow.OpenPause();
                }
            }

            if (_hudPanel != null && _hudPanel.activeSelf && Time.unscaledTime >= _nextHudRefresh)
            {
                _nextHudRefresh = Time.unscaledTime + 0.1f;
                RefreshHud();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
                ApplyCursor(_flow.Snapshot);
        }

        private void OnDestroy()
        {
            if (_flow != null)
            {
                _flow.SnapshotChanged -= OnSnapshotChanged;
                _flow.FriendSessionsChanged -= OnFriendSessionsChanged;
            }
            PlayerLookSettings.Changed -= RefreshLookSettings;
            if (_performanceOverlay != null)
                _performanceOverlay.VisibilityChanged -= OnPerformanceVisibilityChanged;
            PlayerLookSettings.Save();
            _pauseAction?.Dispose();
            PlayerInputReader.SetGameplayEnabled(false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void Configure(RunDirector runDirector, SteamSessionService steamSession,
            SteamNetworkCoordinator steamCoordinator, TransportSelector transportSelector,
            NetworkManager networkManager, SessionFlowController flow = null)
        {
            _runDirector = runDirector;
            _steamSession = steamSession;
            _steamCoordinator = steamCoordinator;
            _transportSelector = transportSelector;
            _networkManager = networkManager;
            if (flow != null)
                _flow = flow;
            if (_flow != null)
                _flow.Configure(_runDirector, _steamSession, _steamCoordinator, _transportSelector, _networkManager);
        }

        public static bool CanStartOffline(SteamConnectionPhase phase, bool hasSteamLobby, bool networkStarted) =>
            (phase is SteamConnectionPhase.Idle or SteamConnectionPhase.InvitationReceived or SteamConnectionPhase.Failed) &&
            !hasSteamLobby && !networkStarted;

        private void EnsureFlow()
        {
            _runDirector ??= GetComponent<RunDirector>();
            _steamSession ??= GetComponent<SteamSessionService>();
            _steamCoordinator ??= GetComponent<SteamNetworkCoordinator>();
            _transportSelector ??= GetComponent<TransportSelector>();
            _networkManager ??= GetComponent<NetworkManager>();
            _flow ??= GetComponent<SessionFlowController>();
            _flow ??= gameObject.AddComponent<SessionFlowController>();
            _flow.Configure(_runDirector, _steamSession, _steamCoordinator, _transportSelector, _networkManager);
        }

        private void BuildUi()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            GameObject canvasObject = new("PushUp UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            _canvas = canvasObject.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 1000;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _backdrop = CreateImage("Menu Backdrop", canvasObject.transform, Backdrop);
            Stretch(_backdrop.GetComponent<RectTransform>());
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _developmentBuildLabel = CreateDevelopmentBuildLabel(canvasObject.transform, Application.version);
#endif

            _mainPanel = CreatePanel("Main Menu", canvasObject.transform, new Vector2(650f, 780f));
            CreateHeading(_mainPanel.transform, "PUSH UP");
            CreateText(_mainPanel.transform, "CO-OP BOULDER CLIMB", 22, TextMuted, TextAnchor.MiddleCenter, 38f);
            _mainStatus = CreateText(_mainPanel.transform, string.Empty, 22, TextPrimary, TextAnchor.MiddleCenter, 80f);
            _playOfflineButton = CreateButton(_mainPanel.transform, "Play Offline Hill", () => _flow.PlayOffline());
            _hostSteamButton = CreateButton(_mainPanel.transform, "Host Steam Friends Game", () => _flow.HostSteamFriends());
            _hostLocalButton = CreateButton(_mainPanel.transform, "Host Local Development Game", () => _flow.HostLocalDevelopment());
            _joinFriendButton = CreateButton(_mainPanel.transform, "Join Friend Game", ToggleFriendSessions);
            _friendSessionList = CreateScrollList(_mainPanel.transform, 190f);
            GetScrollRoot(_friendSessionList).SetActive(false);
            CreateButton(_mainPanel.transform, "Settings", ShowSettings);
            CreateButton(_mainPanel.transform, "Quit", Application.Quit);

            _lobbyPanel = CreatePanel("Lobby", canvasObject.transform, new Vector2(760f, 990f));
            _lobbyTitle = CreateHeading(_lobbyPanel.transform, "FRIENDS LOBBY");
            _lobbyStatus = CreateText(_lobbyPanel.transform, string.Empty, 22, TextPrimary, TextAnchor.MiddleCenter, 68f);
            CreateText(_lobbyPanel.transform, "PLAYERS", 19, TextMuted, TextAnchor.MiddleCenter, 32f);
            _rosterText = CreateText(_lobbyPanel.transform, string.Empty, 22, TextPrimary, TextAnchor.UpperLeft, 110f);
            _hostStartButton = CreateButton(_lobbyPanel.transform, "Start Hill", () => _flow.StartHill());
            _lobbyInviteButton = CreateButton(_lobbyPanel.transform, "Invite Friends", ToggleInviteFriends);
            _inviteFriendList = CreateScrollList(_lobbyPanel.transform, 390f);
            GetScrollRoot(_inviteFriendList).SetActive(false);
            CreateButton(_lobbyPanel.transform, "Leave Lobby", () => _flow.LeaveToMainMenu());

            _connectingPanel = CreatePanel("Connecting", canvasObject.transform, new Vector2(620f, 370f));
            _connectingTitle = CreateHeading(_connectingPanel.transform, "CONNECTING");
            _connectingStatus = CreateText(_connectingPanel.transform, string.Empty, 22, TextPrimary,
                TextAnchor.MiddleCenter, 110f);
            CreateText(_connectingPanel.transform, "This closes automatically when your player is ready.", 18,
                TextMuted, TextAnchor.MiddleCenter, 44f);
            CreateButton(_connectingPanel.transform, "Cancel", () => _flow.CancelCurrentOperation());

            _pausePanel = CreatePanel("Pause", canvasObject.transform, new Vector2(700f, 960f));
            CreateHeading(_pausePanel.transform, "SESSION MENU");
            _pauseStatus = CreateText(_pausePanel.transform, string.Empty, 20, TextPrimary, TextAnchor.MiddleCenter, 72f);
            CreateButton(_pausePanel.transform, "Resume Hill", () => _flow.Resume());
            _pauseInviteButton = CreateButton(_pausePanel.transform, "Invite Friends", TogglePauseInviteFriends);
            _pauseInviteFriendList = CreateScrollList(_pausePanel.transform, 360f);
            GetScrollRoot(_pauseInviteFriendList).SetActive(false);
            _pauseResetButton = CreateButton(_pausePanel.transform, "Reset Boulder", () => _flow.ResetBoulder());
            _pausePerformanceButton = CreateButton(_pausePanel.transform, string.Empty,
                () => _performanceOverlay.Toggle());
            CreateButton(_pausePanel.transform, "Settings", ShowSettings);
            CreateButton(_pausePanel.transform, "Leave Run", RequestLeaveFromRun);
            CreateText(_pausePanel.transform, "Multiplayer simulation continues while this menu is open.", 17,
                Warning, TextAnchor.MiddleCenter, 45f);

            _errorPanel = CreatePanel("Error", canvasObject.transform, new Vector2(680f, 520f));
            _errorTitle = CreateHeading(_errorPanel.transform, "SESSION ERROR");
            _errorStatus = CreateText(_errorPanel.transform, string.Empty, 23, TextPrimary, TextAnchor.MiddleCenter, 105f);
            _errorDiagnostic = CreateText(_errorPanel.transform, string.Empty, 16, TextMuted, TextAnchor.MiddleCenter, 90f);
            _retryButton = CreateButton(_errorPanel.transform, "Retry", () => _flow.Retry());
            CreateButton(_errorPanel.transform, "Return to Main Menu", () => _flow.ReturnAfterError());

            _resultsPanel = CreatePanel("Results", canvasObject.transform, new Vector2(620f, 480f));
            CreateHeading(_resultsPanel.transform, "SUMMIT REACHED");
            CreateText(_resultsPanel.transform, "The boulder made it. Everyone gets bragging rights.", 22,
                TextPrimary, TextAnchor.MiddleCenter, 90f);
            _restartButton = CreateButton(_resultsPanel.transform, "Restart Run", () => _flow.RestartRun());
            CreateButton(_resultsPanel.transform, "Leave Run", RequestLeaveFromRun);

            _hudPanel = CreateImage("HUD", canvasObject.transform, new Color(0.02f, 0.04f, 0.05f, 0.78f));
            RectTransform hudRect = _hudPanel.GetComponent<RectTransform>();
            hudRect.anchorMin = new Vector2(0.5f, 1f);
            hudRect.anchorMax = new Vector2(0.5f, 1f);
            hudRect.pivot = new Vector2(0.5f, 1f);
            hudRect.anchoredPosition = new Vector2(0f, -24f);
            hudRect.sizeDelta = new Vector2(760f, 118f);
            _hudText = CreateText(_hudPanel.transform, string.Empty, 19, TextPrimary, TextAnchor.MiddleCenter, 108f);
            Stretch(_hudText.rectTransform, 10f);

            _inviteToast = CreatePanel("Invite Toast", canvasObject.transform, new Vector2(560f, 230f), false);
            RectTransform toastRect = _inviteToast.GetComponent<RectTransform>();
            toastRect.anchorMin = new Vector2(1f, 1f);
            toastRect.anchorMax = new Vector2(1f, 1f);
            toastRect.pivot = new Vector2(1f, 1f);
            toastRect.anchoredPosition = new Vector2(-28f, -28f);
            _inviteText = CreateText(_inviteToast.transform, string.Empty, 20, TextPrimary, TextAnchor.MiddleCenter, 80f);
            _inviteJoinButton = CreateButton(_inviteToast.transform, "Join", RequestInviteJoin);
            CreateButton(_inviteToast.transform, "Decline", () => _flow.DeclinePendingInvite());

            _confirmationPanel = CreatePanel("Confirmation", canvasObject.transform, new Vector2(620f, 330f), false);
            _confirmationPanel.GetComponent<Image>().color = new Color(0.16f, 0.075f, 0.055f, 0.99f);
            CreateHeading(_confirmationPanel.transform, "ARE YOU SURE?");
            _confirmationText = CreateText(_confirmationPanel.transform, string.Empty, 21, TextPrimary,
                TextAnchor.MiddleCenter, 100f);
            _confirmationButton = CreateButton(_confirmationPanel.transform, "Confirm", ConfirmAction);
            CreateButton(_confirmationPanel.transform, "Cancel", HideConfirmation);

            _settingsPanel = CreatePanel("Settings", canvasObject.transform, new Vector2(680f, 550f), false);
            CreateHeading(_settingsPanel.transform, "LOOK SETTINGS");
            CreateText(_settingsPanel.transform, "Changes apply immediately and are saved on close.", 18, TextMuted,
                TextAnchor.MiddleCenter, 40f);
            _mouseSensitivityText = CreateText(_settingsPanel.transform, string.Empty, 21, TextPrimary,
                TextAnchor.MiddleCenter, 40f);
            _mouseSensitivitySlider = CreateSlider(_settingsPanel.transform, PlayerLookSettings.MinimumMouseSensitivity,
                PlayerLookSettings.MaximumMouseSensitivity, PlayerLookSettings.MouseSensitivity,
                PlayerLookSettings.SetMouseSensitivity);
            _controllerSensitivityText = CreateText(_settingsPanel.transform, string.Empty, 21, TextPrimary,
                TextAnchor.MiddleCenter, 40f);
            _controllerSensitivitySlider = CreateSlider(_settingsPanel.transform, PlayerLookSettings.MinimumControllerSensitivity,
                PlayerLookSettings.MaximumControllerSensitivity, PlayerLookSettings.ControllerSensitivity,
                PlayerLookSettings.SetControllerSensitivity);
            _settingsPerformanceButton = CreateButton(_settingsPanel.transform, string.Empty,
                () => _performanceOverlay.Toggle());
            CreateButton(_settingsPanel.transform, "Reset Defaults", PlayerLookSettings.ResetToDefaults);
            CreateButton(_settingsPanel.transform, "Back", HideSettings);

            _inviteToast.SetActive(false);
            _confirmationPanel.SetActive(false);
            _settingsPanel.SetActive(false);
            RefreshPerformanceButtons();
            SetAllPrimaryPanelsInactive();
        }

        private void OnPerformanceVisibilityChanged(bool _) => RefreshPerformanceButtons();

        private void RefreshPerformanceButtons()
        {
            string label = _performanceOverlay != null && _performanceOverlay.IsVisible
                ? "Debug Performance: ON  [F3]"
                : "Debug Performance: OFF  [F3]";
            if (_pausePerformanceButton != null)
                SetButtonText(_pausePerformanceButton, label);
            if (_settingsPerformanceButton != null)
                SetButtonText(_settingsPerformanceButton, label);
        }

        private void OnSnapshotChanged(SessionSnapshot snapshot)
        {
            if (_canvas == null)
                return;

            if (_settingsOpen)
            {
                _backdrop.SetActive(true);
                _settingsPanel.SetActive(true);
                RefreshLookSettings();
                ApplyCursor(snapshot);
                SelectFirstVisible();
                return;
            }

            SetAllPrimaryPanelsInactive();
            _backdrop.SetActive(snapshot.Phase != SessionPhase.InRun || snapshot.MenuVisible);
            _inviteToast.SetActive(snapshot.HasPendingInvite && !_confirmationPanel.activeSelf);
            if (snapshot.HasPendingInvite)
            {
                _inviteText.text = $"{snapshot.PendingInviteName} invited you to PushUp.";
                SetButtonText(_inviteJoinButton, snapshot.RequiresInviteSwitchConfirmation ? "Leave & Join" : "Join");
            }

            if (snapshot.Phase == SessionPhase.MainMenu)
            {
                _mainPanel.SetActive(true);
                _mainStatus.text = snapshot.Message;
                _hostSteamButton.gameObject.SetActive(snapshot.UsesSteamTransport);
                _hostLocalButton.gameObject.SetActive(!snapshot.UsesSteamTransport);
                _hostSteamButton.interactable = snapshot.SteamAvailable;
                _joinFriendButton.interactable = snapshot.SteamAvailable && snapshot.UsesSteamTransport;
                _firstVisibleButton = _playOfflineButton;
            }
            else if (snapshot.Phase is SessionPhase.HostLobby or SessionPhase.ClientLobby)
            {
                _lobbyPanel.SetActive(true);
                _lobbyTitle.text = snapshot.Mode == SessionMode.LocalDevelopment
                    ? "LOCAL DEVELOPMENT LOBBY"
                    : snapshot.IsHost ? "FRIENDS LOBBY - HOST" : "FRIENDS LOBBY";
                _lobbyStatus.text = string.IsNullOrWhiteSpace(snapshot.Message)
                    ? snapshot.IsHost
                        ? "Invite friends, then press Start Hill when everyone is ready."
                        : "Waiting for the host to start the hill..."
                    : snapshot.Message;
                string count = snapshot.Capacity > 0 ? $"{snapshot.MemberCount}/{snapshot.Capacity}" : snapshot.MemberCount.ToString();
                _rosterText.text = string.IsNullOrWhiteSpace(snapshot.Roster)
                    ? $"Players: {count}\nWaiting for Steam roster..."
                    : $"Players: {count}\n{snapshot.Roster}";
                _hostStartButton.gameObject.SetActive(snapshot.IsHost);
                _lobbyInviteButton.gameObject.SetActive(snapshot.Mode == SessionMode.Steam);
                _firstVisibleButton = snapshot.IsHost ? _hostStartButton : _lobbyInviteButton;
            }
            else if (snapshot.Phase is SessionPhase.StartingOffline or SessionPhase.CreatingLobby or
                     SessionPhase.JoiningLobby or SessionPhase.ConnectingTransport or SessionPhase.Authenticating or
                     SessionPhase.WaitingForPlayer or SessionPhase.StartingRun or SessionPhase.Leaving)
            {
                _connectingPanel.SetActive(true);
                _connectingTitle.text = snapshot.Phase switch
                {
                    SessionPhase.CreatingLobby => "CREATING LOBBY",
                    SessionPhase.JoiningLobby => "JOINING LOBBY",
                    SessionPhase.Authenticating => "AUTHENTICATING",
                    SessionPhase.WaitingForPlayer => "PREPARING HILL",
                    SessionPhase.StartingRun => "STARTING HILL",
                    SessionPhase.Leaving => "LEAVING",
                    _ => "CONNECTING"
                };
                _connectingStatus.text = snapshot.Message;
                _firstVisibleButton = _connectingPanel.GetComponentInChildren<Button>(true);
            }
            else if (snapshot.Phase == SessionPhase.InRun)
            {
                if (snapshot.MenuVisible)
                {
                    _pausePanel.SetActive(true);
                    _pauseStatus.text = snapshot.Message;
                    _pauseInviteButton.gameObject.SetActive(snapshot.Mode == SessionMode.Steam);
                    _pauseResetButton.gameObject.SetActive(snapshot.Mode != SessionMode.Steam || snapshot.IsHost);
                    _firstVisibleButton = _pausePanel.GetComponentInChildren<Button>(true);
                }
                else
                {
                    if (_pauseInviteFriendList != null)
                        ClosePauseInviteFriends();
                    _hudPanel.SetActive(true);
                }
            }
            else if (snapshot.Phase is SessionPhase.Error or SessionPhase.HostEnded)
            {
                _errorPanel.SetActive(true);
                _errorTitle.text = snapshot.Phase == SessionPhase.HostEnded
                    ? "HOST ENDED SESSION"
                    : snapshot.CanRetry ? "CONNECTION LOST" : "SESSION ERROR";
                _errorStatus.text = snapshot.Message;
                _errorDiagnostic.text = string.IsNullOrWhiteSpace(snapshot.Diagnostic)
                    ? snapshot.CanRetry
                        ? "The Steam lobby is still available. Rejoin or return to the main menu."
                        : "You can safely return to the main menu."
                    : "Diagnostic: " + snapshot.Diagnostic;
                _retryButton.gameObject.SetActive(snapshot.CanRetry);
                SetButtonText(_retryButton, snapshot.CanRetry ? "Rejoin Game" : "Retry");
                _firstVisibleButton = snapshot.CanRetry ? _retryButton : _errorPanel.GetComponentsInChildren<Button>(true)[^1];
            }
            else if (snapshot.Phase == SessionPhase.Results)
            {
                _resultsPanel.SetActive(true);
                // NetworkRunState has no synchronized restart operation yet. Hiding this prevents the host from
                // performing a local-only reset while every client remains in the completed-run state.
                _restartButton.gameObject.SetActive(snapshot.Mode == SessionMode.Offline);
                _firstVisibleButton = _resultsPanel.GetComponentInChildren<Button>(true);
            }

            ApplyCursor(snapshot);
            SelectFirstVisible();
        }

        private void ToggleFriendSessions()
        {
            GameObject scroll = GetScrollRoot(_friendSessionList);
            bool show = !scroll.activeSelf;
            scroll.SetActive(show);
            if (!show)
                return;
            ClearChildren(_friendSessionList.transform);
            CreateButton(_friendSessionList.transform, "Refresh Friend Games", RefreshFriendSessions);
            RefreshFriendSessions();
        }

        private void RefreshFriendSessions()
        {
            RenderFriendSessions(_flow.RefreshJoinableFriendSessions());
        }

        private void OnFriendSessionsChanged()
        {
            if (_friendSessionList != null && GetScrollRoot(_friendSessionList).activeSelf)
                RenderFriendSessions(_flow.GetJoinableFriendSessions());
        }

        private void RenderFriendSessions(SteamFriendSessionInfo[] sessions)
        {
            ClearChildren(_friendSessionList.transform);
            CreateButton(_friendSessionList.transform, "Refresh Friend Games", RefreshFriendSessions);
            if (sessions.Length == 0)
            {
                CreateText(_friendSessionList.transform,
                    "No compatible friend sessions found. Ask the host to create a friends lobby.", 17,
                    TextMuted, TextAnchor.MiddleCenter, 64f);
                return;
            }

            foreach (SteamFriendSessionInfo value in sessions)
            {
                SteamFriendSessionInfo session = value;
                string capacity = session.Capacity > 0 ? $" {session.Members}/{session.Capacity}" : string.Empty;
                string suffix = session.Compatibility switch
                {
                    SteamLobbyCompatibility.Incompatible => " - incompatible build",
                    SteamLobbyCompatibility.Unknown => " - checking build",
                    _ when session.IsFull => " - full",
                    _ => string.Empty
                };
                Button button = CreateButton(_friendSessionList.transform,
                    $"Join {session.FriendName}{capacity}{suffix}", () => _flow.JoinFriendSession(session), 44f);
                button.interactable = session.CanJoin;
            }
        }

        private void ToggleInviteFriends()
        {
            GameObject scroll = GetScrollRoot(_inviteFriendList);
            bool show = !scroll.activeSelf;
            scroll.SetActive(show);
            SetButtonText(_lobbyInviteButton, show ? "Hide Friends" : "Invite Friends");
            if (show)
                RefreshInviteFriends();
        }

        private void CloseInviteFriends()
        {
            GetScrollRoot(_inviteFriendList).SetActive(false);
            SetButtonText(_lobbyInviteButton, "Invite Friends");
        }

        private void TogglePauseInviteFriends()
        {
            GameObject scroll = GetScrollRoot(_pauseInviteFriendList);
            bool show = !scroll.activeSelf;
            scroll.SetActive(show);
            SetButtonText(_pauseInviteButton, show ? "Hide Friends" : "Invite Friends");
            if (show)
                RefreshPauseInviteFriends();
        }

        private void ClosePauseInviteFriends()
        {
            GetScrollRoot(_pauseInviteFriendList).SetActive(false);
            SetButtonText(_pauseInviteButton, "Invite Friends");
        }

        private void RefreshInviteFriends() => RenderInviteFriends(_inviteFriendList, RefreshInviteFriends,
            CloseInviteFriends);

        private void RefreshPauseInviteFriends() => RenderInviteFriends(_pauseInviteFriendList,
            RefreshPauseInviteFriends, ClosePauseInviteFriends);

        private void RenderInviteFriends(GameObject content, Action refresh, Action close)
        {
            ClearChildren(content.transform);
            if (_flow.IsInviteOverlayAvailable)
            {
                CreateButton(content.transform, "Open Steam Invite Overlay",
                    () => _flow.OpenInviteOverlay(out _), FriendRowHeight);
            }
            else
            {
                CreateText(content.transform,
                    "Steam Overlay unavailable - direct invites still work below.", 17, TextMuted,
                    TextAnchor.MiddleCenter, FriendRowHeight);
            }

            CreateButton(content.transform, "Refresh Steam Friends", refresh, FriendRowHeight);
            SteamFriendInfo[] friends = _flow.GetInviteCandidates();
            if (friends.Length == 0)
            {
                CreateText(content.transform, "No Steam friends returned. Steam may still be updating presence.",
                    17, TextMuted,
                    TextAnchor.MiddleCenter, 45f);
                CreateButton(content.transform, "Close", close, FriendRowHeight);
                return;
            }
            foreach (SteamFriendInfo value in friends)
            {
                SteamFriendInfo friend = value;
                string presence = friend.IsCurrentLobbyMember
                    ? "already in lobby"
                    : friend.IsOnline ? "online" : "offline";
                Button button = CreateButton(content.transform, $"Invite {friend.Name} ({presence})",
                    () => _flow.InviteFriend(friend.SteamId, out _), FriendRowHeight);
                button.interactable = friend.IsOnline && !friend.IsCurrentLobbyMember;
            }
            CreateButton(content.transform, "Close", close, FriendRowHeight);
        }

        private void RequestInviteJoin()
        {
            if (_flow.Snapshot.RequiresInviteSwitchConfirmation)
                ShowConfirmation($"Leave the current session and join {_flow.Snapshot.PendingInviteName}?",
                    () => _flow.ConfirmPendingInviteSwitch(), "Leave & Join");
            else
                _flow.AcceptPendingInvite();
        }

        private void RequestLeaveFromRun()
        {
            SessionSnapshot snapshot = _flow.Snapshot;
            if (snapshot.Mode == SessionMode.Steam && snapshot.IsHost)
                ShowConfirmation("End this session for every connected player?", () => _flow.LeaveToMainMenu(),
                    "End Session");
            else
                ShowConfirmation("Leave this run and return to the main menu?", () => _flow.LeaveToMainMenu(),
                    "Leave Run");
        }

        private void ShowConfirmation(string message, Action confirmed, string actionLabel)
        {
            _confirmedAction = confirmed;
            _confirmationText.text = message;
            SetButtonText(_confirmationButton, actionLabel);
            _confirmationPanel.SetActive(true);
            _inviteToast.SetActive(false);
            EventSystem.current?.SetSelectedGameObject(_confirmationButton.gameObject);
        }

        private void ConfirmAction()
        {
            Action action = _confirmedAction;
            HideConfirmation();
            action?.Invoke();
        }

        private void HideConfirmation()
        {
            _confirmedAction = null;
            _confirmationPanel.SetActive(false);
            _inviteToast.SetActive(_flow.Snapshot.HasPendingInvite);
            SelectFirstVisible();
        }

        private void ShowSettings()
        {
            _settingsOpen = true;
            SetAllPrimaryPanelsInactive();
            _backdrop.SetActive(true);
            _settingsPanel.SetActive(true);
            RefreshLookSettings();
            _firstVisibleButton = _settingsPanel.GetComponentInChildren<Button>(true);
            SelectFirstVisible();
        }

        private void HideSettings()
        {
            _settingsOpen = false;
            PlayerLookSettings.Save();
            _settingsPanel.SetActive(false);
            OnSnapshotChanged(_flow.Snapshot);
        }

        private void RefreshLookSettings()
        {
            if (_mouseSensitivitySlider != null)
                _mouseSensitivitySlider.SetValueWithoutNotify(PlayerLookSettings.MouseSensitivity);
            if (_controllerSensitivitySlider != null)
                _controllerSensitivitySlider.SetValueWithoutNotify(PlayerLookSettings.ControllerSensitivity);
            if (_mouseSensitivityText != null)
                _mouseSensitivityText.text = $"Mouse look: {PlayerLookSettings.MouseSensitivity:0.00}";
            if (_controllerSensitivityText != null)
                _controllerSensitivityText.text =
                    $"Controller look: {PlayerLookSettings.ControllerSensitivity:0}° / second";
        }

        private void RefreshHud()
        {
            if (_runDirector == null)
                return;
            NetworkRunState networkState = NetworkRunState.Active;
            BoulderController boulder = _runDirector.Boulder != null
                ? _runDirector.Boulder
                : networkState != null ? networkState.Boulder : null;
            string anchor = boulder != null && boulder.IsAnchored ||
                            networkState != null && networkState.IsBoulderAnchored
                ? "PINNED"
                : "free";
            double elapsed = _runDirector.IsRunActive ? _runDirector.ElapsedSeconds : 0d;
            if (networkState != null)
            {
                if (networkState.Phase == NetworkRunPhase.Complete)
                    elapsed = networkState.CompletionSeconds;
                else if (_networkManager != null && _networkManager.TimeManager != null)
                    elapsed = unchecked(_networkManager.TimeManager.Tick - networkState.StartedTick) *
                              _networkManager.TimeManager.TickDelta;
            }
            _hudText.text = "Hold RMB at the boulder, then push with W. Side paths hold powerups.\n" +
                            $"Run time: {elapsed:0.0}s   |   Boulder anchor: {anchor}\n" +
                            "Esc / Start opens the session menu.";
        }

        private void ApplyCursor(SessionSnapshot snapshot)
        {
            bool gameplay = SessionFlowController.ShouldEnableGameplay(snapshot.Phase, snapshot.MenuVisible);
            Cursor.lockState = gameplay ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !gameplay;
        }

        private void SetMenuVisible(bool visible)
        {
            // Compatibility entry point for existing editor tests and older scene tooling.
            if (_flow != null && _flow.Phase == SessionPhase.InRun)
            {
                if (visible) _flow.OpenPause();
                else _flow.Resume();
                return;
            }
            if (_runDirector == null && _networkManager == null)
                PlayerInputReader.SetGameplayEnabled(!visible);
        }

        private void SetAllPrimaryPanelsInactive()
        {
            _mainPanel.SetActive(false);
            _lobbyPanel.SetActive(false);
            _connectingPanel.SetActive(false);
            _pausePanel.SetActive(false);
            _errorPanel.SetActive(false);
            _resultsPanel.SetActive(false);
            _settingsPanel.SetActive(false);
            _hudPanel.SetActive(false);
        }

        private void SelectFirstVisible()
        {
            if (_firstVisibleButton == null || !_firstVisibleButton.gameObject.activeInHierarchy ||
                !_firstVisibleButton.interactable)
            {
                Button[] buttons = _canvas.GetComponentsInChildren<Button>(false);
                _firstVisibleButton = Array.Find(buttons, button => button.interactable);
            }
            if (_firstVisibleButton != null && _firstVisibleButton.gameObject.activeInHierarchy)
                EventSystem.current?.SetSelectedGameObject(_firstVisibleButton.gameObject);
        }

        private void EnsureEventSystem()
        {
            EventSystem eventSystem = FindFirstObjectByType<EventSystem>(FindObjectsInactive.Include);
            if (eventSystem == null)
            {
                GameObject eventObject = new("Event System", typeof(EventSystem));
                eventObject.transform.SetParent(transform, false);
                eventSystem = eventObject.GetComponent<EventSystem>();
            }
            foreach (BaseInputModule inputModule in eventSystem.GetComponents<BaseInputModule>())
            {
                if (inputModule is not InputSystemUIInputModule)
                    inputModule.enabled = false;
            }

            InputSystemUIInputModule module = eventSystem.GetComponent<InputSystemUIInputModule>();
            bool added = module == null;
            if (module == null)
                module = eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
            if (added || module.actionsAsset == null)
                module.AssignDefaultActions();
        }

        private GameObject CreatePanel(string name, Transform parent, Vector2 size, bool centered = true)
        {
            GameObject panel = CreateImage(name, parent, Panel);
            RectTransform rect = panel.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            if (centered)
            {
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
            }
            VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(28, 28, 24, 24);
            layout.spacing = 12f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return panel;
        }

        private GameObject CreateScrollList(Transform parent, float height)
        {
            GameObject scrollObject = CreateImage("Scroll", parent, new Color(0.025f, 0.07f, 0.09f, 0.9f));
            LayoutElement layout = scrollObject.AddComponent<LayoutElement>();
            layout.preferredHeight = height;
            layout.minHeight = height;
            ScrollRect scroll = scrollObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = MenuScrollSensitivity;

            GameObject viewport = CreateImage("Viewport", scrollObject.transform, Color.clear);
            RectTransform viewportRect = viewport.GetComponent<RectTransform>();
            Stretch(viewportRect, 6f);
            viewportRect.offsetMax = new Vector2(-24f, -6f);
            viewport.AddComponent<RectMask2D>();
            GameObject content = new("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            RectTransform contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = Vector2.zero;
            VerticalLayoutGroup group = content.GetComponent<VerticalLayoutGroup>();
            group.spacing = 6f;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewportRect;
            scroll.content = contentRect;
            scroll.verticalScrollbar = CreateVerticalScrollbar(scrollObject.transform);
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            return content;
        }

        private Scrollbar CreateVerticalScrollbar(Transform parent)
        {
            GameObject scrollbarObject = CreateImage("Scrollbar", parent, new Color(0.08f, 0.16f, 0.19f, 0.95f));
            RectTransform scrollbarRect = scrollbarObject.GetComponent<RectTransform>();
            scrollbarRect.anchorMin = new Vector2(1f, 0f);
            scrollbarRect.anchorMax = new Vector2(1f, 1f);
            scrollbarRect.pivot = new Vector2(1f, 0.5f);
            scrollbarRect.offsetMin = new Vector2(-18f, 6f);
            scrollbarRect.offsetMax = new Vector2(-6f, -6f);

            Scrollbar scrollbar = scrollbarObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.numberOfSteps = 0;

            GameObject slidingArea = new("Sliding Area", typeof(RectTransform));
            slidingArea.transform.SetParent(scrollbarObject.transform, false);
            Stretch(slidingArea.GetComponent<RectTransform>(), 2f);

            GameObject handle = CreateImage("Handle", slidingArea.transform, ButtonHighlight);
            RectTransform handleRect = handle.GetComponent<RectTransform>();
            Stretch(handleRect);
            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handle.GetComponent<Image>();
            return scrollbar;
        }

        private GameObject CreateImage(string name, Transform parent, Color color)
        {
            GameObject result = new(name, typeof(RectTransform), typeof(Image));
            result.transform.SetParent(parent, false);
            result.GetComponent<Image>().color = color;
            return result;
        }

        private Text CreateHeading(Transform parent, string text) =>
            CreateText(parent, text, 38, TextPrimary, TextAnchor.MiddleCenter, 58f, FontStyle.Bold);

        /// <summary>
        /// Unity's Development Build watermark does not include a version. Keep this small
        /// label immediately to its left so screenshots and friend playtests identify the
        /// exact compatible build without adding noise to the menu copy.
        /// </summary>
        private Text CreateDevelopmentBuildLabel(Transform parent, string version)
        {
            GameObject labelObject = new("Development Build Version", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(parent, false);
            RectTransform rect = labelObject.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-158f, 12f);
            rect.sizeDelta = new Vector2(140f, 28f);

            Text label = labelObject.GetComponent<Text>();
            label.font = _font;
            label.text = FormatDevelopmentBuildLabel(version);
            label.fontSize = 16;
            label.fontStyle = FontStyle.Bold;
            label.color = TextPrimary;
            label.alignment = TextAnchor.MiddleRight;
            label.raycastTarget = false;
            return label;
        }

        public static string FormatDevelopmentBuildLabel(string version) => $"v{version}";

        private Text CreateText(Transform parent, string value, int fontSize, Color color,
            TextAnchor alignment, float height, FontStyle style = FontStyle.Normal)
        {
            GameObject textObject = new("Text", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
            textObject.transform.SetParent(parent, false);
            Text text = textObject.GetComponent<Text>();
            text.font = _font;
            text.text = value;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            textObject.GetComponent<LayoutElement>().preferredHeight = height;
            return text;
        }

        private Button CreateButton(Transform parent, string label, Action action, float height = 52f)
        {
            GameObject buttonObject = CreateImage(label, parent, ButtonNormal);
            buttonObject.AddComponent<LayoutElement>().preferredHeight = height;
            Button button = buttonObject.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = ButtonNormal;
            colors.highlightedColor = ButtonHighlight;
            colors.selectedColor = ButtonHighlight;
            colors.pressedColor = new Color(0.08f, 0.7f, 0.76f, 1f);
            colors.disabledColor = new Color(0.12f, 0.16f, 0.18f, 0.75f);
            button.colors = colors;
            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.Automatic;
            button.navigation = navigation;
            Text text = CreateText(buttonObject.transform, label, 20, TextPrimary, TextAnchor.MiddleCenter, height);
            Stretch(text.rectTransform, 8f);
            if (action != null)
                button.onClick.AddListener(() => action());
            return button;
        }

        private Slider CreateSlider(Transform parent, float minimum, float maximum, float value,
            Action<float> changed)
        {
            GameObject sliderObject = new("Sensitivity Slider", typeof(RectTransform), typeof(LayoutElement),
                typeof(Slider));
            sliderObject.transform.SetParent(parent, false);
            LayoutElement layout = sliderObject.GetComponent<LayoutElement>();
            layout.preferredHeight = 34f;
            layout.minHeight = 34f;

            Slider slider = sliderObject.GetComponent<Slider>();
            slider.minValue = minimum;
            slider.maxValue = maximum;
            slider.wholeNumbers = false;
            slider.direction = Slider.Direction.LeftToRight;

            GameObject background = CreateImage("Background", sliderObject.transform,
                new Color(0.025f, 0.07f, 0.09f, 1f));
            RectTransform backgroundRect = background.GetComponent<RectTransform>();
            backgroundRect.anchorMin = new Vector2(0f, 0.5f);
            backgroundRect.anchorMax = new Vector2(1f, 0.5f);
            backgroundRect.offsetMin = new Vector2(12f, -6f);
            backgroundRect.offsetMax = new Vector2(-12f, 6f);

            GameObject fillArea = new("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(sliderObject.transform, false);
            RectTransform fillAreaRect = fillArea.GetComponent<RectTransform>();
            Stretch(fillAreaRect, 12f);
            GameObject fill = CreateImage("Fill", fillArea.transform, ButtonHighlight);
            RectTransform fillRect = fill.GetComponent<RectTransform>();
            Stretch(fillRect, 0f);
            slider.fillRect = fillRect;

            GameObject handleArea = new("Handle Area", typeof(RectTransform));
            handleArea.transform.SetParent(sliderObject.transform, false);
            Stretch(handleArea.GetComponent<RectTransform>(), 12f);
            GameObject handle = CreateImage("Handle", handleArea.transform, TextPrimary);
            RectTransform handleRect = handle.GetComponent<RectTransform>();
            handleRect.anchorMin = handleRect.anchorMax = new Vector2(0.5f, 0.5f);
            handleRect.sizeDelta = new Vector2(24f, 30f);
            slider.handleRect = handleRect;
            slider.targetGraphic = handle.GetComponent<Image>();

            ColorBlock colors = slider.colors;
            colors.normalColor = TextPrimary;
            colors.highlightedColor = ButtonHighlight;
            colors.selectedColor = ButtonHighlight;
            colors.pressedColor = new Color(0.08f, 0.7f, 0.76f, 1f);
            slider.colors = colors;
            slider.SetValueWithoutNotify(value);
            slider.onValueChanged.AddListener(next => changed?.Invoke(next));
            return slider;
        }

        private static void SetButtonText(Button button, string label)
        {
            Text text = button != null ? button.GetComponentInChildren<Text>(true) : null;
            if (text != null)
                text.text = label;
        }

        private static GameObject GetScrollRoot(GameObject content) =>
            content.transform.parent.parent.gameObject;

        private static void Stretch(RectTransform rect, float inset = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.one * inset;
            rect.offsetMax = Vector2.one * -inset;
        }

        private static void ClearChildren(Transform parent)
        {
            for (int index = parent.childCount - 1; index >= 0; index--)
                Destroy(parent.GetChild(index).gameObject);
        }
    }
}
