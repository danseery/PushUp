using FishNet.Component.Transforming;
using System.Collections.Generic;
using UnityEngine;

namespace PushUp.Gameplay
{
    /// <summary>
    /// Smooths only a remote player's graphical rig. The hidden collision/network root remains current
    /// and never feeds back into the owner's camera or movement body.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class RemotePlayerPresentation : MonoBehaviour
    {
        private static readonly List<RemotePlayerPresentation> Instances = new(4);
        [SerializeField] private NetworkTransform _networkTransform;
        [SerializeField] private Transform _worldRoot;

        private readonly RemotePresentationBuffer _buffer = new();
        private readonly NetworkPresentationClock _clock = new();
        private PlayerMotor _motor;
        private Vector3 _worldRootLocalPosition;
        private Quaternion _worldRootLocalRotation;
        private NetworkSmoothingDiagnosticsSnapshot _diagnostics;
        private uint _sampledFrames;

        public NetworkSmoothingDiagnosticsSnapshot Diagnostics => _diagnostics;
        public static IReadOnlyList<RemotePlayerPresentation> ActiveInstances => Instances;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instances.Clear();

        private void Awake()
        {
            _motor = GetComponent<PlayerMotor>();
            _networkTransform ??= GetComponent<NetworkTransform>();
            _worldRoot ??= transform.Find("World Rig");
            if (_worldRoot != null)
            {
                _worldRootLocalPosition = _worldRoot.localPosition;
                _worldRootLocalRotation = _worldRoot.localRotation;
            }
        }

        private void OnEnable()
        {
            if (!Instances.Contains(this))
                Instances.Add(this);
            if (_networkTransform != null)
                _networkTransform.OnDataReceived += OnNetworkDataReceived;
        }

        private void OnDisable()
        {
            Instances.Remove(this);
            if (_networkTransform != null)
                _networkTransform.OnDataReceived -= OnNetworkDataReceived;
            ResetBuffer();
        }

        public void Configure(NetworkTransform networkTransform, Transform worldRoot)
        {
            if (isActiveAndEnabled && _networkTransform != null)
                _networkTransform.OnDataReceived -= OnNetworkDataReceived;
            _networkTransform = networkTransform;
            _worldRoot = worldRoot;
            if (_worldRoot != null)
            {
                _worldRootLocalPosition = _worldRoot.localPosition;
                _worldRootLocalRotation = _worldRoot.localRotation;
            }
            if (isActiveAndEnabled && _networkTransform != null)
                _networkTransform.OnDataReceived += OnNetworkDataReceived;
        }

        public void ResetBuffer()
        {
            _buffer.Clear();
            _clock.Reset();
            _sampledFrames = 0u;
            _diagnostics = default;
            if (_worldRoot != null)
            {
                _worldRoot.localPosition = _worldRootLocalPosition;
                _worldRoot.localRotation = _worldRootLocalRotation;
            }
        }

        private void OnNetworkDataReceived(NetworkTransform.TransformData previous,
            NetworkTransform.TransformData next)
        {
            Vector3 position = next.Position;
            Quaternion rotation = next.Rotation;
            Transform parent = transform.parent;
            if (parent != null)
            {
                position = parent.TransformPoint(position);
                rotation = parent.rotation * rotation;
            }
            double arrivalTime = Time.unscaledTimeAsDouble;
            float tickDelta = _motor != null && _motor.TimeManager != null
                ? (float)_motor.TimeManager.TickDelta
                : 1f / 60f;
            if (_buffer.Add(new RemotePoseSample(next.Tick, position, rotation, arrivalTime)))
            {
                _clock.ObserveSample(next.Tick, arrivalTime, tickDelta);
                _diagnostics.SamplesReceived++;
            }
            else
                _diagnostics.SamplesRejected++;
        }

        private void LateUpdate()
        {
            if (_worldRoot == null || _motor == null)
                return;
            if (_motor.IsLocallyControlled)
            {
                if (_worldRoot.localPosition != _worldRootLocalPosition)
                    _worldRoot.localPosition = _worldRootLocalPosition;
                _worldRoot.localRotation = _worldRootLocalRotation;
                return;
            }

            float tickDelta = _motor.TimeManager != null
                ? (float)_motor.TimeManager.TickDelta
                : 1f / 60f;
            double presentationTick = _clock.Advance(_buffer.LatestTick, tickDelta,
                Time.unscaledDeltaTime);
            if (!_buffer.TrySample(presentationTick, tickDelta, out Vector3 position,
                    out Quaternion rotation, out bool extrapolated))
            {
                _diagnostics.BufferUnderruns++;
                UpdateRates();
                return;
            }

            _sampledFrames++;
            if (extrapolated)
                _diagnostics.ExtrapolatedFrames++;
            float positionError = Vector3.Distance(_worldRoot.position, position);
            float rotationError = Quaternion.Angle(_worldRoot.rotation, rotation);
            _diagnostics.LastPositionError = positionError;
            _diagnostics.LastRotationError = rotationError;
            _diagnostics.BufferedTicks = _clock.BufferedTicks(_buffer.LatestTick);
            _diagnostics.TargetBufferedTicks = _clock.TargetDelayTicks;
            _diagnostics.ArrivalJitterMilliseconds = _clock.ArrivalJitterSeconds * 1000f;
            _diagnostics.SnapshotAgeMilliseconds = _buffer.LatestArrivalTime > 0d
                ? (float)((Time.unscaledTimeAsDouble - _buffer.LatestArrivalTime) * 1000d)
                : 0f;
            _diagnostics.PlaybackSpeed = _clock.PlaybackSpeed;
            UpdateRates();
            if (positionError > 5f)
            {
                _diagnostics.HardSnaps++;
                _worldRoot.SetPositionAndRotation(position, rotation);
                return;
            }
            _worldRoot.SetPositionAndRotation(position, rotation);
        }

        private void UpdateRates()
        {
            uint total = _sampledFrames + _diagnostics.BufferUnderruns;
            _diagnostics.UnderflowPercent = total > 0u
                ? _diagnostics.BufferUnderruns * 100f / total
                : 0f;
            _diagnostics.ExtrapolationPercent = _sampledFrames > 0u
                ? _diagnostics.ExtrapolatedFrames * 100f / _sampledFrames
                : 0f;
        }
    }
}
