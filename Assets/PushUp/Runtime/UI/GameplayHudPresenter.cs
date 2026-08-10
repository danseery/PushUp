using System;
using System.Collections.Generic;
using PushUp.Gameplay;
using UnityEngine;
using UnityEngine.UI;

namespace PushUp.UI
{
    /// <summary>Retained-mode presentation for local interaction feedback and attack-dummy threat markers.</summary>
    [DisallowMultipleComponent]
    public sealed class GameplayHudPresenter : MonoBehaviour
    {
        private sealed class DummyMarker
        {
            public AttackDummy Source;
            public Text Label;
            public Action<AttackDummyPresentationSnapshot> Listener;
            public bool Aggressive;
        }

        private readonly Dictionary<AttackDummy, DummyMarker> _dummyMarkers = new();
        private Canvas _canvas;
        private RectTransform _canvasRect;
        private GameObject _interactionRoot;
        private Text _reticle;
        private Text _interactionStatus;
        private Text _fighterStatus;
        private PlayerInteraction _localInteraction;
        private Camera _worldCamera;
        private Font _font;
        private PlayerInteractionHudSnapshot _lastHudSnapshot;
        private bool _lastGameplayEnabled;
        private bool _hudInitialized;

        private static readonly Color InteractionColor = new(0.8f, 0.95f, 1f, 1f);
        private static readonly Color ThreatColor = new(1f, 0.12f, 0.04f, 1f);

        private void Awake() => BuildUi();

        private void OnEnable()
        {
            PlayerInteraction.LocalHudSourceChanged += BindLocalInteraction;
            AttackDummy.InstanceAvailabilityChanged += OnDummyAvailabilityChanged;
            BindLocalInteraction(PlayerInteraction.LocalHudSource);
            for (int index = 0; index < AttackDummy.ActiveInstances.Count; index++)
                AddDummy(AttackDummy.ActiveInstances[index]);
        }

        private void OnDisable()
        {
            PlayerInteraction.LocalHudSourceChanged -= BindLocalInteraction;
            AttackDummy.InstanceAvailabilityChanged -= OnDummyAvailabilityChanged;
            BindLocalInteraction(null);
            AttackDummy[] sources = new AttackDummy[_dummyMarkers.Count];
            _dummyMarkers.Keys.CopyTo(sources, 0);
            for (int index = 0; index < sources.Length; index++)
                RemoveDummy(sources[index]);
        }

        private void LateUpdate()
        {
            bool gameplayEnabled = PlayerInputReader.GameplayEnabled;
            if (!_hudInitialized || gameplayEnabled != _lastGameplayEnabled)
            {
                _lastGameplayEnabled = gameplayEnabled;
                _hudInitialized = true;
                ApplyInteractionHud(_lastHudSnapshot);
            }
            UpdateDummyMarkers();
        }

        public static bool ShouldShowInteractionHud(bool gameplayEnabled, bool snapshotVisible) =>
            gameplayEnabled && snapshotVisible;

        public static bool ShouldShowThreatMarker(bool gameplayEnabled, bool aggressive, bool hasCamera,
            float cameraDepth, bool insideViewport) =>
            gameplayEnabled && aggressive && hasCamera && cameraDepth > 0f && insideViewport;

        private void BuildUi()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            GameObject canvasObject = new("Gameplay HUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
                typeof(CanvasGroup));
            canvasObject.transform.SetParent(transform, false);
            _canvas = canvasObject.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 900;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            CanvasGroup group = canvasObject.GetComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            _canvasRect = canvasObject.GetComponent<RectTransform>();

            _interactionRoot = new GameObject("Interaction", typeof(RectTransform));
            _interactionRoot.transform.SetParent(canvasObject.transform, false);
            Stretch(_interactionRoot.GetComponent<RectTransform>());

            _reticle = CreateLabel("Reticle", _interactionRoot.transform, "+", 28, Color.white,
                new Vector2(0f, 0f), new Vector2(48f, 48f));
            _interactionStatus = CreateLabel("Interaction Status", _interactionRoot.transform, string.Empty, 18,
                InteractionColor, new Vector2(0f, -42f), new Vector2(360f, 42f));
            _fighterStatus = CreateLabel("Fighter Status", _interactionRoot.transform, string.Empty, 24,
                ThreatColor, new Vector2(0f, 78f), new Vector2(520f, 48f));
            _interactionRoot.SetActive(false);
        }

        private void BindLocalInteraction(PlayerInteraction interaction)
        {
            if (_localInteraction == interaction)
                return;
            if (_localInteraction != null)
                _localInteraction.HudChanged -= OnInteractionHudChanged;
            _localInteraction = interaction;
            if (_localInteraction != null)
            {
                _localInteraction.HudChanged += OnInteractionHudChanged;
                OnInteractionHudChanged(_localInteraction.HudSnapshot);
            }
            else
                OnInteractionHudChanged(default);
        }

        private void OnInteractionHudChanged(PlayerInteractionHudSnapshot snapshot)
        {
            _lastHudSnapshot = snapshot;
            _lastGameplayEnabled = PlayerInputReader.GameplayEnabled;
            _hudInitialized = true;
            ApplyInteractionHud(snapshot);
        }

        private void ApplyInteractionHud(PlayerInteractionHudSnapshot snapshot)
        {
            if (_interactionRoot == null)
                return;
            bool visible = ShouldShowInteractionHud(PlayerInputReader.GameplayEnabled, snapshot.Visible);
            if (_interactionRoot.activeSelf != visible)
                _interactionRoot.SetActive(visible);
            if (!visible)
                return;
            if (_reticle.gameObject.activeSelf != snapshot.ReticleVisible)
                _reticle.gameObject.SetActive(snapshot.ReticleVisible);
            if (!string.Equals(_interactionStatus.text, snapshot.InteractionStatus, StringComparison.Ordinal))
                _interactionStatus.text = snapshot.InteractionStatus;
            bool interactionVisible = !string.IsNullOrWhiteSpace(snapshot.InteractionStatus);
            if (_interactionStatus.gameObject.activeSelf != interactionVisible)
                _interactionStatus.gameObject.SetActive(interactionVisible);
            if (!string.Equals(_fighterStatus.text, snapshot.FighterStatus, StringComparison.Ordinal))
                _fighterStatus.text = snapshot.FighterStatus;
            bool fighterVisible = snapshot.FighterThreatActive || !string.IsNullOrWhiteSpace(snapshot.FighterStatus);
            if (_fighterStatus.gameObject.activeSelf != fighterVisible)
                _fighterStatus.gameObject.SetActive(fighterVisible);
        }

        private void OnDummyAvailabilityChanged(AttackDummy dummy, bool available)
        {
            if (available)
                AddDummy(dummy);
            else
                RemoveDummy(dummy);
        }

        private void AddDummy(AttackDummy dummy)
        {
            if (dummy == null || _dummyMarkers.ContainsKey(dummy) || _canvasRect == null)
                return;
            Text label = CreateLabel("Attack Dummy Threat", _canvasRect, "!", 34, ThreatColor,
                Vector2.zero, new Vector2(64f, 64f));
            DummyMarker marker = new()
            {
                Source = dummy,
                Label = label,
                Aggressive = dummy.PresentationSnapshot.Aggressive
            };
            marker.Listener = snapshot =>
            {
                marker.Aggressive = snapshot.Aggressive;
                if (!snapshot.Aggressive && marker.Label != null)
                    marker.Label.gameObject.SetActive(false);
            };
            dummy.PresentationChanged += marker.Listener;
            _dummyMarkers.Add(dummy, marker);
            label.gameObject.SetActive(false);
        }

        private void RemoveDummy(AttackDummy dummy)
        {
            if (ReferenceEquals(dummy, null) || !_dummyMarkers.TryGetValue(dummy, out DummyMarker marker))
                return;
            if (marker.Source != null)
                marker.Source.PresentationChanged -= marker.Listener;
            if (marker.Label != null)
                Destroy(marker.Label.gameObject);
            _dummyMarkers.Remove(dummy);
        }

        private void UpdateDummyMarkers()
        {
            bool gameplay = PlayerInputReader.GameplayEnabled;
            if (gameplay && (_worldCamera == null || !_worldCamera.isActiveAndEnabled))
                _worldCamera = Camera.main;

            foreach (DummyMarker marker in _dummyMarkers.Values)
            {
                if (marker.Source == null || marker.Label == null)
                    continue;
                AttackDummyPresentationSnapshot snapshot = marker.Source.PresentationSnapshot;
                marker.Aggressive = snapshot.Aggressive;
                Vector3 screen = _worldCamera != null
                    ? _worldCamera.WorldToScreenPoint(snapshot.WorldPosition)
                    : Vector3.back;
                bool inside = screen.x >= 0f && screen.x <= Screen.width && screen.y >= 0f && screen.y <= Screen.height;
                bool visible = ShouldShowThreatMarker(gameplay, marker.Aggressive, _worldCamera != null, screen.z,
                    inside);
                marker.Label.gameObject.SetActive(visible);
                if (!visible)
                    continue;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null,
                        out Vector2 localPoint))
                    marker.Label.rectTransform.anchoredPosition = localPoint;
            }
        }

        private Text CreateLabel(string name, Transform parent, string value, int size, Color color,
            Vector2 anchoredPosition, Vector2 dimensions)
        {
            GameObject labelObject = new(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text),
                typeof(Outline));
            labelObject.transform.SetParent(parent, false);
            RectTransform rect = labelObject.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = dimensions;
            Text label = labelObject.GetComponent<Text>();
            label.font = _font;
            label.text = value;
            label.fontSize = size;
            label.fontStyle = FontStyle.Bold;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = color;
            label.raycastTarget = false;
            Outline outline = labelObject.GetComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(2f, -2f);
            return label;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
