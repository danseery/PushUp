using System.Collections.Generic;
using UnityEngine;

namespace PushUp.Gameplay
{
    /// <summary>Small allocation-free-after-warmup pool for validated punch contact flashes.</summary>
    public sealed class PunchImpactFeedback : MonoBehaviour
    {
        public const float Lifetime = 0.38f;
        public const float InitialWorldSize = 0.14f;
        public const float FinalWorldSize = 0.34f;
        public const float SurfaceOffset = 0.035f;
        private const int PoolCapacity = 8;

        [SerializeField] private GameObject _impactPrefab;

        private readonly List<Marker> _markers = new(PoolCapacity);
        private MaterialPropertyBlock _properties;

        public GameObject ImpactPrefab => _impactPrefab;

        private void Awake() => _properties = new MaterialPropertyBlock();

        public void Configure(GameObject prefab) => _impactPrefab = prefab;

        public void Show(Transform target, Vector3 localPoint, Vector3 localNormal)
        {
            if (target == null || _impactPrefab == null)
                return;

            Vector3 worldPoint = target.TransformPoint(localPoint);
            Vector3 worldNormal = localNormal.sqrMagnitude > 0.001f
                ? target.TransformDirection(localNormal).normalized
                : -transform.forward;
            Marker marker = Acquire();
            marker.Root.transform.SetParent(target, false);
            marker.Root.transform.SetPositionAndRotation(
                worldPoint + worldNormal * SurfaceOffset,
                Quaternion.FromToRotation(Vector3.forward, worldNormal));
            SetWorldSize(marker.Root.transform, InitialWorldSize);
            marker.StartedAt = Time.time;
            marker.Root.SetActive(true);
            ApplyColor(marker, 1f);
        }

        private void Update()
        {
            for (int index = 0; index < _markers.Count; index++)
            {
                Marker marker = _markers[index];
                if (marker.Root == null || !marker.Root.activeSelf)
                    continue;

                float progress = Mathf.Clamp01((Time.time - marker.StartedAt) / Lifetime);
                SetWorldSize(marker.Root.transform, Mathf.Lerp(InitialWorldSize, FinalWorldSize, progress));
                ApplyColor(marker, 1f - progress);
                if (progress >= 1f)
                {
                    marker.Root.SetActive(false);
                    marker.Root.transform.SetParent(transform, false);
                }
            }
        }

        private Marker Acquire()
        {
            for (int index = 0; index < _markers.Count; index++)
            {
                if (_markers[index].Root != null && !_markers[index].Root.activeSelf)
                    return _markers[index];
            }

            if (_markers.Count < PoolCapacity)
            {
                Marker created = CreateMarker(_markers.Count);
                _markers.Add(created);
                return created;
            }

            Marker oldest = _markers[0];
            for (int index = 1; index < _markers.Count; index++)
            {
                if (_markers[index].StartedAt < oldest.StartedAt)
                    oldest = _markers[index];
            }
            return oldest;
        }

        private Marker CreateMarker(int index)
        {
            GameObject root = Instantiate(_impactPrefab);
            root.name = $"Punch Impact {index}";
            root.transform.SetParent(transform, false);
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            root.SetActive(false);
            return new Marker(root, renderers);
        }

        private void ApplyColor(Marker marker, float alpha)
        {
            Color color = new(1f, 0.82f, 0.18f, Mathf.Clamp01(alpha));
            _properties.Clear();
            _properties.SetColor("_BaseColor", color);
            _properties.SetColor("_Color", color);
            foreach (Renderer renderer in marker.Renderers)
                renderer.SetPropertyBlock(_properties);
        }

        private static void SetWorldSize(Transform marker, float size)
        {
            Vector3 parentScale = marker.parent != null ? marker.parent.lossyScale : Vector3.one;
            marker.localScale = new Vector3(
                size / Mathf.Max(0.0001f, Mathf.Abs(parentScale.x)),
                size / Mathf.Max(0.0001f, Mathf.Abs(parentScale.y)),
                size / Mathf.Max(0.0001f, Mathf.Abs(parentScale.z)));
        }

        private sealed class Marker
        {
            public readonly GameObject Root;
            public readonly Renderer[] Renderers;
            public float StartedAt;

            public Marker(GameObject root, Renderer[] renderers)
            {
                Root = root;
                Renderers = renderers;
            }
        }
    }
}
