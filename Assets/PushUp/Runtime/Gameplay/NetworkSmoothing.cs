using UnityEngine;

namespace PushUp.Gameplay
{
    public readonly struct RemotePoseSample
    {
        public readonly uint Tick;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly double ArrivalTime;

        public RemotePoseSample(uint tick, Vector3 position, Quaternion rotation, double arrivalTime = 0d)
        {
            Tick = tick;
            Position = position;
            Rotation = NetworkQuaternion.NormalizeOrIdentity(rotation);
            ArrivalTime = arrivalTime;
        }
    }

    public struct NetworkSmoothingDiagnosticsSnapshot
    {
        public uint SamplesReceived;
        public uint SamplesRejected;
        public uint BufferUnderruns;
        public uint ExtrapolatedFrames;
        public uint HardSnaps;
        public float LastPositionError;
        public float LastRotationError;
        public float BufferedTicks;
        public float TargetBufferedTicks;
        public float ArrivalJitterMilliseconds;
        public float SnapshotAgeMilliseconds;
        public float PlaybackSpeed;
        public float PredictionOffset;
        public float LastCorrection;
        public float UnderflowPercent;
        public float ExtrapolationPercent;
    }

    public static class NetworkQuaternion
    {
        public static bool IsFinite(Quaternion value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) &&
            float.IsFinite(value.z) && float.IsFinite(value.w);

        public static bool TryNormalize(Quaternion value, out Quaternion normalized)
        {
            float lengthSquared = value.x * value.x + value.y * value.y +
                                  value.z * value.z + value.w * value.w;
            if (!IsFinite(value) || lengthSquared < 0.00000001f)
            {
                normalized = Quaternion.identity;
                return false;
            }

            float inverseLength = 1f / Mathf.Sqrt(lengthSquared);
            normalized = new Quaternion(value.x * inverseLength, value.y * inverseLength,
                value.z * inverseLength, value.w * inverseLength);
            return true;
        }

        public static Quaternion NormalizeOrIdentity(Quaternion value) =>
            TryNormalize(value, out Quaternion normalized) ? normalized : Quaternion.identity;

        public static bool IsNewer(uint candidate, uint previous) => unchecked((int)(candidate - previous)) > 0;
    }

    /// <summary>Fixed-capacity tick buffer used only for remote graphical presentation.</summary>
    public sealed class RemotePresentationBuffer
    {
        public const int Capacity = 16;
        public const uint PlaybackDelayTicks = 5;
        public const uint MaximumExtrapolationTicks = 6;
        public const float MaximumLinearSpeed = 22f;
        public const float MaximumAngularSpeed = 720f;

        private readonly RemotePoseSample[] _samples = new RemotePoseSample[Capacity];
        private int _count;
        private bool _hasPlaybackCursor;
        private double _playbackCursor;
        private bool _hasRenderedPosition;
        private Vector3 _lastRenderedPosition;

        public int Count => _count;
        public uint LatestTick => _count > 0 ? _samples[_count - 1].Tick : 0u;
        public double LatestArrivalTime => _count > 0 ? _samples[_count - 1].ArrivalTime : 0d;

        public void Clear()
        {
            _count = 0;
            _hasPlaybackCursor = false;
            _playbackCursor = 0d;
            _hasRenderedPosition = false;
            _lastRenderedPosition = default;
        }

        public bool Add(RemotePoseSample sample)
        {
            if (!IsFinite(sample.Position) || !NetworkQuaternion.IsFinite(sample.Rotation))
                return false;
            // A late packet must never rewrite time the presentation has already displayed. It can
            // otherwise pull a remote player backward after a short extrapolation gap.
            if (_hasPlaybackCursor && sample.Tick <= _playbackCursor)
                return false;

            int insertionIndex = _count;
            while (insertionIndex > 0 && NetworkQuaternion.IsNewer(_samples[insertionIndex - 1].Tick, sample.Tick))
                insertionIndex--;
            if (insertionIndex > 0 && _samples[insertionIndex - 1].Tick == sample.Tick)
                return false;
            if (insertionIndex < _count && _samples[insertionIndex].Tick == sample.Tick)
                return false;

            if (_count == Capacity)
            {
                if (insertionIndex == 0)
                    return false;
                for (int index = 1; index < insertionIndex; index++)
                    _samples[index - 1] = _samples[index];
                insertionIndex--;
                _count--;
            }

            for (int index = _count; index > insertionIndex; index--)
                _samples[index] = _samples[index - 1];
            _samples[insertionIndex] = sample;
            _count++;
            return true;
        }

        public bool TrySample(double targetTick, float tickDelta, out Vector3 position,
            out Quaternion rotation, out bool extrapolated)
        {
            position = default;
            rotation = Quaternion.identity;
            extrapolated = false;
            if (_count == 0)
                return false;
            if (_hasPlaybackCursor && targetTick < _playbackCursor)
                targetTick = _playbackCursor;
            _hasPlaybackCursor = true;
            _playbackCursor = targetTick;
            if (_count == 1 || targetTick <= _samples[0].Tick)
            {
                position = PreserveAuthoritativeDirection(_samples[0].Position, Vector3.zero);
                rotation = _samples[0].Rotation;
                return true;
            }

            for (int index = 1; index < _count; index++)
            {
                RemotePoseSample next = _samples[index];
                if (targetTick > next.Tick)
                    continue;
                RemotePoseSample previous = _samples[index - 1];
                double spanTicks = Mathf.Max(1f, unchecked(next.Tick - previous.Tick));
                float t = Mathf.Clamp01((float)((targetTick - previous.Tick) / spanTicks));
                Vector3 previousVelocity = SampleVelocity(Mathf.Max(0, index - 2), index - 1, tickDelta);
                Vector3 nextVelocity = SampleVelocity(index - 1, index, tickDelta);
                float spanSeconds = (float)spanTicks * Mathf.Max(0.0001f, tickDelta);
                Vector3 linear = Vector3.Lerp(previous.Position, next.Position, t);
                Vector3 hermite = Hermite(previous.Position, previousVelocity * spanSeconds,
                    next.Position, nextVelocity * spanSeconds, t);
                Vector3 candidate = Vector3.Distance(hermite, linear) <= 0.25f ? hermite : linear;
                position = PreserveAuthoritativeDirection(candidate, next.Position - previous.Position);
                rotation = Quaternion.Slerp(previous.Rotation, ShortestPath(previous.Rotation, next.Rotation), t);
                return true;
            }

            RemotePoseSample latest = _samples[_count - 1];
            if (_count < 2)
            {
                position = PreserveAuthoritativeDirection(latest.Position, Vector3.zero);
                rotation = latest.Rotation;
                return true;
            }

            RemotePoseSample before = _samples[_count - 2];
            double requestedTicks = targetTick - latest.Tick;
            double extrapolationTicks = System.Math.Min(MaximumExtrapolationTicks, System.Math.Max(0d, requestedTicks));
            float duration = DampedExtrapolationDuration((float)extrapolationTicks,
                Mathf.Max(0.0001f, tickDelta));
            float sampleSeconds = Mathf.Max(0.0001f, unchecked(latest.Tick - before.Tick) * tickDelta);
            Vector3 velocity = Vector3.ClampMagnitude((latest.Position - before.Position) / sampleSeconds,
                MaximumLinearSpeed);
            position = PreserveAuthoritativeDirection(latest.Position + velocity * duration, velocity);
            float angularSpeed = Mathf.Min(MaximumAngularSpeed,
                Quaternion.Angle(before.Rotation, latest.Rotation) / sampleSeconds);
            Quaternion delta = latest.Rotation * Quaternion.Inverse(before.Rotation);
            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (!float.IsFinite(angle) || !IsFinite(axis) || axis.sqrMagnitude < 0.0001f)
                rotation = latest.Rotation;
            else
                rotation = Quaternion.AngleAxis(angularSpeed * duration * Mathf.Sign(Mathf.DeltaAngle(0f, angle)),
                    axis.normalized) * latest.Rotation;
            extrapolated = requestedTicks > 0d;
            return true;
        }

        private Vector3 PreserveAuthoritativeDirection(Vector3 candidate, Vector3 direction)
        {
            if (_hasRenderedPosition && direction.sqrMagnitude > 0.000001f &&
                Vector3.Dot(candidate - _lastRenderedPosition, direction) < 0f)
                candidate = _lastRenderedPosition;
            _lastRenderedPosition = candidate;
            _hasRenderedPosition = true;
            return candidate;
        }

        private Vector3 SampleVelocity(int first, int second, float tickDelta)
        {
            RemotePoseSample a = _samples[first];
            RemotePoseSample b = _samples[second];
            float seconds = Mathf.Max(0.0001f, unchecked(b.Tick - a.Tick) * tickDelta);
            return Vector3.ClampMagnitude((b.Position - a.Position) / seconds, MaximumLinearSpeed);
        }

        private static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return (2f * t3 - 3f * t2 + 1f) * p0 + (t3 - 2f * t2 + t) * m0 +
                   (-2f * t3 + 3f * t2) * p1 + (t3 - t2) * m1;
        }

        private static Quaternion ShortestPath(Quaternion from, Quaternion to) =>
            Quaternion.Dot(from, to) < 0f ? new Quaternion(-to.x, -to.y, -to.z, -to.w) : to;

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        internal static float DampedExtrapolationDuration(float ticks, float tickDelta)
        {
            float fullSpeedTicks = Mathf.Min(3f, Mathf.Max(0f, ticks));
            float dampingTicks = Mathf.Clamp(ticks - 3f, 0f, 3f);
            // Velocity fades linearly from full speed to zero during ticks four through six.
            float dampedContribution = dampingTicks - dampingTicks * dampingTicks / 6f;
            return (fullSpeedTicks + dampedContribution) * tickDelta;
        }
    }

    /// <summary>
    /// Advances presentation time in the sender's tick domain. A server-only Steam host may have
    /// been running for thousands of ticks before a client joins, so its LocalTick must never be
    /// compared directly with the joining client's NetworkTransform sample ticks.
    /// </summary>
    public class NetworkPresentationClock
    {
        public const uint MinimumDelayTicks = 4;
        public const uint MaximumDelayTicks = 8;
        public const float MinimumPlaybackSpeed = 0.9f;
        public const float MaximumPlaybackSpeed = 1.1f;

        private readonly uint _baseDelayTicks;
        private bool _initialized;
        private double _tick;
        private uint _targetDelayTicks;
        private bool _hasArrival;
        private uint _lastSampleTick;
        private double _lastArrivalTime;
        private float _arrivalJitterSeconds;
        private float _playbackSpeed = 1f;

        public double Tick => _tick;
        public uint TargetDelayTicks => _targetDelayTicks;
        public float ArrivalJitterSeconds => _arrivalJitterSeconds;
        public float PlaybackSpeed => _playbackSpeed;
        public float BufferedTicks(uint latestSampleTick) => (float)(latestSampleTick - _tick);

        public NetworkPresentationClock(uint baseDelayTicks = RemotePresentationBuffer.PlaybackDelayTicks)
        {
            _baseDelayTicks = (uint)Mathf.Clamp((int)baseDelayTicks,
                (int)MinimumDelayTicks, (int)MaximumDelayTicks);
            _targetDelayTicks = _baseDelayTicks;
        }

        public void Reset()
        {
            _initialized = false;
            _tick = 0d;
            _targetDelayTicks = _baseDelayTicks;
            _hasArrival = false;
            _lastSampleTick = 0u;
            _lastArrivalTime = 0d;
            _arrivalJitterSeconds = 0f;
            _playbackSpeed = 1f;
        }

        public void ObserveSample(uint sampleTick, double arrivalTime, float tickDelta)
        {
            if (_hasArrival && NetworkQuaternion.IsNewer(sampleTick, _lastSampleTick))
            {
                uint tickSpan = unchecked(sampleTick - _lastSampleTick);
                double expected = tickSpan * Mathf.Max(0.0001f, tickDelta);
                float error = (float)System.Math.Abs((arrivalTime - _lastArrivalTime) - expected);
                _arrivalJitterSeconds = Mathf.Lerp(_arrivalJitterSeconds, error, 0.12f);
                int jitterTicks = Mathf.CeilToInt(_arrivalJitterSeconds /
                                                  Mathf.Max(0.0001f, tickDelta) * 2f);
                _targetDelayTicks = (uint)Mathf.Clamp((int)_baseDelayTicks + jitterTicks,
                    (int)MinimumDelayTicks, (int)MaximumDelayTicks);
            }
            _hasArrival = true;
            _lastSampleTick = sampleTick;
            _lastArrivalTime = arrivalTime;
        }

        public double Advance(uint latestSampleTick, float tickDelta, float renderDelta)
        {
            if (!_initialized)
            {
                _tick = System.Math.Max(0d,
                    latestSampleTick - (double)_targetDelayTicks);
                _initialized = true;
                return _tick;
            }

            double tickSeconds = System.Math.Max(0.0001d, tickDelta);
            double desiredTick = latestSampleTick - (double)_targetDelayTicks;
            double nominalAdvance = System.Math.Max(0d, renderDelta) / tickSeconds;
            double occupancyError = desiredTick - (_tick + nominalAdvance);
            _playbackSpeed = occupancyError > 0.75d
                ? MaximumPlaybackSpeed
                : occupancyError < -0.75d ? MinimumPlaybackSpeed : 1f;
            _tick += nominalAdvance * _playbackSpeed;
            double maximum = latestSampleTick + (double)RemotePresentationBuffer.MaximumExtrapolationTicks;
            if (_tick > maximum)
                _tick = maximum;
            return _tick;
        }
    }

    // Compatibility alias for project code and older tests; new code should use NetworkPresentationClock.
    public sealed class RemotePresentationClock : NetworkPresentationClock
    {
        public RemotePresentationClock() : base(RemotePresentationBuffer.PlaybackDelayTicks) { }
    }

}
