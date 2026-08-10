using UnityEngine;

namespace PushUp.Gameplay
{
    public readonly struct RemotePoseSample
    {
        public readonly uint Tick;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;

        public RemotePoseSample(uint tick, Vector3 position, Quaternion rotation)
        {
            Tick = tick;
            Position = position;
            Rotation = NetworkQuaternion.NormalizeOrIdentity(rotation);
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
        public const uint PlaybackDelayTicks = 4;
        public const uint MaximumExtrapolationTicks = 6;
        public const float MaximumLinearSpeed = 22f;
        public const float MaximumAngularSpeed = 720f;

        private readonly RemotePoseSample[] _samples = new RemotePoseSample[Capacity];
        private int _count;
        private bool _hasPlaybackCursor;
        private double _playbackCursor;

        public int Count => _count;
        public uint LatestTick => _count > 0 ? _samples[_count - 1].Tick : 0u;

        public void Clear()
        {
            _count = 0;
            _hasPlaybackCursor = false;
            _playbackCursor = 0d;
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
                position = _samples[0].Position;
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
                position = Hermite(previous.Position, previousVelocity * spanSeconds,
                    next.Position, nextVelocity * spanSeconds, t);
                rotation = Quaternion.Slerp(previous.Rotation, ShortestPath(previous.Rotation, next.Rotation), t);
                return true;
            }

            RemotePoseSample latest = _samples[_count - 1];
            if (_count < 2)
            {
                position = latest.Position;
                rotation = latest.Rotation;
                return true;
            }

            RemotePoseSample before = _samples[_count - 2];
            double requestedTicks = targetTick - latest.Tick;
            double extrapolationTicks = System.Math.Min(MaximumExtrapolationTicks, System.Math.Max(0d, requestedTicks));
            float duration = (float)extrapolationTicks * Mathf.Max(0.0001f, tickDelta);
            float sampleSeconds = Mathf.Max(0.0001f, unchecked(latest.Tick - before.Tick) * tickDelta);
            Vector3 velocity = Vector3.ClampMagnitude((latest.Position - before.Position) / sampleSeconds,
                MaximumLinearSpeed);
            position = latest.Position + velocity * duration;
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
    }

    /// <summary>
    /// Advances presentation time in the sender's tick domain. A server-only Steam host may have
    /// been running for thousands of ticks before a client joins, so its LocalTick must never be
    /// compared directly with the joining client's NetworkTransform sample ticks.
    /// </summary>
    public sealed class RemotePresentationClock
    {
        private bool _initialized;
        private double _tick;

        public double Tick => _tick;

        public void Reset()
        {
            _initialized = false;
            _tick = 0d;
        }

        public double Advance(uint latestSampleTick, float tickDelta, float renderDelta)
        {
            if (!_initialized)
            {
                _tick = System.Math.Max(0d,
                    latestSampleTick - (double)RemotePresentationBuffer.PlaybackDelayTicks);
                _initialized = true;
                return _tick;
            }

            double tickSeconds = System.Math.Max(0.0001d, tickDelta);
            _tick += System.Math.Max(0d, renderDelta) / tickSeconds;
            double maximum = latestSampleTick + (double)RemotePresentationBuffer.MaximumExtrapolationTicks;
            if (_tick > maximum)
                _tick = maximum;
            return _tick;
        }
    }

}
