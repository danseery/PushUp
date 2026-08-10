using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace PushUp.Gameplay
{
    /// <summary>Observer-only, non-physical Steam persona label above a player's presented body.</summary>
    [DisallowMultipleComponent]
    public sealed class PlayerNameplate : MonoBehaviour
    {
        public const int MaximumDisplayNameLength = 32;

        private PlayerMotor _motor;
        private Transform _headAnchor;
        private RectTransform _root;
        private Text _label;
        private Camera _viewCamera;
        private string _displayName = string.Empty;

        public string DisplayName => _displayName;

        private void Awake()
        {
            _motor = GetComponent<PlayerMotor>();
            _headAnchor = transform.Find("World Rig/Torso") ?? transform.Find("World Rig") ?? transform;
        }

        public void SetIdentity(ulong steamId, string displayName)
        {
            _displayName = SanitizeDisplayName(displayName,
                steamId != 0UL ? steamId.ToString() : "Player");
            EnsureVisual();
            _label.text = _displayName;
        }

        private void LateUpdate()
        {
            if (_root == null)
                return;
            bool visible = _motor != null && !_motor.IsLocallyControlled && !string.IsNullOrEmpty(_displayName);
            if (_root.gameObject.activeSelf != visible)
                _root.gameObject.SetActive(visible);
            if (!visible)
                return;

            _viewCamera ??= Camera.main;
            _root.position = _headAnchor.position + Vector3.up * 1.25f;
            if (_viewCamera == null)
                return;
            Vector3 awayFromCamera = _root.position - _viewCamera.transform.position;
            if (awayFromCamera.sqrMagnitude > 0.001f)
                _root.rotation = Quaternion.LookRotation(awayFromCamera.normalized, Vector3.up);
        }

        private void EnsureVisual()
        {
            if (_root != null)
                return;

            GameObject canvasObject = new("Player Nameplate", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler));
            canvasObject.layer = GameplayLayers.Presentation;
            canvasObject.transform.SetParent(transform, false);
            _root = canvasObject.GetComponent<RectTransform>();
            _root.sizeDelta = new Vector2(240f, 42f);
            _root.localScale = Vector3.one * 0.008f;
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 25;

            GameObject textObject = new("Name", typeof(RectTransform), typeof(Text), typeof(Outline));
            textObject.layer = GameplayLayers.Presentation;
            textObject.transform.SetParent(_root, false);
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            _label = textObject.GetComponent<Text>();
            _label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _label.fontSize = 30;
            _label.fontStyle = FontStyle.Bold;
            _label.alignment = TextAnchor.MiddleCenter;
            _label.color = new Color(0.88f, 0.97f, 1f, 1f);
            _label.raycastTarget = false;
            _label.supportRichText = false;
            Outline outline = textObject.GetComponent<Outline>();
            outline.effectColor = new Color(0f, 0.08f, 0.11f, 0.92f);
            outline.effectDistance = new Vector2(2f, -2f);
        }

        public static string SanitizeDisplayName(string value, string fallback = "Player")
        {
            string source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            StringBuilder result = new(Mathf.Min(MaximumDisplayNameLength, source.Length));
            for (int index = 0; index < source.Length && result.Length < MaximumDisplayNameLength; index++)
            {
                char character = source[index];
                if (!char.IsControl(character))
                    result.Append(character);
            }
            return result.Length > 0 ? result.ToString() : "Player";
        }
    }
}
