using System.Collections.Generic;
using NUnit.Framework;
using PushUp.Gameplay;
using UnityEngine;

namespace PushUp.Tests
{
    public sealed class NetworkSmoothingTests
    {
        private readonly struct ScheduledPose
        {
            public readonly double ArrivalTick;
            public readonly RemotePoseSample Sample;

            public ScheduledPose(double arrivalTick, RemotePoseSample sample)
            {
                ArrivalTick = arrivalTick;
                Sample = sample;
            }
        }

        private readonly struct ScheduledBoulder
        {
            public readonly double ArrivalTick;
            public readonly BoulderSnapshot Snapshot;

            public ScheduledBoulder(double arrivalTick, BoulderSnapshot snapshot)
            {
                ArrivalTick = arrivalTick;
                Snapshot = snapshot;
            }
        }

        [Test]
        public void RemotePresentationBufferInterpolatesMonotonicallyAcrossBurstAndReorder()
        {
            RemotePresentationBuffer buffer = new();
            Assert.That(buffer.Add(new RemotePoseSample(6u, Vector3.right * 6f, Quaternion.Euler(0f, 60f, 0f))), Is.True);
            Assert.That(buffer.Add(new RemotePoseSample(2u, Vector3.right * 2f, Quaternion.Euler(0f, 20f, 0f))), Is.True);
            Assert.That(buffer.Add(new RemotePoseSample(4u, Vector3.right * 4f, Quaternion.Euler(0f, 40f, 0f))), Is.True);
            Assert.That(buffer.Add(new RemotePoseSample(5u, Vector3.right * 5f, Quaternion.Euler(0f, 50f, 0f))), Is.True);
            Assert.That(buffer.Add(new RemotePoseSample(3u, Vector3.right * 3f, Quaternion.Euler(0f, 30f, 0f))), Is.True);

            float previous = float.NegativeInfinity;
            for (double tick = 2d; tick <= 6d; tick += 0.25d)
            {
                Assert.That(buffer.TrySample(tick, 1f / 60f, out Vector3 position,
                    out Quaternion rotation, out bool extrapolated), Is.True);
                Assert.That(position.x, Is.GreaterThanOrEqualTo(previous - 0.0001f));
                Assert.That(Quaternion.Angle(Quaternion.identity, rotation), Is.InRange(19.9f, 60.1f));
                Assert.That(extrapolated, Is.False);
                previous = position.x;
            }
        }

        [Test]
        public void RemotePresentationExtrapolationIsTimeAndSpeedBounded()
        {
            RemotePresentationBuffer buffer = new();
            buffer.Add(new RemotePoseSample(10u, Vector3.zero, Quaternion.identity));
            buffer.Add(new RemotePoseSample(11u, Vector3.right * 100f, Quaternion.Euler(0f, 170f, 0f)));
            Assert.That(buffer.TrySample(100d, 1f / 60f, out Vector3 position,
                out _, out bool extrapolated), Is.True);
            Assert.That(extrapolated, Is.True);
            Assert.That(position.x - 100f, Is.LessThanOrEqualTo(
                RemotePresentationBuffer.MaximumLinearSpeed * 0.1001f));
        }

        [Test]
        public void RemotePresentationClockUsesJoiningClientsTickDomain()
        {
            RemotePresentationClock clock = new();
            // A server-only host may already be at tick 20,000 when a client whose stream starts
            // at tick 10 joins. The presentation clock must begin near 10, not near 20,000.
            Assert.That(clock.Advance(10u, 1f / 60f, 1f / 60f), Is.EqualTo(5d).Within(0.0001d));
            Assert.That(clock.Advance(11u, 1f / 60f, 1f / 60f), Is.EqualTo(6d).Within(0.0001d));
            Assert.That(clock.Advance(12u, 1f / 60f, 1f / 60f), Is.EqualTo(7d).Within(0.0001d));

            RemotePresentationBuffer buffer = new();
            Assert.That(buffer.Add(new RemotePoseSample(10u, Vector3.zero, Quaternion.identity)), Is.True);
            Assert.That(buffer.TrySample(clock.Tick, 1f / 60f, out _, out _, out _), Is.True);
            Assert.That(buffer.Add(new RemotePoseSample(12u, Vector3.right, Quaternion.identity)), Is.True,
                "a valid client sample must not be rejected because the host has an older, larger LocalTick");
        }

        [Test]
        public void PresentationClockAdaptsDelayAndPlaybackSpeedWithoutSnapping()
        {
            NetworkPresentationClock clock = new(5u);
            const float tickDelta = 1f / 60f;
            clock.ObserveSample(100u, 1d, tickDelta);
            Assert.That(clock.Advance(100u, tickDelta, tickDelta), Is.EqualTo(95d).Within(0.001d));

            clock.ObserveSample(101u, 1d + tickDelta, tickDelta);
            double stable = clock.Advance(101u, tickDelta, tickDelta);
            Assert.That(stable, Is.EqualTo(96d).Within(0.001d));
            Assert.That(clock.PlaybackSpeed, Is.EqualTo(1f));

            clock.ObserveSample(102u, 1.15d, tickDelta);
            Assert.That(clock.TargetDelayTicks, Is.GreaterThan(5u));
            double before = clock.Tick;
            double after = clock.Advance(102u, tickDelta, tickDelta);
            Assert.That(after - before, Is.EqualTo(NetworkPresentationClock.MinimumPlaybackSpeed).Within(0.001d));
            Assert.That(clock.TargetDelayTicks, Is.InRange(NetworkPresentationClock.MinimumDelayTicks,
                NetworkPresentationClock.MaximumDelayTicks));
        }

        [Test]
        public void BoulderVisualPredictionIsBoundedAndDoesNotMovePhysicsRoot()
        {
            GameObject instance = Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/PushUp/Prefabs/Boulder.prefab"));
            try
            {
                Rigidbody body = instance.GetComponent<Rigidbody>();
                BoulderVisualPredictor predictor = instance.GetComponent<BoulderVisualPredictor>();
                Vector3 initialPosition = body.position;
                predictor.AddImpulse(Vector3.forward * 400f, body.worldCenterOfMass + Vector3.up);
                for (int index = 0; index < 120; index++)
                    predictor.Simulate(1f / 120f);

                Assert.That(predictor.PositionOffset.magnitude,
                    Is.LessThanOrEqualTo(BoulderVisualPredictor.MaximumPositionOffset + 0.0001f));
                Assert.That(predictor.RotationOffsetDegrees,
                    Is.LessThanOrEqualTo(BoulderVisualPredictor.MaximumRotationOffsetDegrees + 0.0001f));
                Assert.That(body.position, Is.EqualTo(initialPosition),
                    "visual prediction must never move the authoritative or collision Rigidbody");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void InvalidPoseQuaternionsAreRejectedAndValidOnesAreNormalized()
        {
            PlayerPoseSnapshot invalid = new()
            {
                Sequence = 1u,
                LeftArmLocalRotation = new Quaternion(0f, 0f, 0f, 0f),
                RightArmLocalRotation = Quaternion.identity
            };
            Assert.That(invalid.TrySanitize(out _), Is.False);

            PlayerPoseSnapshot valid = new(12u, 2u, new Quaternion(0f, 0f, 0f, 2f),
                Quaternion.Euler(10f, 20f, 30f));
            Assert.That(valid.TrySanitize(out PlayerPoseSnapshot sanitized), Is.True);
            Assert.That(Mathf.Abs(1f - Quaternion.Dot(sanitized.LeftArmLocalRotation,
                sanitized.LeftArmLocalRotation)), Is.LessThan(0.0001f));
        }

        [Test]
        public void PackedBoulderRotationRoundTripsWithinVisualTolerance()
        {
            foreach (Quaternion rotation in new[]
                     {
                         Quaternion.identity,
                         Quaternion.Euler(12f, 130f, -43f),
                         Quaternion.Euler(179f, 359f, 92f)
                     })
            {
                Quaternion unpacked = PackedQuaternion64.Unpack(PackedQuaternion64.Pack(rotation));
                Assert.That(Quaternion.Angle(rotation, unpacked), Is.LessThan(0.02f));
            }
        }

        [Test]
        public void BoulderBufferTreatsTeleportGenerationAsCallerVisibleState()
        {
            BoulderSnapshotBuffer buffer = new();
            BoulderSnapshot first = new(10u, 1u, 3u, Vector3.zero, Quaternion.identity,
                Vector3.right, Vector3.zero, false, false);
            BoulderSnapshot reset = new(12u, 2u, 4u, Vector3.one * 20f, Quaternion.identity,
                Vector3.zero, Vector3.zero, false, true);
            Assert.That(buffer.Add(first), Is.True);
            Assert.That(buffer.LatestTeleportGeneration, Is.EqualTo(3u));
            buffer.Clear();
            Assert.That(buffer.Add(reset), Is.True);
            Assert.That(buffer.LatestTeleportGeneration, Is.EqualTo(4u));
            Assert.That(buffer.TrySample(12d, 1f / 60f, out BoulderSnapshot sampled,
                out _), Is.True);
            Assert.That(sampled.Position, Is.EqualTo(reset.Position));
        }

        [TestCase(30)]
        [TestCase(60)]
        [TestCase(120)]
        [TestCase(144)]
        public void RemoteReplayRemainsForwardMovingAcrossJitterBurstsReorderingAndLoss(int renderFps)
        {
            List<ScheduledPose> deliveries = new();
            uint sequence = 0u;
            for (uint tick = 0u; tick <= 360u; tick += 2u)
            {
                sequence++;
                if (sequence % 50u == 0u) // deterministic two percent loss
                    continue;
                int jitter = (sequence % 9u) switch
                {
                    0u => 3,
                    1u => 1,
                    4u => -1,
                    _ => 0
                };
                // Several samples intentionally become available together, and the tie-break below
                // delivers the newest one first to exercise burst and reordering behavior.
                double arrival = tick + 5d + jitter + (sequence % 13u == 0u ? 3d : 0d);
                deliveries.Add(new ScheduledPose(arrival, new RemotePoseSample(tick,
                    Vector3.right * (tick * 0.1f), Quaternion.Euler(0f, tick * 0.75f, 0f))));
            }
            deliveries.Sort((a, b) =>
            {
                int arrivalOrder = a.ArrivalTick.CompareTo(b.ArrivalTick);
                return arrivalOrder != 0 ? arrivalOrder : b.Sample.Tick.CompareTo(a.Sample.Tick);
            });

            RemotePresentationBuffer buffer = new();
            NetworkPresentationClock clock = new();
            int deliveryIndex = 0;
            float previousX = float.NegativeInfinity;
            float frameTicks = 60f / renderFps;
            int sampledFrames = 0;
            for (double renderTick = 0d; renderTick <= 360d; renderTick += frameTicks)
            {
                while (deliveryIndex < deliveries.Count &&
                       deliveries[deliveryIndex].ArrivalTick <= renderTick)
                {
                    ScheduledPose delivery = deliveries[deliveryIndex];
                    if (buffer.Add(delivery.Sample))
                        clock.ObserveSample(delivery.Sample.Tick, renderTick / 60d, 1f / 60f);
                    deliveryIndex++;
                }
                double playbackTick = clock.Advance(buffer.LatestTick, 1f / 60f,
                    frameTicks / 60f);
                if (renderTick < 16d || !buffer.TrySample(
                        playbackTick, 1f / 60f,
                        out Vector3 position, out Quaternion rotation, out _))
                    continue;

                Assert.That(position.x, Is.GreaterThanOrEqualTo(previousX - 0.001f),
                    $"presentation reversed at {renderFps} FPS on render tick {renderTick:F2}");
                Assert.That(NetworkQuaternion.IsFinite(rotation), Is.True);
                previousX = position.x;
                sampledFrames++;
            }
            Assert.That(sampledFrames, Is.GreaterThan(renderFps * 4));
        }

        [TestCase(30)]
        [TestCase(60)]
        [TestCase(120)]
        [TestCase(144)]
        public void BoulderReplayRemainsContinuousAcrossJitterBurstsReorderingAndLoss(int renderFps)
        {
            List<ScheduledBoulder> deliveries = new();
            uint sequence = 0u;
            for (uint tick = 0u; tick <= 360u; tick += 2u)
            {
                sequence++;
                if (sequence % 50u == 0u)
                    continue;
                int jitter = (sequence % 11u) switch
                {
                    0u => 3,
                    2u => -1,
                    7u => 2,
                    _ => 0
                };
                double arrival = tick + 5d + jitter + (sequence % 17u == 0u ? 3d : 0d);
                Vector3 position = Vector3.right * (tick * 0.06f);
                BoulderSnapshot snapshot = new(tick, sequence, 1u, position,
                    Quaternion.Euler(0f, 0f, tick * 1.5f), Vector3.right * 3.6f,
                    Vector3.forward * 1.5f, false, false);
                deliveries.Add(new ScheduledBoulder(arrival, snapshot));
            }
            deliveries.Sort((a, b) =>
            {
                int arrivalOrder = a.ArrivalTick.CompareTo(b.ArrivalTick);
                return arrivalOrder != 0
                    ? arrivalOrder
                    : b.Snapshot.ServerTick.CompareTo(a.Snapshot.ServerTick);
            });

            BoulderSnapshotBuffer buffer = new();
            NetworkPresentationClock clock = new(BoulderSnapshotBuffer.PlaybackDelayTicks);
            int deliveryIndex = 0;
            float previousX = float.NegativeInfinity;
            float frameTicks = 60f / renderFps;
            int sampledFrames = 0;
            for (double renderTick = 0d; renderTick <= 360d; renderTick += frameTicks)
            {
                while (deliveryIndex < deliveries.Count &&
                       deliveries[deliveryIndex].ArrivalTick <= renderTick)
                {
                    ScheduledBoulder delivery = deliveries[deliveryIndex];
                    if (buffer.Add(delivery.Snapshot))
                        clock.ObserveSample(delivery.Snapshot.ServerTick, renderTick / 60d, 1f / 60f);
                    deliveryIndex++;
                }
                double playbackTick = clock.Advance(buffer.LatestTick, 1f / 60f,
                    frameTicks / 60f);
                if (renderTick < 16d || !buffer.TrySample(
                        playbackTick, 1f / 60f,
                        out BoulderSnapshot sampled, out _))
                    continue;

                Assert.That(sampled.Position.x, Is.GreaterThanOrEqualTo(previousX - 0.001f),
                    $"boulder presentation reversed at {renderFps} FPS on render tick {renderTick:F2}");
                Assert.That(NetworkQuaternion.IsFinite(sampled.Rotation), Is.True);
                previousX = sampled.Position.x;
                sampledFrames++;
            }
            Assert.That(sampledFrames, Is.GreaterThan(renderFps * 4));
        }
    }
}
