using FishNet.Object;
using FishNet.Transporting;
using FishNet.Utility.Template;
using UnityEngine;

namespace PushUp.Gameplay
{
    public struct BoulderSnapshot
    {
        public uint ServerTick;
        public uint Sequence;
        public uint TeleportGeneration;
        public Vector3 Position;
        public ulong PackedRotation;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
        public bool Anchored;
        public bool Resting;

        public Quaternion Rotation => PackedQuaternion64.Unpack(PackedRotation);

        public BoulderSnapshot(uint serverTick, uint sequence, uint teleportGeneration, Vector3 position,
            Quaternion rotation, Vector3 linearVelocity, Vector3 angularVelocity, bool anchored, bool resting)
        {
            ServerTick = serverTick;
            Sequence = sequence;
            TeleportGeneration = teleportGeneration;
            Position = position;
            PackedRotation = PackedQuaternion64.Pack(rotation);
            LinearVelocity = linearVelocity;
            AngularVelocity = angularVelocity;
            Anchored = anchored;
            Resting = resting;
        }

        public bool IsFinite() => float.IsFinite(Position.x) && float.IsFinite(Position.y) &&
                                  float.IsFinite(Position.z) && float.IsFinite(LinearVelocity.x) &&
                                  float.IsFinite(LinearVelocity.y) && float.IsFinite(LinearVelocity.z) &&
                                  float.IsFinite(AngularVelocity.x) && float.IsFinite(AngularVelocity.y) &&
                                  float.IsFinite(AngularVelocity.z);
    }

    public static class PackedQuaternion64
    {
        public static ulong Pack(Quaternion rotation)
        {
            Quaternion normalized = NetworkQuaternion.NormalizeOrIdentity(rotation);
            return PackComponent(normalized.x) |
                   (PackComponent(normalized.y) << 16) |
                   (PackComponent(normalized.z) << 32) |
                   (PackComponent(normalized.w) << 48);
        }

        public static Quaternion Unpack(ulong packed)
        {
            Quaternion value = new(UnpackComponent((ushort)packed),
                UnpackComponent((ushort)(packed >> 16)),
                UnpackComponent((ushort)(packed >> 32)),
                UnpackComponent((ushort)(packed >> 48)));
            return NetworkQuaternion.NormalizeOrIdentity(value);
        }

        private static ulong PackComponent(float value) =>
            unchecked((ushort)(short)Mathf.RoundToInt(Mathf.Clamp(value, -1f, 1f) * short.MaxValue));

        private static float UnpackComponent(ushort value) => unchecked((short)value) / (float)short.MaxValue;
    }

    public sealed class BoulderSnapshotBuffer
    {
        public const int Capacity = 16;
        public const uint PlaybackDelayTicks = 4;
        public const uint MaximumExtrapolationTicks = 6;

        private readonly BoulderSnapshot[] _samples = new BoulderSnapshot[Capacity];
        private int _count;
        private bool _hasPlaybackCursor;
        private double _playbackCursor;

        public int Count => _count;
        public uint LatestTick => _count > 0 ? _samples[_count - 1].ServerTick : 0u;
        public uint LatestTeleportGeneration => _count > 0 ? _samples[_count - 1].TeleportGeneration : 0u;

        public void Clear()
        {
            _count = 0;
            _hasPlaybackCursor = false;
            _playbackCursor = 0d;
        }

        public bool Add(BoulderSnapshot sample)
        {
            if (!sample.IsFinite())
                return false;
            if (_hasPlaybackCursor && sample.ServerTick <= _playbackCursor)
                return false;
            int insertion = _count;
            while (insertion > 0 && NetworkQuaternion.IsNewer(_samples[insertion - 1].ServerTick,
                       sample.ServerTick))
                insertion--;
            if (insertion > 0 && _samples[insertion - 1].Sequence == sample.Sequence)
                return false;
            if (_count == Capacity)
            {
                if (insertion == 0)
                    return false;
                for (int i = 1; i < insertion; i++)
                    _samples[i - 1] = _samples[i];
                insertion--;
                _count--;
            }
            for (int i = _count; i > insertion; i--)
                _samples[i] = _samples[i - 1];
            _samples[insertion] = sample;
            _count++;
            return true;
        }

        public bool TrySample(double targetTick, float tickDelta, out BoulderSnapshot sampled,
            out bool extrapolated)
        {
            sampled = default;
            extrapolated = false;
            if (_count == 0)
                return false;
            if (_hasPlaybackCursor && targetTick < _playbackCursor)
                targetTick = _playbackCursor;
            _hasPlaybackCursor = true;
            _playbackCursor = targetTick;
            if (_count == 1 || targetTick <= _samples[0].ServerTick)
            {
                sampled = _samples[0];
                return true;
            }
            for (int i = 1; i < _count; i++)
            {
                BoulderSnapshot next = _samples[i];
                if (targetTick > next.ServerTick)
                    continue;
                BoulderSnapshot previous = _samples[i - 1];
                float t = Mathf.InverseLerp(previous.ServerTick, next.ServerTick, (float)targetTick);
                sampled = Interpolate(previous, next, t);
                return true;
            }

            BoulderSnapshot latest = _samples[_count - 1];
            double ticks = System.Math.Min(MaximumExtrapolationTicks,
                System.Math.Max(0d, targetTick - latest.ServerTick));
            float seconds = (float)ticks * Mathf.Max(0.0001f, tickDelta);
            sampled = latest;
            sampled.Position += Vector3.ClampMagnitude(latest.LinearVelocity,
                RemotePresentationBuffer.MaximumLinearSpeed) * seconds;
            float angularSpeed = Mathf.Min(RemotePresentationBuffer.MaximumAngularSpeed,
                latest.AngularVelocity.magnitude * Mathf.Rad2Deg);
            if (angularSpeed > 0.001f)
            {
                Quaternion extrapolatedRotation = Quaternion.AngleAxis(angularSpeed * seconds,
                    latest.AngularVelocity.normalized) * latest.Rotation;
                sampled.PackedRotation = PackedQuaternion64.Pack(extrapolatedRotation);
            }
            extrapolated = ticks > 0d;
            return true;
        }

        private static BoulderSnapshot Interpolate(BoulderSnapshot a, BoulderSnapshot b, float t)
        {
            BoulderSnapshot value = b;
            value.Position = Vector3.Lerp(a.Position, b.Position, t);
            value.PackedRotation = PackedQuaternion64.Pack(Quaternion.Slerp(a.Rotation, b.Rotation, t));
            value.LinearVelocity = Vector3.Lerp(a.LinearVelocity, b.LinearVelocity, t);
            value.AngularVelocity = Vector3.Lerp(a.AngularVelocity, b.AngularVelocity, t);
            return value;
        }
    }

    /// <summary>Host-authored boulder state with fixed-tick collision and buffered render presentation.</summary>
    [RequireComponent(typeof(Rigidbody), typeof(BoulderController))]
    public sealed class BoulderNetworkState : TickNetworkBehaviour
    {
        private const uint SnapshotIntervalTicks = 2u;
        private const float RestLinearSpeed = 0.035f;
        private const float RestAngularSpeed = 0.06f;

        [SerializeField] private Transform _presentationRoot;

        private readonly BoulderSnapshotBuffer _buffer = new();
        private Rigidbody _body;
        private BoulderController _controller;
        private uint _sequence;
        private uint _teleportGeneration;
        private uint _lastSnapshotTick;
        private bool _lastAnchored;
        private bool _lastResting;
        private float _lastMass;
        private bool _receivedInitialSnapshot;
        private Vector3 _previousProxyPosition;
        private Quaternion _previousProxyRotation;
        private Vector3 _currentProxyPosition;
        private Quaternion _currentProxyRotation;
        private float _lastProxyRealtime;
        private NetworkSmoothingDiagnosticsSnapshot _diagnostics;

        public Transform PresentationRoot => _presentationRoot != null ? _presentationRoot : transform;
        public NetworkSmoothingDiagnosticsSnapshot Diagnostics => _diagnostics;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _controller = GetComponent<BoulderController>();
            _presentationRoot ??= transform.Find("Presentation");
            _previousProxyPosition = _currentProxyPosition = transform.position;
            _previousProxyRotation = _currentProxyRotation = transform.rotation;
        }

        public void Configure(Transform presentationRoot) => _presentationRoot = presentationRoot;

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();
            SetTickCallbacks(TickCallback.Tick | TickCallback.PostTick);
            ConfigureAuthority();
            if (IsServerStarted)
                PublishReliableKeyframe();
        }

        public override void OnStopNetwork()
        {
            _buffer.Clear();
            _receivedInitialSnapshot = false;
            base.OnStopNetwork();
        }

        private void ConfigureAuthority()
        {
            if (IsServerStarted)
            {
                _body.isKinematic = _controller.IsAnchored;
                _body.interpolation = RigidbodyInterpolation.Interpolate;
                _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
            else
            {
                _body.isKinematic = true;
                _body.interpolation = RigidbodyInterpolation.None;
                _body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
        }

        protected override void TimeManager_OnTick()
        {
            if (IsServerStarted)
            {
                bool teleported = _controller.SimulateAuthorityTick();
                if (teleported)
                {
                    _teleportGeneration++;
                    PublishReliableKeyframe();
                }
                return;
            }
            if (!IsClientStarted || TimeManager == null)
                return;

            double playbackTick = TimeManager.LocalTick - BoulderSnapshotBuffer.PlaybackDelayTicks;
            if (!_buffer.TrySample(playbackTick, (float)TimeManager.TickDelta, out BoulderSnapshot snapshot,
                    out bool extrapolated))
            {
                _diagnostics.BufferUnderruns++;
                return;
            }
            if (extrapolated)
                _diagnostics.ExtrapolatedFrames++;
            _previousProxyPosition = _currentProxyPosition;
            _previousProxyRotation = _currentProxyRotation;
            _currentProxyPosition = snapshot.Position;
            _currentProxyRotation = snapshot.Rotation;
            _body.MovePosition(_currentProxyPosition);
            _body.MoveRotation(_currentProxyRotation);
            _lastProxyRealtime = Time.unscaledTime;
        }

        protected override void TimeManager_OnPostTick()
        {
            if (!IsServerStarted || TimeManager == null)
                return;
            uint tick = TimeManager.LocalTick;
            bool resting = IsResting();
            bool stateChanged = _lastAnchored != _controller.IsAnchored || _lastResting != resting ||
                                !Mathf.Approximately(_lastMass, _body.mass);
            _lastAnchored = _controller.IsAnchored;
            _lastResting = resting;
            _lastMass = _body.mass;
            if (stateChanged)
                PublishReliableKeyframe();
            if (unchecked(tick - _lastSnapshotTick) < SnapshotIntervalTicks)
                return;
            _lastSnapshotTick = tick;
            PublishMovementObserversRpc(CaptureSnapshot(tick), Channel.Unreliable);
        }

        private void LateUpdate()
        {
            if (_presentationRoot == null || IsServerStarted || !_receivedInitialSnapshot)
                return;
            float tickDelta = TimeManager != null ? (float)TimeManager.TickDelta : 1f / 60f;
            float alpha = Mathf.Clamp01((Time.unscaledTime - _lastProxyRealtime) / Mathf.Max(0.0001f, tickDelta));
            Vector3 targetPosition = Vector3.Lerp(_previousProxyPosition, _currentProxyPosition, alpha);
            Quaternion targetRotation = Quaternion.Slerp(_previousProxyRotation, _currentProxyRotation, alpha);
            float positionError = Vector3.Distance(_presentationRoot.position, targetPosition);
            float rotationError = Quaternion.Angle(_presentationRoot.rotation, targetRotation);
            _diagnostics.LastPositionError = positionError;
            _diagnostics.LastRotationError = rotationError;
            float catchUp = positionError > 0.15f || rotationError > 5f ? 0.72f : 0.45f;
            _presentationRoot.SetPositionAndRotation(
                Vector3.Lerp(_presentationRoot.position, targetPosition, catchUp),
                Quaternion.Slerp(_presentationRoot.rotation, targetRotation, catchUp));
        }

        private BoulderSnapshot CaptureSnapshot(uint tick) => new(tick, ++_sequence, _teleportGeneration,
            _body.position, _body.rotation, _body.linearVelocity, _body.angularVelocity,
            _controller.IsAnchored, IsResting());

        private bool IsResting() => _body.linearVelocity.sqrMagnitude <= RestLinearSpeed * RestLinearSpeed &&
                                    _body.angularVelocity.sqrMagnitude <= RestAngularSpeed * RestAngularSpeed;

        private void PublishReliableKeyframe()
        {
            if (!IsServerStarted)
                return;
            uint tick = TimeManager != null ? TimeManager.LocalTick : 0u;
            PublishKeyframeObserversRpc(CaptureSnapshot(tick), Channel.Reliable);
        }

        [ObserversRpc(BufferLast = true)]
        private void PublishKeyframeObserversRpc(BoulderSnapshot snapshot, Channel channel = Channel.Reliable) =>
            ReceiveSnapshot(snapshot, true);

        [ObserversRpc]
        private void PublishMovementObserversRpc(BoulderSnapshot snapshot, Channel channel = Channel.Unreliable) =>
            ReceiveSnapshot(snapshot, false);

        private void ReceiveSnapshot(BoulderSnapshot snapshot, bool keyframe)
        {
            if (IsServerStarted)
                return;
            if (!snapshot.IsFinite())
            {
                _diagnostics.SamplesRejected++;
                return;
            }
            bool teleport = !_receivedInitialSnapshot ||
                            snapshot.TeleportGeneration != _buffer.LatestTeleportGeneration;
            if (teleport)
            {
                _buffer.Clear();
                _body.position = snapshot.Position;
                _body.rotation = snapshot.Rotation;
                _previousProxyPosition = _currentProxyPosition = snapshot.Position;
                _previousProxyRotation = _currentProxyRotation = snapshot.Rotation;
                if (_presentationRoot != null)
                    _presentationRoot.SetPositionAndRotation(snapshot.Position, snapshot.Rotation);
                _diagnostics.HardSnaps++;
            }
            if (_buffer.Add(snapshot))
            {
                _diagnostics.SamplesReceived++;
                _receivedInitialSnapshot = true;
            }
            else if (!keyframe)
                _diagnostics.SamplesRejected++;
        }
    }
}
