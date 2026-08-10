using System;
using PushUp.Networking;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PushUp.UI
{
    /// <summary>Small opt-in runtime overlay for frame pacing and Steam round-trip latency.</summary>
    [DisallowMultipleComponent]
    public sealed class PerformanceDebugOverlay : MonoBehaviour
    {
        private const float FrameSmoothingSeconds = 0.5f;
        private const float LabelRefreshSeconds = 0.2f;

        private SteamSocketsTransport _steamTransport;
        private SessionFlowController _flow;
        private InputAction _toggleAction;
        private GameObject _root;
        private Text _label;
        private float _smoothedFrameSeconds;
        private float _nextLabelRefresh;
        private bool _visible;

        public event Action<bool> VisibilityChanged;
        public bool IsVisible => _visible;

        private void Awake()
        {
            _steamTransport = GetComponent<SteamSocketsTransport>();
            _flow = GetComponent<SessionFlowController>();
            BuildUi();
            _toggleAction = new InputAction("Toggle Performance Debug", InputActionType.Button,
                "<Keyboard>/f3");
        }

        private void OnEnable() => _toggleAction?.Enable();

        private void OnDisable() => _toggleAction?.Disable();

        private void OnDestroy() => _toggleAction?.Dispose();

        private void Update()
        {
            if (_toggleAction != null && _toggleAction.WasPressedThisFrame())
                Toggle();

            float frameSeconds = Mathf.Clamp(Time.unscaledDeltaTime, 0.0001f, 0.25f);
            if (_smoothedFrameSeconds <= 0f)
                _smoothedFrameSeconds = frameSeconds;
            else
            {
                float blend = 1f - Mathf.Exp(-frameSeconds / FrameSmoothingSeconds);
                _smoothedFrameSeconds = Mathf.Lerp(_smoothedFrameSeconds, frameSeconds, blend);
            }

            if (!_visible || Time.unscaledTime < _nextLabelRefresh)
                return;
            _nextLabelRefresh = Time.unscaledTime + LabelRefreshSeconds;
            RefreshLabel();
        }

        public void Toggle() => SetVisible(!_visible);

        public void SetVisible(bool visible)
        {
            if (_visible == visible && _root != null && _root.activeSelf == visible)
                return;
            _visible = visible;
            if (_root != null)
                _root.SetActive(visible);
            if (visible)
            {
                _nextLabelRefresh = 0f;
                RefreshLabel();
            }
            VisibilityChanged?.Invoke(visible);
        }

        public static string FormatPing(SessionMode mode, NetworkDiagnosticsSnapshot diagnostics)
        {
            if (mode is SessionMode.Offline or SessionMode.LocalDevelopment)
                return "LOCAL";
            if (mode != SessionMode.Steam)
                return "--";
            return diagnostics.HasConnectionStatus ? $"{Mathf.Max(0, diagnostics.RoundTripTimeMs)} ms" : "WAIT";
        }

        private void RefreshLabel()
        {
            if (_label == null)
                return;
            float safeFrameSeconds = Mathf.Max(0.0001f, _smoothedFrameSeconds);
            int fps = Mathf.RoundToInt(1f / safeFrameSeconds);
            float milliseconds = safeFrameSeconds * 1000f;
            SessionMode mode = _flow != null ? _flow.Mode : SessionMode.None;
            NetworkDiagnosticsSnapshot diagnostics = _steamTransport != null
                ? _steamTransport.Diagnostics
                : default;
            _label.text = $"PERFORMANCE  [F3]\nFPS  {fps}   {milliseconds:0.0} ms\nPING  {FormatPing(mode, diagnostics)}";
        }

        private void BuildUi()
        {
            if (_root != null)
                return;
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            GameObject canvasObject = new("Performance Debug Canvas", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(CanvasGroup));
            canvasObject.transform.SetParent(transform, false);
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1200;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            CanvasGroup group = canvasObject.GetComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;

            _root = new GameObject("Performance Debug", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image));
            _root.transform.SetParent(canvasObject.transform, false);
            RectTransform panel = _root.GetComponent<RectTransform>();
            panel.anchorMin = panel.anchorMax = panel.pivot = Vector2.one;
            panel.anchoredPosition = new Vector2(-24f, -24f);
            panel.sizeDelta = new Vector2(310f, 112f);
            _root.GetComponent<Image>().color = new Color(0.015f, 0.035f, 0.045f, 0.88f);

            GameObject labelObject = new("Performance Values", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Text), typeof(Outline));
            labelObject.transform.SetParent(_root.transform, false);
            RectTransform labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(16f, 10f);
            labelRect.offsetMax = new Vector2(-16f, -10f);
            _label = labelObject.GetComponent<Text>();
            _label.font = font;
            _label.fontSize = 19;
            _label.fontStyle = FontStyle.Bold;
            _label.alignment = TextAnchor.MiddleLeft;
            _label.color = new Color(0.75f, 0.95f, 1f, 1f);
            _label.raycastTarget = false;
            Outline outline = labelObject.GetComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(1f, -1f);
            _root.SetActive(false);
        }
    }
}
