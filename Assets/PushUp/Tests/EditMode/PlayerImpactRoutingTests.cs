using System.Reflection;
using FishNet.Object;
using NUnit.Framework;
using PushUp.Gameplay;
using UnityEngine;

namespace PushUp.Tests
{
    public sealed class PlayerImpactRoutingTests
    {
        [Test]
        public void ImpactCommandCarriesStableIdentityActionAndLocalContact()
        {
            Vector3 impulse = new(10f, 20f, 30f);
            Vector3 localPoint = new(0.2f, 0.6f, -0.1f);
            PlayerImpactCommand command = new(17, 23u, 41u, PlayerImpactCommand.GrabPullAction,
                impulse, localPoint);

            Assert.That(command.SourceObjectId, Is.EqualTo(17));
            Assert.That(command.Sequence, Is.EqualTo(23u));
            Assert.That(command.SimulationTick, Is.EqualTo(41u));
            Assert.That(command.Action, Is.EqualTo(PlayerImpactCommand.GrabPullAction));
            Assert.That(command.Impulse, Is.EqualTo(impulse));
            Assert.That(command.LocalHitPoint, Is.EqualTo(localPoint));
            Assert.That(command.IsGrabPull, Is.True);
            Assert.That(command.IsPush, Is.False);

            command.Action = PlayerImpactCommand.PushAction;
            Assert.That(command.IsPush, Is.True);
        }

        [Test]
        public void OwnerImpactRoutingAppliesEachSourceSequenceExactlyOnce()
        {
            GameObject player = CreateStandalonePlayer("impact routing player", out StandalonePlayerController motor);
            try
            {
                Rigidbody body = player.GetComponent<Rigidbody>();
                PlayerImpactCommand first = new(3, 1u, 100u, PlayerImpactCommand.PunchAction,
                    Vector3.forward * 200f, Vector3.up * 0.65f);

                Assert.That(PlayerImpactRouting.ApplyToLocalAuthority(body, first), Is.True);
                Assert.That(PlayerImpactRouting.ApplyToLocalAuthority(body, first), Is.False,
                    "reliable retries must not apply a second physical impulse");

                PlayerImpactCommand newer = first;
                newer.Sequence = 2u;
                Assert.That(PlayerImpactRouting.ApplyToLocalAuthority(body, newer), Is.True);

                PlayerImpactCommand otherSource = first;
                otherSource.SourceObjectId = 4;
                Assert.That(PlayerImpactRouting.ApplyToLocalAuthority(body, otherSource), Is.True,
                    "different attackers own independent monotonic sequences");
            }
            finally
            {
                DestroyStandalonePlayer(player, motor);
            }
        }

        [Test]
        public void NetworkTargetResolutionPromotesLimbBodyAndPointToActorRoot()
        {
            GameObject root = new("network actor root");
            GameObject limb = new("arm limb");
            try
            {
                root.transform.SetPositionAndRotation(new Vector3(4f, 2f, -3f), Quaternion.Euler(0f, 35f, 0f));
                Rigidbody rootBody = root.AddComponent<Rigidbody>();
                NetworkObject networkObject = root.AddComponent<NetworkObject>();
                limb.transform.SetParent(root.transform, false);
                limb.transform.localPosition = new Vector3(0.5f, 0.7f, 0.1f);
                Rigidbody limbBody = limb.AddComponent<Rigidbody>();
                limb.AddComponent<SphereCollider>();
                Vector3 worldPoint = limb.transform.TransformPoint(new Vector3(0.1f, 0.2f, -0.05f));

                MethodInfo resolver = typeof(PlayerInteraction).GetMethod("TryResolveNetworkTarget",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(resolver, Is.Not.Null);
                object[] arguments = { limbBody, worldPoint, null, null, Vector3.zero };
                bool resolved = (bool)resolver.Invoke(null, arguments);

                Assert.That(resolved, Is.True);
                Assert.That(arguments[2], Is.SameAs(networkObject));
                Assert.That(arguments[3], Is.SameAs(rootBody));
                Vector3 localPoint = (Vector3)arguments[4];
                Assert.That((localPoint - root.transform.InverseTransformPoint(worldPoint)).sqrMagnitude,
                    Is.LessThan(0.000001f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PersistentOwnerGrabRejectsStaleBeginAndEndSequences()
        {
            GameObject target = CreateStandalonePlayer("grab target", out StandalonePlayerController motor);
            GameObject source = new("grab source");
            try
            {
                PlayerInteraction interaction = target.AddComponent<PlayerInteraction>();
                typeof(PlayerInteraction).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(interaction, null);
                source.AddComponent<Rigidbody>();
                NetworkObject sourceNetworkObject = source.AddComponent<NetworkObject>();
                MethodInfo begin = typeof(PlayerInteraction).GetMethod("BeginOwnerGrabConstraint",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo end = typeof(PlayerInteraction).GetMethod("EndOwnerGrabConstraint",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(begin, Is.Not.Null);
                Assert.That(end, Is.Not.Null);

                int sourceObjectId = sourceNetworkObject.ObjectId;
                Assert.That((bool)begin.Invoke(interaction,
                    new object[] { sourceNetworkObject, sourceObjectId, 7u, Vector3.up * 0.5f }), Is.True);
                Assert.That((bool)begin.Invoke(interaction,
                    new object[] { sourceNetworkObject, sourceObjectId, 7u, Vector3.up * 0.5f }), Is.False);
                Assert.That((bool)begin.Invoke(interaction,
                    new object[] { sourceNetworkObject, sourceObjectId, 8u, Vector3.up * 0.5f }), Is.True);
                Assert.That((bool)end.Invoke(interaction, new object[] { sourceObjectId, 7u }), Is.False,
                    "a delayed release must not cancel a newer grab");
                Assert.That((bool)end.Invoke(interaction, new object[] { sourceObjectId, 8u }), Is.True);
                Assert.That((bool)end.Invoke(interaction, new object[] { sourceObjectId, 8u }), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(source);
                DestroyStandalonePlayer(target, motor);
            }
        }

        private static GameObject CreateStandalonePlayer(string name, out StandalonePlayerController motor)
        {
            GameObject player = new(name);
            player.AddComponent<Rigidbody>();
            player.AddComponent<CapsuleCollider>();
            player.AddComponent<PlayerInputReader>();
            motor = player.AddComponent<StandalonePlayerController>();
            motor.EnsureInitialized();
            return player;
        }

        private static void DestroyStandalonePlayer(GameObject player, StandalonePlayerController motor)
        {
            if (motor != null && motor.CameraPivot != null)
                Object.DestroyImmediate(motor.CameraPivot.gameObject);
            if (player != null)
                Object.DestroyImmediate(player);
        }
    }
}
