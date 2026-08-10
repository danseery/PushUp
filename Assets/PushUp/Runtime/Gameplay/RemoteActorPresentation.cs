using FishNet.Component.Transforming;
using FishNet.Object;
using UnityEngine;

namespace PushUp.Gameplay
{
    /// <summary>Buffered graphical presentation for server-authored physics actors.</summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class RemoteActorPresentation : MonoBehaviour
    {
        [SerializeField] private NetworkTransform _networkTransform;
        [SerializeField] private Transform _worldRoot;

        private readonly RemotePresentationBuffer _buffer = new();
        private readonly NetworkPresentationClock _clock = new(6u);
        private NetworkObject _networkObject;
        private Renderer _rootRenderer;
        private bool _rootRendererInitiallyEnabled;
        private Renderer _presentationRenderer;
        private NetworkSmoothingDiagnosticsSnapshot _diagnostics;
        private uint _sampledFrames;

        public NetworkSmoothingDiagnosticsSnapshot Diagnostics => _diagnostics;

        private void Awake()
        {
            _networkObject = GetComponent<NetworkObject>();
            _networkTransform ??= GetComponent<NetworkTransform>();
            _worldRoot ??= transform.Find("World Rig");
            _rootRenderer = GetComponent<Renderer>();
            _rootRendererInitiallyEnabled = _rootRenderer != null && _rootRenderer.enabled;
            EnsurePresentationRenderer();
        }

        private void OnEnable()
        {
            if (_networkTransform != null)
                _networkTransform.OnDataReceived += OnNetworkDataReceived;
        }

        private void OnDisable()
        {
            if (_networkTransform != null)
                _networkTransform.OnDataReceived -= OnNetworkDataReceived;
            ResetPresentation();
        }

        public void Configure(NetworkTransform networkTransform, Transform worldRoot)
        {
            _networkTransform = networkTransform;
            _worldRoot = worldRoot;
        }

        private void OnNetworkDataReceived(NetworkTransform.TransformData previous,
            NetworkTransform.TransformData next)
        {
            Vector3 position = next.Position;
            Quaternion rotation = next.Rotation;
            if (transform.parent != null)
            {
                position = transform.parent.TransformPoint(position);
                rotation = transform.parent.rotation * rotation;
            }
            double arrival = Time.unscaledTimeAsDouble;
            float tickDelta = 1f / 60f;
            if (_buffer.Add(new RemotePoseSample(next.Tick, position, rotation, arrival)))
            {
                _clock.ObserveSample(next.Tick, arrival, tickDelta);
                _diagnostics.SamplesReceived++;
            }
            else
                _diagnostics.SamplesRejected++;
        }

        private void LateUpdate()
        {
            bool authoritative = _networkObject == null || !_networkObject.IsSpawned ||
                                 _networkObject.IsServerStarted;
            SetRemoteRendererMode(!authoritative);
            if (authoritative || _worldRoot == null)
                return;

            const float tickDelta = 1f / 60f;
            double tick = _clock.Advance(_buffer.LatestTick, tickDelta, Time.unscaledDeltaTime);
            if (!_buffer.TrySample(tick, tickDelta, out Vector3 position, out Quaternion rotation,
                    out bool extrapolated))
            {
                _diagnostics.BufferUnderruns++;
                return;
            }
            _sampledFrames++;
            if (extrapolated)
                _diagnostics.ExtrapolatedFrames++;
            _diagnostics.LastPositionError = Vector3.Distance(_worldRoot.position, position);
            _diagnostics.LastRotationError = Quaternion.Angle(_worldRoot.rotation, rotation);
            _diagnostics.BufferedTicks = _clock.BufferedTicks(_buffer.LatestTick);
            _diagnostics.TargetBufferedTicks = _clock.TargetDelayTicks;
            _diagnostics.ArrivalJitterMilliseconds = _clock.ArrivalJitterSeconds * 1000f;
            _diagnostics.SnapshotAgeMilliseconds = _buffer.LatestArrivalTime > 0d
                ? (float)((Time.unscaledTimeAsDouble - _buffer.LatestArrivalTime) * 1000d)
                : 0f;
            _diagnostics.PlaybackSpeed = _clock.PlaybackSpeed;
            uint total = _sampledFrames + _diagnostics.BufferUnderruns;
            _diagnostics.UnderflowPercent = total > 0u
                ? _diagnostics.BufferUnderruns * 100f / total
                : 0f;
            _diagnostics.ExtrapolationPercent = _sampledFrames > 0u
                ? _diagnostics.ExtrapolatedFrames * 100f / _sampledFrames
                : 0f;
            if (_diagnostics.LastPositionError > 5f)
                _diagnostics.HardSnaps++;
            _worldRoot.SetPositionAndRotation(position, rotation);
        }

        private void EnsurePresentationRenderer()
        {
            if (_rootRenderer == null || !_rootRendererInitiallyEnabled || _worldRoot == null ||
                _presentationRenderer != null)
                return;
            MeshFilter sourceFilter = GetComponent<MeshFilter>();
            if (sourceFilter == null)
                return;
            GameObject visual = new("Body Presentation", typeof(MeshFilter), typeof(MeshRenderer));
            visual.layer = GameplayLayers.Presentation;
            visual.transform.SetParent(_worldRoot, false);
            visual.GetComponent<MeshFilter>().sharedMesh = sourceFilter.sharedMesh;
            MeshRenderer target = visual.GetComponent<MeshRenderer>();
            target.sharedMaterials = _rootRenderer.sharedMaterials;
            target.shadowCastingMode = _rootRenderer.shadowCastingMode;
            target.receiveShadows = _rootRenderer.receiveShadows;
            _presentationRenderer = target;
        }

        private void SetRemoteRendererMode(bool remote)
        {
            if (_rootRenderer != null)
                _rootRenderer.enabled = !remote && _rootRendererInitiallyEnabled;
            if (_presentationRenderer != null)
                _presentationRenderer.enabled = remote;
        }

        private void ResetPresentation()
        {
            _buffer.Clear();
            _clock.Reset();
            _diagnostics = default;
            _sampledFrames = 0u;
            SetRemoteRendererMode(false);
        }
    }
}
