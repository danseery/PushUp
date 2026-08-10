using System.Reflection;
using NUnit.Framework;
using PushUp.Gameplay;
using PushUp.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PushUp.Tests
{
    public sealed class GameplayPhysicsTests
    {
        [TestCase("Assets/PushUp/Prefabs/Player.prefab")]
        [TestCase("Assets/PushUp/Prefabs/Boulder.prefab")]
        [TestCase("Assets/PushUp/Prefabs/SpeedBoost.prefab")]
        [TestCase("Assets/PushUp/Prefabs/BoulderAssist.prefab")]
        [TestCase("Assets/PushUp/Prefabs/CarryAnchor.prefab")]
        public void StandalonePrototypeCanReusePrefabUrpMaterial(string prefabPath)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.That(prefab, Is.Not.Null);
            Component networkObject = prefab.GetComponent("NetworkObject");
            Assert.That(networkObject, Is.Not.Null);

            MethodInfo materialFromPrefab = typeof(RunDirector).GetMethod("MaterialFromPrefab", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(materialFromPrefab, Is.Not.Null);
            Material material = materialFromPrefab.Invoke(null, new object[] { networkObject }) as Material;

            Assert.That(material, Is.SameAs(prefab.GetComponentInChildren<Renderer>(true).sharedMaterial));
            Assert.That(material, Is.Not.Null);
            Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"));
        }

        [Test]
        public void OfflineBoulderUsesTheBoulderPrefabScale()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/PushUp/Prefabs/Boulder.prefab");
            Assert.That(prefab, Is.Not.Null);
            Component networkObject = prefab.GetComponent("NetworkObject");
            Assert.That(networkObject, Is.Not.Null);

            MethodInfo scaleFromPrefab = typeof(RunDirector).GetMethod("ScaleFromPrefab", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(scaleFromPrefab, Is.Not.Null);
            Vector3 scale = (Vector3)scaleFromPrefab.Invoke(null, new object[] { networkObject });

            Assert.That(scale, Is.EqualTo(prefab.transform.localScale));
        }

        [Test]
        public void OfflineBoulderUsesTheNetworkBoulderPresentationMaterial()
        {
            GameObject networkPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/PushUp/Prefabs/Boulder.prefab");
            GameObject offlinePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/PushUp/Prefabs/Offline/Boulder.prefab");

            Assert.That(networkPrefab, Is.Not.Null);
            Assert.That(offlinePrefab, Is.Not.Null);
            Material networkMaterial = networkPrefab.GetComponentInChildren<Renderer>(true).sharedMaterial;
            Material offlineMaterial = offlinePrefab.GetComponentInChildren<Renderer>(true).sharedMaterial;
            Assert.That(networkMaterial, Is.Not.Null);
            Assert.That(offlineMaterial, Is.SameAs(networkMaterial),
                "offline and Steam runs must render the same prefab-authored boulder material");
        }

        [Test]
        public void BoulderReplicationIsServerAuthoritative()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/PushUp/Prefabs/Boulder.prefab");
            Assert.That(prefab.GetComponent("NetworkTransform"), Is.Null,
                "the boulder uses the fixed-tick project-owned snapshot path, not render-clock NetworkTransform");
            Assert.That(prefab.GetComponent<BoulderNetworkState>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<BoulderVisualPredictor>(), Is.Not.Null);
            Assert.That(prefab.transform.Find("Presentation"), Is.Not.Null);
            Rigidbody body = prefab.GetComponent<Rigidbody>();
            Assert.That(body.mass, Is.EqualTo(150f));
            Assert.That(body.linearDamping, Is.EqualTo(0.08f).Within(0.001f));
            Assert.That(body.angularDamping, Is.EqualTo(0.3f).Within(0.001f));
            Assert.That(prefab.GetComponent<NetworkRunState>(), Is.Not.Null);
        }

        [Test]
        public void PlayerPrefabUsesUnitOwnerAuthoritativeRigidbodyRoot()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/PushUp/Prefabs/Player.prefab");
            Assert.That(prefab.transform.localScale, Is.EqualTo(Vector3.one));
            CapsuleCollider capsule = prefab.GetComponent<CapsuleCollider>();
            Assert.That(capsule.height, Is.EqualTo(PlayerPhysics.CapsuleHeight));
            Assert.That(capsule.radius, Is.EqualTo(PlayerPhysics.CapsuleRadius));
            Assert.That(capsule.sharedMaterial, Is.Not.Null);
            Assert.That(capsule.sharedMaterial.dynamicFriction, Is.Zero);

            Component networkObject = prefab.GetComponent("NetworkObject");
            SerializedObject network = new(networkObject);
            Assert.That(network.FindProperty("_enablePrediction").boolValue, Is.False,
                "player movement belongs to the owning machine and must not be reconciled by FishNet prediction");
            Assert.That(network.FindProperty("_graphicalObject").objectReferenceValue, Is.Null);
            Assert.That(network.FindProperty("_detachGraphicalObject").boolValue, Is.False,
                "jointed world arms cannot share FishNet's detached prediction graphical path");
            Assert.That(network.FindProperty("_enableStateForwarding").boolValue, Is.False);

            Component networkTransform = prefab.GetComponent("NetworkTransform");
            SerializedObject transformSettings = new(networkTransform);
            Assert.That(transformSettings.FindProperty("_clientAuthoritative").boolValue, Is.True);
            Assert.That(transformSettings.FindProperty("_sendToOwner").boolValue, Is.False);
            Assert.That(transformSettings.FindProperty("_componentConfiguration").intValue, Is.EqualTo(2),
                "only the owner may keep the player Rigidbody dynamic");
            Assert.That(transformSettings.FindProperty("_interval").intValue, Is.EqualTo(1));
            Assert.That(transformSettings.FindProperty("_interpolation").intValue, Is.Zero,
                "the hidden query proxy must not add a second visible interpolation layer");
            Assert.That(transformSettings.FindProperty("_synchronizeScale").boolValue, Is.False);
            Assert.That(network.FindProperty("_networkTransform").objectReferenceValue, Is.EqualTo(networkTransform));
            Assert.That(prefab.GetComponent<RemotePlayerPresentation>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<PlayerActorPhysics>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<Renderer>().enabled, Is.False,
                "the collision capsule is never presentation geometry");
        }

        [Test]
        public void PlayerPrefabOwnsPunchTuningArmsAndImpactFeedback()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/PushUp/Prefabs/Player.prefab");
            Assert.That(prefab, Is.Not.Null);
            Assert.That(prefab.GetComponent<PlayerInteraction>().ConfiguredPunchImpulse, Is.EqualTo(200f));
            Assert.That(prefab.GetComponent<ActiveRagdollPuppet>(), Is.Not.Null);
            Assert.That(prefab.transform.Find("World Rig/Torso/Left Arm"), Is.Not.Null);
            Assert.That(prefab.transform.Find("World Rig/Torso/Right Arm"), Is.Not.Null);
            Assert.That(prefab.transform.Find("Camera Pivot/First Person Rig/Left Arm"), Is.Not.Null);
            Assert.That(prefab.transform.Find("Camera Pivot/First Person Rig/Right Arm"), Is.Not.Null);

            PunchImpactFeedback feedback = prefab.GetComponent<PunchImpactFeedback>();
            Assert.That(feedback, Is.Not.Null);
            Assert.That(feedback.ImpactPrefab, Is.Not.Null);
            Renderer impactRenderer = feedback.ImpactPrefab.GetComponentInChildren<Renderer>();
            Assert.That(impactRenderer.sharedMaterial.shader.name, Does.StartWith("Universal Render Pipeline/"));
            Assert.That(impactRenderer.sharedMaterial.GetFloat("_Cull"), Is.EqualTo(0f),
                "impact geometry must render from either side of a curved surface");
            Assert.That(feedback.ImpactPrefab.transform.Find("Center"), Is.Not.Null);
            Assert.That(ActiveRagdollPuppet.ShouldShowBodyRenderer(false), Is.False,
                "remote players use the complete world rig; the collision capsule must remain hidden");
            Assert.That(ActiveRagdollPuppet.ShouldShowBodyRenderer(true), Is.False,
                "the local first-person camera must not render its own capsule body");
            Assert.That(ActiveRagdollPuppet.ShouldShowWorldRig(false), Is.True,
                "a remote network player must always expose the world arms/body rig");
            Assert.That(ActiveRagdollPuppet.ShouldShowWorldRig(true), Is.False,
                "the owning first-person player uses only the local arm rig");
        }

        [Test]
        public void PlayerArmsAlternatePunchesWithoutChangingPushPose()
        {
            GameObject player = new("Alternating punch test");
            try
            {
                ActiveRagdollPuppet puppet = player.AddComponent<ActiveRagdollPuppet>();
                FieldInfo action = typeof(ActiveRagdollPuppet).GetField("_action", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(action, Is.Not.Null);

                puppet.PlayInteraction(false);
                Assert.That(action.GetValue(puppet), Is.EqualTo(1), "first punch uses the right hand");
                puppet.PlayInteraction(false);
                Assert.That(action.GetValue(puppet), Is.EqualTo(3), "second punch uses the left hand");
                puppet.PlayInteraction(true);
                Assert.That(action.GetValue(puppet), Is.EqualTo(2), "PUSH remains the two-hand action");
                puppet.PlayInteraction(false);
                Assert.That(action.GetValue(puppet), Is.EqualTo(1), "PUSH does not consume the next alternating hand");
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void RollingHandsRequireAMovingBoulderGrab()
        {
            Assert.That(ActiveRagdollPuppet.ShouldRollHands(false, true, 2f), Is.False,
                "the rolling pose requires an active grab");
            Assert.That(ActiveRagdollPuppet.ShouldRollHands(true, false, 2f), Is.False,
                "other moving grabbed objects keep the steady two-hand pose");
            Assert.That(ActiveRagdollPuppet.ShouldRollHands(true, true, 0.2f), Is.False,
                "holding a stationary boulder keeps the steady two-hand pose");
            Assert.That(ActiveRagdollPuppet.ShouldRollHands(true, true, 1f), Is.True,
                "walking with a grabbed boulder alternates the hands");
        }

        [Test]
        public void OfflinePlayerClonesPrefabTuningAndFirstPersonRig()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/PushUp/Prefabs/Player.prefab");
            Component networkObject = prefab.GetComponent("NetworkObject");
            MethodInfo spawn = typeof(RunDirector).GetMethod("SpawnStandalonePlayer", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(spawn, Is.Not.Null);

            GameObject offline = null;
            try
            {
                spawn.Invoke(null, new object[] { Vector3.zero, networkObject });
                offline = GameObject.Find("Offline Player");
                Assert.That(offline, Is.Not.Null);
                Assert.That(offline.GetComponent<PlayerInteraction>().ConfiguredPunchImpulse, Is.EqualTo(200f));
                Assert.That(offline.GetComponent<PunchImpactFeedback>().ImpactPrefab, Is.Not.Null);
                ActiveRagdollPuppet puppet = offline.GetComponent<ActiveRagdollPuppet>();
                Assert.That(puppet, Is.Not.Null);
                Assert.That(offline.GetComponent<StandalonePlayerController>().CameraPivot.parent, Is.Null,
                    "the offline camera presentation rig must not inherit fixed-step Rigidbody motion");
                FieldInfo worldRoot = typeof(ActiveRagdollPuppet).GetField("_worldRoot", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo viewRoot = typeof(ActiveRagdollPuppet).GetField("_viewRoot", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(worldRoot?.GetValue(puppet), Is.Not.Null);
                Assert.That(viewRoot?.GetValue(puppet), Is.Not.Null);
            }
            finally
            {
                if (offline != null)
                    Object.DestroyImmediate(offline);
            }
        }

        [Test]
        public void TrainingDummyPrefabOwnsEditablePresentationWithoutPlayerOrNetworkComponents()
        {
            GameObject dummyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/PushUp/Prefabs/Offline/TrainingDummy.prefab");
            Material dummyMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/PushUp/Materials/Dummy.mat");
            PhysicsMaterial dummyPhysics = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(
                "Assets/PushUp/Materials/DummyPhysics.physicMaterial");
            Assert.That(dummyPrefab, Is.Not.Null);
            TrainingDummy dummy = dummyPrefab.GetComponent<TrainingDummy>();
            Assert.That(dummy, Is.Not.Null);
            Assert.That(dummyPhysics, Is.Not.Null);
            Assert.That(dummy.GetComponent<CapsuleCollider>().sharedMaterial, Is.SameAs(dummyPhysics));
            Assert.That(dummyPhysics.dynamicFriction, Is.GreaterThanOrEqualTo(0.8f));
            Assert.That(dummyPhysics.staticFriction, Is.EqualTo(1f).Within(0.001f));
            Assert.That(dummyPhysics.frictionCombine, Is.EqualTo(PhysicsMaterialCombine.Maximum));
            Assert.That(dummy.transform.Find("World Rig/Torso/Left Arm"), Is.Not.Null);
            Assert.That(dummy.transform.Find("World Rig/Torso/Right Arm"), Is.Not.Null);
            Assert.That(dummy.transform.Find("World Rig/Torso/Left Arm").GetComponent<Rigidbody>(), Is.Not.Null);
            foreach (string armPath in new[] { "World Rig/Torso/Left Arm", "World Rig/Torso/Right Arm" })
            {
                Rigidbody armBody = dummy.transform.Find(armPath).GetComponent<Rigidbody>();
                ConfigurableJoint armJoint = dummy.transform.Find(armPath).GetComponent<ConfigurableJoint>();
                Assert.That(armBody, Is.Not.Null);
                Assert.That(armJoint, Is.Not.Null);
                Assert.That(armBody.angularDamping, Is.EqualTo(TrainingDummy.ArmAngularDamping).Within(0.001f));
                Assert.That(armJoint.rotationDriveMode, Is.EqualTo(RotationDriveMode.Slerp));
                Assert.That(armJoint.slerpDrive.positionSpring, Is.EqualTo(TrainingDummy.ArmPoseSpring).Within(0.001f));
                Assert.That(armJoint.slerpDrive.positionDamper, Is.EqualTo(TrainingDummy.ArmPoseDamper).Within(0.001f));
                Assert.That(armJoint.slerpDrive.maximumForce, Is.EqualTo(TrainingDummy.ArmPoseMaximumForce).Within(0.001f));
                Assert.That(armJoint.slerpDrive.useAcceleration, Is.True);
            }
            foreach (Renderer renderer in dummyPrefab.GetComponentsInChildren<Renderer>(true))
                Assert.That(renderer.sharedMaterial, Is.SameAs(dummyMaterial));
            Assert.That(dummy.GetComponent<PlayerInputReader>(), Is.Null);
            Assert.That(dummy.GetComponent("PlayerMotor"), Is.Null);
            Assert.That(dummy.GetComponent("NetworkObject"), Is.Null);
            GameObject instance = Object.Instantiate(dummyPrefab);
            try
            {
                TrainingDummy runtimeDummy = instance.GetComponent<TrainingDummy>();
                Assert.That(PlayerInteraction.IsLocalOnlyBody(runtimeDummy.Body), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void TrainingDummyDefinitionUsesRegisteredServerAuthoritativeNetworkPrefab()
        {
            SpawnDefinition definition = AssetDatabase.LoadAssetAtPath<SpawnDefinition>(
                "Assets/PushUp/SpawnDefinitions/TrainingDummy.asset");
            Assert.That(definition, Is.Not.Null);
            Assert.That(definition.Policy, Is.EqualTo(SpawnPolicy.Replicated));
            Assert.That(definition.HasOfflineOverride, Is.True);
            Assert.That(definition.OfflinePrefab.GetComponent("NetworkObject"), Is.Null);

            GameObject prefab = definition.Prefab;
            Assert.That(prefab.GetComponent<TrainingDummy>(), Is.Not.Null);
            Assert.That(prefab.GetComponent("NetworkObject"), Is.Not.Null);
            Component networkTransform = prefab.GetComponent("NetworkTransform");
            Assert.That(networkTransform, Is.Not.Null);
            SerializedObject settings = new(networkTransform);
            Assert.That(settings.FindProperty("_componentConfiguration").intValue, Is.EqualTo(2));
            Assert.That(settings.FindProperty("_interval").intValue, Is.EqualTo(3));
            Assert.That(settings.FindProperty("_interpolation").intValue, Is.Zero);
            Assert.That(settings.FindProperty("_synchronizeScale").boolValue, Is.False);
            Assert.That(prefab.GetComponent<RemoteActorPresentation>(), Is.Not.Null);
            Assert.That(PlayerInteraction.IsLocalOnlyBody(prefab.GetComponent<Rigidbody>()), Is.False);

            Object registry = AssetDatabase.LoadMainAssetAtPath("Assets/DefaultPrefabObjects.asset");
            SerializedProperty prefabs = new SerializedObject(registry).FindProperty("_prefabs");
            bool registered = false;
            for (int index = 0; index < prefabs.arraySize; index++)
                registered |= prefabs.GetArrayElementAtIndex(index).objectReferenceValue ==
                              prefab.GetComponent("NetworkObject");
            Assert.That(registered, Is.True);
        }

        [Test]
        public void TrainingDummyOnlyEntersGetUpStateAfterItIsDown()
        {
            Assert.That(TrainingDummy.IsDown(Vector3.up), Is.False);
            Assert.That(TrainingDummy.IsDown(Vector3.right), Is.True);
            Assert.That(TrainingDummy.IsDown(Quaternion.Euler(29f, 0f, 0f) * Vector3.up), Is.False);
            Assert.That(TrainingDummy.IsDown(Quaternion.Euler(31f, 0f, 0f) * Vector3.up), Is.True);
            Vector3 torque = TrainingDummy.CalculateUprightTorque(Quaternion.Euler(90f, 0f, 0f), Vector3.zero, 42f, 4.5f);
            Assert.That(torque.sqrMagnitude, Is.GreaterThan(1f));
            Vector3 capped = TrainingDummy.CalculateUprightTorque(Quaternion.Euler(29f, 0f, 0f),
                Vector3.zero, 1000f, 0f, TrainingDummy.MaximumUprightAcceleration);
            Assert.That(capped.magnitude, Is.EqualTo(TrainingDummy.MaximumUprightAcceleration).Within(0.001f));
        }

        [Test]
        public void AttackDummyPrefabIsANetworkedOpponentWithoutPlayerInputComponents()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/PushUp/Prefabs/AttackDummy.prefab");
            Material material = AssetDatabase.LoadAssetAtPath<Material>("Assets/PushUp/Materials/AttackDummy.mat");
            Assert.That(prefab, Is.Not.Null);
            Assert.That(prefab.GetComponent<AttackDummy>(), Is.Not.Null);
            AttackDummy attackDummy = prefab.GetComponent<AttackDummy>();
            Assert.That(attackDummy.ConfiguredPunchImpulse, Is.EqualTo(AttackDummy.DefaultPunchImpulse));
            Assert.That(attackDummy.ConfiguredGrabPullImpulse, Is.EqualTo(AttackDummy.DefaultGrabPullImpulse));
            Assert.That(attackDummy.ConfiguredPushImpulse, Is.EqualTo(AttackDummy.DefaultPushImpulse));
            Assert.That(prefab.GetComponent<AttackDummyNetworkRelay>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<TrainingDummy>(), Is.Not.Null);
            Assert.That(prefab.GetComponent("NetworkObject"), Is.Not.Null);
            Assert.That(prefab.GetComponent("NetworkTransform"), Is.Not.Null);
            Assert.That(prefab.GetComponent<PlayerInputReader>(), Is.Null);
            Assert.That(prefab.GetComponent<PlayerMotor>(), Is.Null);
            Assert.That(prefab.GetComponent<PlayerInteraction>(), Is.Null);
            SerializedObject networkTransform = new(prefab.GetComponent("NetworkTransform"));
            Assert.That(networkTransform.FindProperty("_componentConfiguration").intValue, Is.EqualTo(2));
            Assert.That(networkTransform.FindProperty("_interval").intValue, Is.EqualTo(3));
            Assert.That(networkTransform.FindProperty("_interpolation").intValue, Is.Zero);
            Assert.That(networkTransform.FindProperty("_synchronizeScale").boolValue, Is.False);
            Assert.That(prefab.GetComponent<RemoteActorPresentation>(), Is.Not.Null);
            Assert.That(PlayerInteraction.IsLocalOnlyBody(prefab.GetComponent<Rigidbody>()), Is.False,
                "the replicated fighter also has TrainingDummy behavior but must still use server RPCs");
            foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                Assert.That(renderer.sharedMaterial, Is.SameAs(material));
        }

        [Test]
        public void YawReplicationRoundTripsWithoutAVisibleDirectionError()
        {
            foreach (float yaw in new[] { 0f, 0.1f, 45f, 179.9f, 270f, 359.9f })
            {
                float decoded = PlayerPhysics.DecodeYaw(PlayerPhysics.EncodeYaw(yaw));
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(yaw, decoded)), Is.LessThan(0.01f));
            }
        }

        [Test]
        public void OwnerCameraYawIsNotOverwrittenByAnOlderServerReconcile()
        {
            const float localCameraYaw = 128f;
            const float olderServerYaw = 93f;
            Assert.That(PlayerMotor.ReconciledPresentationYaw(localCameraYaw, olderServerYaw, true),
                Is.EqualTo(localCameraYaw).Within(0.001f),
                "the owning client must keep its immediate look response during reconciliation");
            Assert.That(PlayerMotor.ReconciledPresentationYaw(localCameraYaw, olderServerYaw, false),
                Is.EqualTo(olderServerYaw).Within(0.001f),
                "spectators still render the authoritative rotation");
        }

        [Test]
        public void BoulderAutoFacingCannotOverwritePresentationYaw()
        {
            const float presentationYaw = 128f;
            const float bodyYaw = 42f;
            Assert.That(PlayerPhysics.SelectMotorYaw(presentationYaw, bodyYaw, true, false),
                Is.EqualTo(bodyYaw).Within(0.001f),
                "hands-on auto-facing may retain or adjust the physical body without changing camera yaw");
            Assert.That(PlayerPhysics.SelectMotorYaw(presentationYaw, bodyYaw, true, true),
                Is.EqualTo(presentationYaw).Within(0.001f),
                "live owner look must take priority on the next simulation tick");
            Assert.That(PlayerPhysics.SelectMotorYaw(presentationYaw, bodyYaw, false, false),
                Is.EqualTo(presentationYaw).Within(0.001f));
        }

        [Test]
        public void RenderFrameLookRemainsLatchedUntilSimulationConsumesIt()
        {
            Assert.That(PlayerPhysics.IsLookActiveForSimulation(true, Vector2.zero), Is.True,
                "a mouse delta sampled between physics ticks must survive until the next tick");
            Assert.That(PlayerPhysics.IsLookActiveForSimulation(false, new Vector2(0.02f, 0f)), Is.True);
            Assert.That(PlayerPhysics.IsLookActiveForSimulation(false, Vector2.zero), Is.False);
        }

        [Test]
        public void SharedWorldMovementIntentIsBoundedAndYawIsQuantized()
        {
            PlayerMovementIntent constructed = new(new Vector2(10f, -10f), true, true, -45f, 17u);
            Assert.That(constructed.Move.magnitude, Is.LessThanOrEqualTo(1.0001f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(315f, constructed.YawDegrees)), Is.LessThan(0.01f));
            Assert.That(constructed.Sequence, Is.EqualTo(17u));

            PlayerMovementIntent untrusted = new()
            {
                Move = new Vector2(float.MaxValue, float.MaxValue),
                Sprint = true,
                BoulderPushStance = true,
                Yaw = ushort.MaxValue,
                Sequence = 23u
            };
            PlayerMovementIntent sanitized = untrusted.Sanitized();
            Assert.That(sanitized.Move.magnitude, Is.LessThanOrEqualTo(1.0001f));
            Assert.That(sanitized.Sequence, Is.EqualTo(23u));

            untrusted.Move = new Vector2(float.NaN, float.PositiveInfinity);
            Assert.That(untrusted.Sanitized().Move, Is.EqualTo(Vector2.zero),
                "malformed owner intent must never feed NaN or infinity into host boulder physics");

            PlayerSharedWorldIntent shared = new(BoulderIntentMode.Stance, null,
                new Vector3(float.NaN, 2f, 3f), Vector3.one * 100f, new Vector2(5f, -5f), true,
                405f, 42u, 7u);
            Assert.That(shared.Position, Is.EqualTo(Vector3.zero));
            Assert.That(shared.Velocity.magnitude, Is.LessThanOrEqualTo(22.001f));
            Assert.That(shared.Move.magnitude, Is.LessThanOrEqualTo(1.001f));
            Assert.That(shared.StanceGeneration, Is.EqualTo(7u));
        }

        [Test]
        public void TimedMovementEffectsExpireOnSimulationTicks()
        {
            float multiplier = 1.35f;
            int ticks = PlayerPhysics.DurationToTicks(0.04f, 0.02f);
            Assert.That(ticks, Is.EqualTo(2));
            PlayerPhysics.AdvanceTimedMultiplier(ref multiplier, ref ticks);
            Assert.That(multiplier, Is.EqualTo(1.35f));
            PlayerPhysics.AdvanceTimedMultiplier(ref multiplier, ref ticks);
            Assert.That(multiplier, Is.EqualTo(1f));
            Assert.That(ticks, Is.Zero);
        }

        [Test]
        public void SharedSimulationStepProducesTheSameOfflineAndPredictedIntent()
        {
            GameObject first = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            GameObject second = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            GameObject firstFloor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject secondFloor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                const float tickDelta = 1f / 60f;
                first.transform.position = new Vector3(-100f, 1f, 0f);
                second.transform.position = new Vector3(100f, 1f, 0f);
                firstFloor.transform.SetPositionAndRotation(new Vector3(-100f, -0.5f, 0f), Quaternion.identity);
                secondFloor.transform.SetPositionAndRotation(new Vector3(100f, -0.5f, 0f), Quaternion.identity);
                firstFloor.transform.localScale = new Vector3(40f, 1f, 40f);
                secondFloor.transform.localScale = new Vector3(40f, 1f, 40f);
                firstFloor.layer = GameplayLayers.Terrain;
                secondFloor.layer = GameplayLayers.Terrain;
                Rigidbody firstBody = first.AddComponent<Rigidbody>();
                Rigidbody secondBody = second.AddComponent<Rigidbody>();
                CapsuleCollider firstCapsule = first.GetComponent<CapsuleCollider>();
                CapsuleCollider secondCapsule = second.GetComponent<CapsuleCollider>();
                PlayerPhysics.ConfigureBody(firstBody, firstCapsule);
                PlayerPhysics.ConfigureBody(secondBody, secondCapsule);
                PlayerSimulationState firstState = new() { GroundNormal = Vector3.up, Yaw = 37f };
                PlayerSimulationState secondState = firstState;
                Physics.SyncTransforms();

                for (int tick = 0; tick < 600; tick++)
                {
                    Vector2 move = Vector2.ClampMagnitude(new Vector2(
                        Mathf.Sin(tick * 0.071f), Mathf.Cos(tick * 0.043f)), 1f);
                    bool jumpPressed = tick is 20 or 145 or 310 or 488;
                    bool jumpHeld = (tick >= 20 && tick < 31) || (tick >= 145 && tick < 151) ||
                                    (tick >= 310 && tick < 329) || (tick >= 488 && tick < 496);
                    bool crouchHeld = tick % 137 >= 92 && tick % 137 < 112;
                    bool crouchPressed = tick % 137 == 92;
                    bool sprint = tick % 180 >= 55 && tick % 180 < 125;
                    float yaw = Mathf.Repeat(37f + tick * 1.37f, 360f);
                    PlayerSimulationInput input = new(move, jumpPressed, jumpHeld, sprint, crouchHeld,
                        crouchPressed, tick % 47 < 8, yaw, tick >= 360 && tick < 480 ? 1.35f : 1f, null);

                    PlayerSimulationStep predicted = PlayerPhysics.SimulatePlayerStep(firstCapsule,
                        first.transform, firstBody, input, firstCapsule.height, firstCapsule.center,
                        tickDelta, ref firstState);
                    PlayerSimulationStep offline = PlayerPhysics.SimulatePlayerStep(secondCapsule,
                        second.transform, secondBody, input, secondCapsule.height, secondCapsule.center,
                        tickDelta, ref secondState);

                    Assert.That(offline.Velocity, Is.EqualTo(predicted.Velocity), $"velocity at tick {tick}");
                    Assert.That(offline.MoveDirection, Is.EqualTo(predicted.MoveDirection), $"intent at tick {tick}");
                    Assert.That(offline.Rotation, Is.EqualTo(predicted.Rotation), $"rotation at tick {tick}");
                    Assert.That(offline.PositionCorrection, Is.EqualTo(predicted.PositionCorrection),
                        $"correction at tick {tick}");
                    Assert.That(secondState.CoyoteTicks, Is.EqualTo(firstState.CoyoteTicks), $"coyote at tick {tick}");
                    Assert.That(secondState.BufferTicks, Is.EqualTo(firstState.BufferTicks), $"buffer at tick {tick}");
                    Assert.That(secondState.Crouched, Is.EqualTo(firstState.Crouched), $"crouch at tick {tick}");
                    Assert.That(secondState.Sliding, Is.EqualTo(firstState.Sliding), $"slide at tick {tick}");
                    Assert.That(secondState.SlideSprintGraceTicks,
                        Is.EqualTo(firstState.SlideSprintGraceTicks), $"slide grace at tick {tick}");
                    Assert.That(secondState.SlideLevelTicks, Is.EqualTo(firstState.SlideLevelTicks),
                        $"slide duration at tick {tick}");
                    Assert.That(secondState.SlideWasDownhill, Is.EqualTo(firstState.SlideWasDownhill),
                        $"downhill slide state at tick {tick}");
                    Assert.That(secondState.Grounded, Is.EqualTo(firstState.Grounded), $"grounded at tick {tick}");
                    Assert.That(secondState.GroundedOnBoulder, Is.EqualTo(firstState.GroundedOnBoulder));
                    Assert.That(secondState.BoulderLandingArmed, Is.EqualTo(firstState.BoulderLandingArmed));
                    Assert.That(secondState.GroundNormal, Is.EqualTo(firstState.GroundNormal));

                    firstBody.linearVelocity = predicted.Velocity;
                    secondBody.linearVelocity = offline.Velocity;
                    firstBody.position += predicted.Velocity * tickDelta + predicted.PositionCorrection;
                    secondBody.position += offline.Velocity * tickDelta + offline.PositionCorrection;
                    firstBody.rotation = predicted.Rotation;
                    secondBody.rotation = offline.Rotation;
                    Physics.SyncTransforms();
                }
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(firstFloor);
                Object.DestroyImmediate(secondFloor);
            }
        }

        [Test]
        public void NetworkPowerupsOnlyProcessTriggersOnTheServer()
        {
            Assert.That(PowerupPickup.ShouldProcessTrigger(false, false, false), Is.True,
                "offline pickups share the same implementation");
            Assert.That(PowerupPickup.ShouldProcessTrigger(true, false, false), Is.True,
                "an unspawned prefab instance is an offline object");
            Assert.That(PowerupPickup.ShouldProcessTrigger(true, true, false), Is.False);
            Assert.That(PowerupPickup.ShouldProcessTrigger(true, true, true), Is.True);
        }

        [Test]
        public void RemotePlayerInputMapsRemainDisabled()
        {
            GameObject player = new("input ownership test");
            try
            {
                PlayerInputReader reader = player.AddComponent<PlayerInputReader>();
                Assert.That(reader.LocalControlEnabled, Is.False);
                reader.SetLocalControlEnabled(true);
                Assert.That(reader.LocalControlEnabled, Is.True);
                reader.SetLocalControlEnabled(false);
                Assert.That(reader.LocalControlEnabled, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void AttackDummyPursuesForFiveSecondsAndUsesToppleAndGrabPushTuning()
        {
            Assert.That(AttackDummy.AggroDuration, Is.EqualTo(7f));
            Assert.That(AttackDummy.IsAggressiveAt(3f, 6f, true), Is.True);
            Assert.That(AttackDummy.IsAggressiveAt(6f, 6f, true), Is.False);
            Assert.That(AttackDummy.IsAggressiveAt(3f, 6f, false), Is.False);
            Assert.That(AttackDummy.AttackImpulse(Vector3.forward, false).magnitude,
                Is.EqualTo(AttackDummy.DefaultPunchImpulse).Within(0.001f));
            Assert.That(AttackDummy.AttackImpulse(Vector3.forward, true).magnitude,
                Is.EqualTo(AttackDummy.DefaultPushImpulse).Within(0.001f));
            Assert.That(AttackDummy.GrabPullImpulse(Vector3.forward).magnitude,
                Is.EqualTo(AttackDummy.DefaultGrabPullImpulse).Within(0.001f));
            Assert.That(AttackDummy.ShouldUseGrabPush(0f), Is.True);
            Assert.That(AttackDummy.ShouldUseGrabPush(1f), Is.False);
            Assert.That(AttackDummy.CanUseGrabPush(false, 0f), Is.True);
            Assert.That(AttackDummy.CanUseGrabPush(true, 0f), Is.False,
                "the fighter may only start one grab combo during an aggro window");
            Assert.That(AttackDummy.GrabHoldDuration, Is.GreaterThan(0f));
            Assert.That(AttackDummy.GrabHoldDuration, Is.LessThanOrEqualTo(AttackDummy.MaximumGrabDuration));
            Assert.That(AttackDummy.MaximumGrabDuration, Is.LessThanOrEqualTo(1f));
            Assert.That(ActiveRagdollPuppet.ImpactTiltDegrees(AttackDummy.DefaultPunchImpulse),
                Is.GreaterThan(TrainingDummy.MaximumStandingTiltDegrees));
            Assert.That(ActiveRagdollPuppet.ImpactTiltDegrees(AttackDummy.DefaultPushImpulse),
                Is.GreaterThan(TrainingDummy.MaximumStandingTiltDegrees));
            Assert.That(ActiveRagdollPuppet.ImpactTiltDegrees(AttackDummy.DefaultGrabPullImpulse),
                Is.LessThan(ActiveRagdollPuppet.PlayerKnockdownTiltDegrees));
            Vector3 pursued = AttackDummy.CalculatePursuitVelocity(Vector3.zero, Vector3.forward, 10f);
            Assert.That(Vector3.ProjectOnPlane(pursued, Vector3.up).magnitude,
                Is.EqualTo(AttackDummy.PursuitSpeed).Within(0.001f));
            Assert.That(AttackDummy.NextAttackDelay(0f), Is.EqualTo(AttackDummy.MinimumAttackCooldown));
            Assert.That(AttackDummy.NextAttackDelay(1f), Is.EqualTo(AttackDummy.MaximumAttackCooldown));
            Assert.That(AttackDummy.PersonalSpaceRange, Is.LessThan(AttackDummy.AttackRange));
            Assert.That(AttackDummy.DecisionInterval, Is.EqualTo(0.1f));
        }

        [Test]
        public void ExternalPlayerImpulsesAreIdempotentPerSourceAndTick()
        {
            GameObject player = new("external impulse test");
            try
            {
                player.AddComponent<Rigidbody>();
                player.AddComponent<CapsuleCollider>();
                player.AddComponent<PlayerInputReader>();
                PlayerMotor motor = player.AddComponent<PlayerMotor>();
                Assert.That(motor.TryApplyExternalImpulse(42u, 7, Vector3.forward * 10f, Vector3.zero), Is.True);
                Assert.That(motor.TryApplyExternalImpulse(42u, 7, Vector3.forward * 10f, Vector3.zero), Is.False);
                Assert.That(motor.TryApplyExternalImpulse(42u, 8, Vector3.forward * 10f, Vector3.zero), Is.True,
                    "different attackers may hit on the same simulation tick");
                Assert.That(motor.TryApplyExternalImpulse(43u, 7, Vector3.forward * 10f, Vector3.zero), Is.True);
                Assert.That(motor.TryApplyExternalImpulse(41u, 7, Vector3.forward * 10f, Vector3.zero), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void PunchReplicationUsesOneTickStampedInteractionResult()
        {
            Assert.That(typeof(InteractionResultPayload).GetField(nameof(InteractionResultPayload.Target)), Is.Not.Null);
            Assert.That(typeof(InteractionResultPayload).GetField(nameof(InteractionResultPayload.Impulse)), Is.Not.Null);
            Assert.That(typeof(InteractionResultPayload).GetField(nameof(InteractionResultPayload.LocalHitPoint)), Is.Not.Null);
            Assert.That(typeof(InteractionResultPayload).GetField(nameof(InteractionResultPayload.LocalHitNormal)), Is.Not.Null);
            Assert.That(typeof(InteractionResultPayload).GetField(nameof(InteractionResultPayload.SimulationTick)), Is.Not.Null);
            Assert.That(typeof(PlayerMotor).GetMethod("PublishInteractionResultObserversRpc",
                BindingFlags.Instance | BindingFlags.NonPublic), Is.Not.Null);
            Assert.That(typeof(PlayerMotor).GetMethod("PlayInteractionObserversRpc",
                BindingFlags.Instance | BindingFlags.NonPublic), Is.Null);
            Assert.That(typeof(PlayerMotor).GetMethod("ReactToHitObserversRpc",
                BindingFlags.Instance | BindingFlags.NonPublic), Is.Null);
            Assert.That(typeof(PlayerMotor).GetMethod("ShowPunchImpactObserversRpc",
                BindingFlags.Instance | BindingFlags.NonPublic), Is.Null);
        }

        [Test]
        public void PlayerHitReactionUsesTheSameThirtyDegreeKnockdownThreshold()
        {
            Assert.That(ActiveRagdollPuppet.ImpactTiltDegrees(PlayerInteraction.PunchImpulse),
                Is.LessThan(ActiveRagdollPuppet.PlayerKnockdownTiltDegrees),
                "a normal punch may stagger without guaranteeing a knockdown");
            Assert.That(ActiveRagdollPuppet.ImpactTiltDegrees(PlayerInteraction.GrabPunchImpulse),
                Is.GreaterThan(ActiveRagdollPuppet.PlayerKnockdownTiltDegrees),
                "a PUSH should normally cross the shared 30-degree knockdown threshold");
        }

        [Test]
        public void KnockdownAddsFirstPersonCameraFallAndRecoveryPresentation()
        {
            GameObject player = new("camera knockdown presentation");
            try
            {
                ActiveRagdollPuppet puppet = player.AddComponent<ActiveRagdollPuppet>();
                puppet.ReactToImpact(Vector3.forward * AttackDummy.DefaultPunchImpulse);
                Assert.That(puppet.IsKnockedDown, Is.True);
                Assert.That(ActiveRagdollPuppet.PlayerControlLockDuration, Is.LessThanOrEqualTo(1f));
                Vector3 fallenPosition = ActiveRagdollPuppet.LerpCameraReaction(Vector3.zero,
                    new Vector3(0f, -0.72f, -0.2f), ActiveRagdollPuppet.CameraFallPositionSharpness, 1f / 60f);
                Assert.That(fallenPosition.y, Is.LessThan(0f));
                Assert.That(fallenPosition.y, Is.GreaterThan(-0.72f), "the camera must visibly lerp into the fall");
                Quaternion fallenRotation = ActiveRagdollPuppet.SlerpCameraReaction(Quaternion.identity,
                    Quaternion.Euler(24f, 0f, 35f), ActiveRagdollPuppet.CameraFallRotationSharpness, 1f / 60f);
                Assert.That(Quaternion.Angle(Quaternion.identity, fallenRotation), Is.GreaterThan(0f));
                Assert.That(Quaternion.Angle(fallenRotation, Quaternion.Euler(24f, 0f, 35f)), Is.GreaterThan(0f),
                    "the camera must visibly slerp instead of snapping to the disorienting rotation");
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void OfflineInteractionFindsRagdollAddedAfterItsAwake()
        {
            GameObject player = new("late standalone ragdoll");
            try
            {
                player.AddComponent<PlayerInputReader>();
                PlayerInteraction interaction = player.AddComponent<PlayerInteraction>();
                ActiveRagdollPuppet puppet = player.AddComponent<ActiveRagdollPuppet>();

                interaction.ReactFromHit(Vector3.forward * AttackDummy.DefaultPunchImpulse);

                Assert.That(puppet.IsKnockedDown, Is.True,
                    "standalone creation adds the presentation puppet after PlayerInteraction initializes");
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void ServerOnlyPlayersNeedExplicitLocalClassification()
        {
            Assert.That(PlayerMotor.ShouldBeLocallyControlled(false, false), Is.False);
            Assert.That(PlayerMotor.ShouldBeLocallyControlled(true, false), Is.True);
            Assert.That(PlayerMotor.ShouldBeLocallyControlled(false, true), Is.True);
            Assert.That(PlayerMotor.ShouldSimulateNetworkPhysics(false, false), Is.False,
                "pure-client remote replicas follow NetworkTransform and must not enter local PhysX");
            Assert.That(PlayerMotor.ShouldSimulateNetworkPhysics(false, true), Is.True);
            Assert.That(PlayerMotor.ShouldSimulateNetworkPhysics(true, false), Is.False,
                "a server copy of a remotely-owned player is a kinematic proxy, not a second simulation");
            Assert.That(PlayerMotor.ShouldSimulateNetworkPhysics(true, true), Is.True);
        }

        [Test]
        public void MenuInputGateDisablesAndRestoresGameplayInput()
        {
            GameObject menuObject = new("Menu input gate test");
            try
            {
                PushUpMenu menu = menuObject.AddComponent<PushUpMenu>();
                Assert.That(PlayerInputReader.GameplayEnabled, Is.False);

                MethodInfo setMenuVisible = typeof(PushUpMenu).GetMethod("SetMenuVisible", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(setMenuVisible, Is.Not.Null);
                setMenuVisible.Invoke(menu, new object[] { false });
                Assert.That(PlayerInputReader.GameplayEnabled, Is.True);

                setMenuVisible.Invoke(menu, new object[] { true });
                Assert.That(PlayerInputReader.GameplayEnabled, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(menuObject);
            }
        }

        [Test]
        public void MenuStartsWithoutConfirmationAndUsesDenseFastFriendScrolling()
        {
            GameObject menuObject = new("Menu presentation test");
            try
            {
                PushUpMenu menu = menuObject.AddComponent<PushUpMenu>();
                const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

                FieldInfo confirmationField = typeof(PushUpMenu).GetField("_confirmationPanel", privateInstance);
                Assert.That(confirmationField, Is.Not.Null);
                if (confirmationField.GetValue(menu) == null)
                {
                    MethodInfo awake = typeof(PushUpMenu).GetMethod("Awake", privateInstance);
                    Assert.That(awake, Is.Not.Null);
                    awake.Invoke(menu, null);
                }

                GameObject confirmation = confirmationField.GetValue(menu) as GameObject;
                Assert.That(confirmation, Is.Not.Null);
                Assert.That(confirmation.activeSelf, Is.False,
                    "the confirmation modal must only appear after a destructive action is requested");

                PerformanceDebugOverlay performance = menuObject.GetComponent<PerformanceDebugOverlay>();
                Assert.That(performance, Is.Not.Null);
                Assert.That(performance.IsVisible, Is.False);
                performance.Toggle();
                Assert.That(performance.IsVisible, Is.True);
                performance.Toggle();
                Assert.That(performance.IsVisible, Is.False);

                foreach (Text text in menuObject.GetComponentsInChildren<Text>(true))
                    Assert.That(text.text, Does.Not.Contain("Choose a mode explicitly"));

                GameObject inviteContent = typeof(PushUpMenu).GetField("_inviteFriendList", privateInstance)
                    ?.GetValue(menu) as GameObject;
                Assert.That(inviteContent, Is.Not.Null);
                GameObject pauseInviteContent = typeof(PushUpMenu).GetField("_pauseInviteFriendList", privateInstance)
                    ?.GetValue(menu) as GameObject;
                Assert.That(pauseInviteContent, Is.Not.Null,
                    "the in-run session menu needs the same direct-invite fallback as the lobby");
                GameObject scrollRoot = inviteContent.transform.parent.parent.gameObject;
                Assert.That(scrollRoot.activeSelf, Is.False);
                Assert.That(scrollRoot.GetComponent<LayoutElement>().preferredHeight, Is.GreaterThanOrEqualTo(390f));
                Assert.That(scrollRoot.GetComponent<ScrollRect>().scrollSensitivity, Is.GreaterThanOrEqualTo(40f));
                Assert.That(scrollRoot.GetComponentInChildren<Scrollbar>(true), Is.Not.Null);

                MethodInfo refresh = typeof(PushUpMenu).GetMethod("RefreshInviteFriends", privateInstance);
                Assert.That(refresh, Is.Not.Null);
                refresh.Invoke(menu, null);
                Text[] inviteLabels = inviteContent.GetComponentsInChildren<Text>(true);
                Assert.That(System.Array.Exists(inviteLabels,
                    text => text.text.Contains("Steam Overlay unavailable")), Is.True);
                Assert.That(System.Array.Exists(inviteLabels,
                    text => text.text == "Open Steam Invite Overlay"), Is.False);

                GameObject pauseScrollRoot = pauseInviteContent.transform.parent.parent.gameObject;
                Assert.That(pauseScrollRoot.activeSelf, Is.False);
                MethodInfo togglePauseInvites = typeof(PushUpMenu).GetMethod("TogglePauseInviteFriends",
                    privateInstance);
                Assert.That(togglePauseInvites, Is.Not.Null);
                togglePauseInvites.Invoke(menu, null);
                Assert.That(pauseScrollRoot.activeSelf, Is.True,
                    "Invite Friends must open the direct friend list even when the Steam Overlay is unavailable");
                Text[] pauseInviteLabels = pauseInviteContent.GetComponentsInChildren<Text>(true);
                Assert.That(System.Array.Exists(pauseInviteLabels,
                    text => text.text.Contains("direct invites still work")), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(menuObject);
            }
        }

        [Test]
        public void PlayerAndBoulderBodiesUseApprovedTuning()
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            GameObject boulder = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            try
            {
                Rigidbody playerBody = player.AddComponent<Rigidbody>();
                Rigidbody boulderBody = boulder.AddComponent<Rigidbody>();
                PlayerPhysics.ConfigureBody(playerBody);
                BoulderController.ConfigureBody(boulderBody, boulder.GetComponent<Collider>());

                Assert.That(playerBody.mass, Is.EqualTo(78f));
                Assert.That(playerBody.useGravity, Is.False);
                Assert.That(playerBody.interpolation, Is.EqualTo(RigidbodyInterpolation.Interpolate));
                Assert.That(playerBody.collisionDetectionMode, Is.EqualTo(CollisionDetectionMode.Continuous));
                Assert.That(playerBody.constraints, Is.EqualTo(RigidbodyConstraints.FreezeRotation));
                Assert.That(playerBody.solverIterations, Is.EqualTo(12));
                Assert.That(playerBody.solverVelocityIterations, Is.EqualTo(4));
                Assert.That(boulderBody.mass, Is.EqualTo(150f));
                Assert.That(boulderBody.linearDamping, Is.EqualTo(0.08f));
                Assert.That(boulderBody.angularDamping, Is.EqualTo(0.3f));
                Assert.That(boulderBody.collisionDetectionMode, Is.EqualTo(CollisionDetectionMode.ContinuousDynamic));
                Assert.That(boulderBody.solverIterations, Is.EqualTo(12));
                Assert.That(boulderBody.solverVelocityIterations, Is.EqualTo(4));
                Assert.That(boulder.GetComponent<Collider>().material.bounciness, Is.Zero);
                Assert.That(Time.fixedDeltaTime, Is.EqualTo(1f / 60f).Within(0.00001f));
            }
            finally
            {
                Object.DestroyImmediate(player);
                Object.DestroyImmediate(boulder);
            }
        }

        [Test]
        public void BoulderAssistExpiresByPhysicsTicksWithoutACoroutine()
        {
            GameObject boulder = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            try
            {
                Rigidbody body = boulder.AddComponent<Rigidbody>();
                BoulderController controller = boulder.AddComponent<BoulderController>();
                controller.ApplyTeamAssist(Time.fixedDeltaTime * 2f);
                Assert.That(body.mass, Is.EqualTo(BoulderController.AssistedMass));
                Assert.That(controller.AssistTicksRemaining, Is.EqualTo(2));

                MethodInfo tick = typeof(BoulderController).GetMethod("FixedUpdate",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(tick, Is.Not.Null);
                tick.Invoke(controller, null);
                Assert.That(body.mass, Is.EqualTo(BoulderController.AssistedMass));
                tick.Invoke(controller, null);
                Assert.That(body.mass, Is.EqualTo(BoulderController.DefaultMass));
                Assert.That(controller.AssistTicksRemaining, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(boulder);
            }
        }

        [Test]
        public void GroundProbeFindsFloorButDoesNotFindOwnColliderOrWall()
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                player.transform.position = new Vector3(0f, 1.03f, 0f);
                floor.transform.position = new Vector3(0f, -0.1f, 0f);
                floor.transform.localScale = new Vector3(10f, 0.2f, 10f);
                wall.transform.position = new Vector3(0.7f, 1f, 0f);
                wall.transform.localScale = new Vector3(0.2f, 4f, 4f);
                Physics.SyncTransforms();

                Assert.That(PlayerPhysics.IsGrounded(player.GetComponent<CapsuleCollider>(), player.transform, out RaycastHit hit), Is.True);
                Assert.That(hit.collider, Is.EqualTo(floor.GetComponent<Collider>()));

                floor.SetActive(false);
                Physics.SyncTransforms();
                Assert.That(PlayerPhysics.IsGrounded(player.GetComponent<CapsuleCollider>(), player.transform, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(player);
                Object.DestroyImmediate(floor);
                Object.DestroyImmediate(wall);
            }
        }

        [Test]
        public void SurfaceClassificationUsesAuthoredLayersBeforeLegacyHierarchyWalks()
        {
            GameObject surface = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                Collider collider = surface.GetComponent<Collider>();
                surface.layer = GameplayLayers.Terrain;
                Assert.That(PlayerPhysics.ClassifySurface(collider), Is.EqualTo(MovementSurfaceKind.StaticTerrain));
                surface.layer = GameplayLayers.Boulder;
                Assert.That(PlayerPhysics.ClassifySurface(collider), Is.EqualTo(MovementSurfaceKind.Boulder));
                surface.layer = GameplayLayers.Player;
                Assert.That(PlayerPhysics.ClassifySurface(collider), Is.EqualTo(MovementSurfaceKind.Player));
                surface.layer = GameplayLayers.Actor;
                Assert.That(PlayerPhysics.ClassifySurface(collider), Is.EqualTo(MovementSurfaceKind.Player));
                surface.layer = GameplayLayers.Interactable;
                Assert.That(PlayerPhysics.ClassifySurface(collider), Is.EqualTo(MovementSurfaceKind.NonWalkable));
                surface.AddComponent<Rigidbody>();
                Assert.That(PlayerPhysics.ClassifySurface(collider), Is.EqualTo(MovementSurfaceKind.DynamicProp));
                surface.layer = GameplayLayers.Pickup;
                Assert.That(PlayerPhysics.ClassifySurface(collider), Is.EqualTo(MovementSurfaceKind.NonWalkable));
            }
            finally
            {
                Object.DestroyImmediate(surface);
            }
        }

        [Test]
        public void GameplayQueryMasksExcludePresentationAndPromoteOnlyStaticLegacyColliders()
        {
            Assert.That((GameplayLayers.InteractionQueryMask & (1 << GameplayLayers.Presentation)), Is.Zero);
            Assert.That((GameplayLayers.InteractionQueryMask & (1 << GameplayLayers.GameplayTrigger)), Is.Zero);
            Assert.That((GameplayLayers.GroundQueryMask & (1 << GameplayLayers.Terrain)), Is.Not.Zero);
            Assert.That((GameplayLayers.GroundQueryMask & (1 << GameplayLayers.Boulder)), Is.Not.Zero);
            Assert.That((GameplayLayers.GroundQueryMask & 1), Is.Not.Zero,
                "legacy Default remains queryable until the one-time scene bootstrap runs");

            GameObject candidate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                Collider collider = candidate.GetComponent<Collider>();
                candidate.layer = GameplayLayers.LegacyDefault;
                Assert.That(GameplayLayers.ShouldPromoteToStaticTerrain(collider), Is.True);
                collider.isTrigger = true;
                Assert.That(GameplayLayers.ShouldPromoteToStaticTerrain(collider), Is.False);
                collider.isTrigger = false;
                candidate.AddComponent<Rigidbody>();
                Assert.That(GameplayLayers.ShouldPromoteToStaticTerrain(collider), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(candidate);
            }
        }

        [Test]
        public void RemotePlayerProxyRemainsQueryableWithoutInjectingKinematicContactForces()
        {
            MethodInfo reset = typeof(GameplayLayers).GetMethod("ResetRuntimeState",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(reset, Is.Not.Null);
            reset.Invoke(null, null);

            Assert.That((GameplayLayers.InteractionQueryMask &
                         (1 << GameplayLayers.RemotePlayerProxy)), Is.Not.Zero);
            Assert.That(Physics.GetIgnoreLayerCollision(GameplayLayers.RemotePlayerProxy,
                GameplayLayers.Player), Is.True);
            Assert.That(Physics.GetIgnoreLayerCollision(GameplayLayers.RemotePlayerProxy,
                GameplayLayers.Boulder), Is.True);
            Assert.That(Physics.GetIgnoreLayerCollision(GameplayLayers.RemotePlayerProxy,
                GameplayLayers.Actor), Is.True);
        }

        [Test]
        public void GroundProbeAcceptsSupportedSlopeAndRejectsSteepSlope()
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            GameObject slope = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                player.transform.position = new Vector3(0f, 1.04f, 0f);
                slope.transform.position = new Vector3(0f, -0.1f, 0f);
                slope.transform.localScale = new Vector3(10f, 0.2f, 10f);
                slope.transform.rotation = Quaternion.Euler(0f, 0f, 30f);
                Physics.SyncTransforms();
                Assert.That(PlayerPhysics.IsGrounded(player.GetComponent<CapsuleCollider>(), player.transform, out _), Is.True);

                Vector3 steepNormal = Quaternion.Euler(0f, 0f, 75f) * Vector3.up;
                Assert.That(PlayerPhysics.IsWalkableNormal(steepNormal), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(player);
                Object.DestroyImmediate(slope);
            }
        }

        [Test]
        public void JumpAndInteractionForcesAreCapped()
        {
            Assert.That(PlayerPhysics.TakeoffVelocityChange(0f), Is.EqualTo(PlayerPhysics.JumpVelocity).Within(0.001f));
            Assert.That(PlayerPhysics.TakeoffVelocityChange(2f), Is.EqualTo(PlayerPhysics.JumpVelocity - 2f).Within(0.001f));
            Assert.That(PlayerPhysics.TakeoffVelocityChange(10f),
                Is.EqualTo(PlayerPhysics.JumpVelocity - 10f).Within(0.001f));

            Vector3 punch = PlayerInteraction.CalculatePunchImpulse(new Vector3(10f, 0f, 0f), 500f);
            Assert.That(punch.magnitude, Is.EqualTo(200f).Within(0.001f));
            Vector3 grabPunch = PlayerInteraction.CalculateGrabPunchImpulse(Vector3.right);
            Assert.That(grabPunch.magnitude, Is.EqualTo(400f).Within(0.001f));
            Assert.That(grabPunch.magnitude / PlayerInteraction.PunchImpulse, Is.EqualTo(2f).Within(0.001f));
            Assert.That(punch.magnitude / 150f, Is.EqualTo(1.3333f).Within(0.001f),
                "a free 150 kg boulder should receive the expected punch delta-v");
            Assert.That(grabPunch.magnitude / 150f, Is.EqualTo(2.6667f).Within(0.001f),
                "a free 150 kg boulder should receive the expected PUSH delta-v");
            Vector3 grab = PlayerInteraction.CalculateGrabForce(Vector3.right * 10f, Vector3.zero, 650f, 40f, 1100f);
            Assert.That(grab.magnitude, Is.EqualTo(1100f).Within(0.001f));
            Assert.That(PlayerInteraction.CanStartGrab(true, false, false), Is.True);
            Assert.That(PlayerInteraction.CanStartGrab(true, false, true), Is.False, "PUSH latch blocks reacquiring while RMB remains held");
            Assert.That(PlayerInteraction.CanStartGrab(false, false, false), Is.False, "release only rearms; the next press starts the grab");
        }

        [Test]
        public void ThreeAcceptedPunchesProduceFortyPercentFinisherAndCooldown()
        {
            int hits = 0;
            float lastHit = float.NegativeInfinity;
            PunchComboResult first = PlayerInteraction.AdvancePunchCombo(ref hits, ref lastHit, 1f, 0.2f);
            PunchComboResult second = PlayerInteraction.AdvancePunchCombo(ref hits, ref lastHit, 1.2f, 0.2f);
            PunchComboResult third = PlayerInteraction.AdvancePunchCombo(ref hits, ref lastHit, 1.4f, 0.2f);

            Assert.That(first.Step, Is.EqualTo(1));
            Assert.That(second.Step, Is.EqualTo(2));
            Assert.That(third.Step, Is.EqualTo(3));
            Assert.That(third.IsFinisher, Is.True);
            Assert.That(third.Multiplier, Is.EqualTo(1.4f).Within(0.0001f));
            Assert.That(third.Cooldown, Is.EqualTo(1.5f).Within(0.0001f));
            Assert.That(PlayerInteraction.CalculateComboPunchImpulse(Vector3.forward,
                PlayerInteraction.PunchImpulse, third.Multiplier).magnitude, Is.EqualTo(280f).Within(0.001f));
            Assert.That(hits, Is.Zero, "the completed combo starts a fresh chain after cooldown");
        }

        [Test]
        public void PunchComboExpiresWhenHitsAreTooFarApart()
        {
            int hits = 0;
            float lastHit = float.NegativeInfinity;
            PlayerInteraction.AdvancePunchCombo(ref hits, ref lastHit, 1f, 0.2f);
            PlayerInteraction.AdvancePunchCombo(ref hits, ref lastHit, 1.2f, 0.2f);
            PunchComboResult expired = PlayerInteraction.AdvancePunchCombo(ref hits, ref lastHit,
                1.2f + PlayerInteraction.PunchComboWindow + 0.01f, 0.2f);
            Assert.That(expired.Step, Is.EqualTo(1));
            Assert.That(expired.IsFinisher, Is.False);
        }

        [Test]
        public void TouchingBoulderIsDetectedWithoutAForwardCastHit()
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            GameObject boulder = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            try
            {
                player.transform.position = new Vector3(0f, 1f, 0f);
                boulder.transform.position = new Vector3(0f, 1f, 1.02f);
                boulder.layer = GameplayLayers.Boulder;
                Rigidbody boulderBody = boulder.AddComponent<Rigidbody>();
                boulderBody.useGravity = false;
                boulder.AddComponent<BoulderController>();
                Physics.SyncTransforms();

                Assert.That(PlayerPhysics.TryFindBoulderContact(player.GetComponent<CapsuleCollider>(),
                    player.transform, Vector2.up, 0f, out Rigidbody found), Is.True);
                Assert.That(found, Is.SameAs(boulderBody));
            }
            finally
            {
                Object.DestroyImmediate(player);
                Object.DestroyImmediate(boulder);
            }
        }

        [Test]
        public void SprintCrouchAndCrouchJumpBoostUseApprovedMovementTuning()
        {
            const float fixedStep = 1f / 60f;
            Assert.That(PlayerPhysics.CurrentMovementSpeed(1f, false, false, true), Is.EqualTo(10f));
            Assert.That(PlayerPhysics.CurrentMovementSpeed(1f, true, false, true), Is.EqualTo(15f));
            Assert.That(PlayerPhysics.CurrentMovementSpeed(1f, true, true, true), Is.EqualTo(3.3f));
            Assert.That(PlayerPhysics.CurrentMovementSpeed(1f, false, true, false), Is.EqualTo(10f),
                "air crouch must preserve normal movement speed");
            Assert.That(PlayerPhysics.CurrentMovementSpeed(1f, true, true, false), Is.EqualTo(15f),
                "air crouch must preserve sprint movement speed");

            int graceTicks = 0;
            int levelTicks = 0;
            bool wasDownhill = false;
            Assert.That(PlayerPhysics.AdvanceSlideState(false, true, false, false, true, true, true,
                PlayerPhysics.SprintSpeed, false, false, fixedStep, ref graceTicks, ref levelTicks,
                ref wasDownhill), Is.True, "sprint-speed crouch press starts a slide");
            graceTicks = 0;
            levelTicks = 0;
            wasDownhill = false;
            Assert.That(PlayerPhysics.AdvanceSlideState(false, true, false, false, true, true, false,
                PlayerPhysics.SprintSpeed, false, false, fixedStep, ref graceTicks, ref levelTicks,
                ref wasDownhill), Is.False, "walking crouch must not start a slide");
            Assert.That(PlayerPhysics.AdvanceSlideState(true, false, false, false, true, false, false,
                PlayerPhysics.SprintSpeed, false, false, fixedStep, ref graceTicks, ref levelTicks,
                ref wasDownhill), Is.True, "a held slide survives a jump");

            Vector3 levelSlide = PlayerPhysics.CalculateSlideVelocity(Vector3.forward * PlayerPhysics.SprintSpeed,
                Vector3.forward, Vector3.up, Vector3.zero, true, 1f, fixedStep);
            Vector3 downhillNormal = Quaternion.AngleAxis(25f, Vector3.right) * Vector3.up;
            Vector3 downhillSlide = PlayerPhysics.CalculateSlideVelocity(
                Vector3.ProjectOnPlane(Vector3.forward * PlayerPhysics.SprintSpeed, downhillNormal).normalized *
                PlayerPhysics.SprintSpeed, Vector3.forward, downhillNormal, Vector3.zero, true, 1f, fixedStep);
            Assert.That(Vector3.ProjectOnPlane(levelSlide, Vector3.up).magnitude,
                Is.GreaterThan(PlayerPhysics.SprintSpeed), "level slide exceeds sprint speed");
            Assert.That(Vector3.ProjectOnPlane(downhillSlide, downhillNormal).magnitude,
                Is.GreaterThan(Vector3.ProjectOnPlane(levelSlide, Vector3.up).magnitude),
                "downhill gravity makes skiing faster than a level slide");

            Vector3 preservedAirSpeed = PlayerPhysics.CalculateLocomotionVelocity(Vector3.forward * 20f,
                Vector3.forward, PlayerPhysics.SprintSpeed, false, Vector3.up, Vector3.zero, fixedStep, true);
            Assert.That(new Vector2(preservedAirSpeed.x, preservedAirSpeed.z).magnitude,
                Is.EqualTo(20f).Within(0.001f), "air crouch preserves slide momentum");

            Vector3 boost = PlayerPhysics.CrouchBoost(Vector3.forward);
            Assert.That(boost.y, Is.EqualTo(1.2f).Within(0.001f));
            Assert.That(boost.z, Is.EqualTo(0.9f).Within(0.001f));

            const float step = 1f / 60f;
            int ticks = 0;
            bool available = false;
            Assert.That(PlayerPhysics.AdvanceCrouchBoost(true, false, false, step, ref ticks, ref available), Is.False);
            Assert.That(available, Is.True);
            Assert.That(PlayerPhysics.AdvanceCrouchBoost(false, false, true, step, ref ticks, ref available), Is.True);
            Assert.That(PlayerPhysics.AdvanceCrouchBoost(false, false, true, step, ref ticks, ref available), Is.False, "one jump permits only one boost");
        }

        [Test]
        public void SlideAllowsSprintReleaseGraceAndStopsAtLevelOrUphillLimits()
        {
            const float fixedStep = 1f / 60f;
            int graceTicks = 0;
            int levelTicks = 0;
            bool wasDownhill = false;

            PlayerPhysics.AdvanceSlideState(false, true, false, false, false, false, true,
                PlayerPhysics.SprintSpeed, false, false, fixedStep, ref graceTicks, ref levelTicks,
                ref wasDownhill);
            for (int tick = 0; tick < 5; tick++)
                PlayerPhysics.AdvanceSlideState(false, true, false, false, false, false, false,
                    PlayerPhysics.SprintSpeed, false, false, fixedStep, ref graceTicks, ref levelTicks,
                    ref wasDownhill);
            Assert.That(PlayerPhysics.AdvanceSlideState(false, true, false, false, true, true, false,
                PlayerPhysics.SprintSpeed, false, false, fixedStep, ref graceTicks, ref levelTicks,
                ref wasDownhill), Is.True, "crouch may follow sprint release within the grace window");

            for (int tick = 0; tick < 44; tick++)
                Assert.That(PlayerPhysics.AdvanceSlideState(true, true, false, false, true, false, false,
                    PlayerPhysics.SprintSpeed, false, false, fixedStep, ref graceTicks, ref levelTicks,
                    ref wasDownhill), Is.True);
            Assert.That(PlayerPhysics.AdvanceSlideState(true, true, false, false, true, false, false,
                PlayerPhysics.SprintSpeed, false, false, fixedStep, ref graceTicks, ref levelTicks,
                ref wasDownhill), Is.False, "level-ground sliding ends after 0.75 seconds");

            Vector3 slopeNormal = Quaternion.AngleAxis(20f, Vector3.right) * Vector3.up;
            Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, slopeNormal).normalized;
            PlayerPhysics.ClassifySlideDirection(downhill * PlayerPhysics.SprintSpeed, slopeNormal,
                out bool movingDownhill, out bool movingUphill);
            Assert.That(movingDownhill, Is.True);
            Assert.That(movingUphill, Is.False);

            graceTicks = 0;
            levelTicks = 0;
            wasDownhill = false;
            bool sliding = PlayerPhysics.AdvanceSlideState(false, true, false, false, true, true, true,
                PlayerPhysics.SprintSpeed, true, false, fixedStep, ref graceTicks, ref levelTicks,
                ref wasDownhill);
            for (int tick = 0; tick < 180; tick++)
                sliding = PlayerPhysics.AdvanceSlideState(sliding, true, false, false, true, false, false,
                    PlayerPhysics.SprintSpeed, true, false, fixedStep, ref graceTicks, ref levelTicks,
                    ref wasDownhill);
            Assert.That(sliding, Is.True, "a downhill ski is not limited by the level-ground timer");
            Assert.That(PlayerPhysics.AdvanceSlideState(sliding, true, false, false, true, false, false,
                PlayerPhysics.SprintSpeed, false, false, fixedStep, ref graceTicks, ref levelTicks,
                ref wasDownhill), Is.False, "a downhill ski ends as soon as it reaches level ground");

            graceTicks = 0;
            levelTicks = 0;
            wasDownhill = false;
            Assert.That(PlayerPhysics.AdvanceSlideState(false, true, false, false, true, true, true,
                PlayerPhysics.SprintSpeed, false, true, fixedStep, ref graceTicks, ref levelTicks,
                ref wasDownhill), Is.False, "a slide cannot begin while moving uphill");
        }

        [Test]
        public void LookSettingsClampAndApplyToMouseAndControllerInputs()
        {
            float previousMouse = PlayerLookSettings.MouseSensitivity;
            float previousController = PlayerLookSettings.ControllerSensitivity;
            try
            {
                PlayerLookSettings.SetMouseSensitivity(-10f);
                PlayerLookSettings.SetControllerSensitivity(1000f);
                Assert.That(PlayerLookSettings.MouseSensitivity,
                    Is.EqualTo(PlayerLookSettings.MinimumMouseSensitivity));
                Assert.That(PlayerLookSettings.ControllerSensitivity,
                    Is.EqualTo(PlayerLookSettings.MaximumControllerSensitivity));

                PlayerLookSettings.SetMouseSensitivity(0.25f);
                PlayerLookSettings.SetControllerSensitivity(300f);
                Assert.That(PlayerPhysics.CalculateLookDelta(Vector2.one, false,
                    PlayerLookSettings.MouseSensitivity, PlayerLookSettings.ControllerSensitivity, 1f).x,
                    Is.EqualTo(0.25f).Within(0.001f));
                Assert.That(PlayerPhysics.CalculateLookDelta(Vector2.one, true,
                    PlayerLookSettings.MouseSensitivity, PlayerLookSettings.ControllerSensitivity, 1f).x,
                    Is.EqualTo(300f).Within(0.001f));
            }
            finally
            {
                PlayerLookSettings.SetMouseSensitivity(previousMouse);
                PlayerLookSettings.SetControllerSensitivity(previousController);
                PlayerLookSettings.Save();
            }
        }

        [Test]
        public void CrouchShortensCapsuleWithoutMovingItsFeet()
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            try
            {
                CapsuleCollider capsule = player.GetComponent<CapsuleCollider>();
                float standingHeight = capsule.height;
                Vector3 standingCenter = capsule.center;
                float standingBottom = standingCenter.y - standingHeight * 0.5f;

                PlayerPhysics.SetCrouched(capsule, true, standingHeight, standingCenter);
                Assert.That(capsule.height, Is.LessThan(standingHeight));
                Assert.That(capsule.center.y - capsule.height * 0.5f, Is.EqualTo(standingBottom).Within(0.001f));

                PlayerPhysics.SetCrouched(capsule, false, standingHeight, standingCenter);
                Assert.That(capsule.height, Is.EqualTo(standingHeight));
                Assert.That(capsule.center, Is.EqualTo(standingCenter));
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void LargeBoulderValidationUsesItsSurfaceInsteadOfItsCenter()
        {
            GameObject boulder = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            try
            {
                boulder.transform.localScale = Vector3.one * 5f;
                boulder.transform.position = Vector3.forward * 3f;
                Rigidbody body = boulder.AddComponent<Rigidbody>();
                Physics.SyncTransforms();

                System.Type motorType = typeof(PlayerInteraction).Assembly.GetType("PushUp.Gameplay.PlayerMotor");
                MethodInfo closestSurface = motorType?.GetMethod("TryGetClosestSurfacePoint", BindingFlags.Static | BindingFlags.Public);
                Assert.That(closestSurface, Is.Not.Null);
                object[] arguments = { body, Vector3.zero, null };
                Assert.That((bool)closestSurface.Invoke(null, arguments), Is.True);
                Vector3 surface = (Vector3)arguments[2];
                Assert.That(Vector3.Distance(Vector3.zero, surface), Is.LessThan(1f));
                Assert.That(Vector3.Distance(Vector3.zero, body.worldCenterOfMass), Is.EqualTo(3f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(boulder);
            }
        }

        [Test]
        public void JumpWindowsSupportCoyoteAndBufferWithoutDoubleJump()
        {
            const float step = 1f / 60f;
            int coyote = 0;
            int buffer = 0;

            Assert.That(PlayerPhysics.AdvanceJumpWindows(true, false, step, ref coyote, ref buffer), Is.False);
            Assert.That(PlayerPhysics.AdvanceJumpWindows(false, true, step, ref coyote, ref buffer), Is.True, "coyote jump should take off");
            Assert.That(PlayerPhysics.AdvanceJumpWindows(false, false, step, ref coyote, ref buffer), Is.False, "held/consumed jump cannot double jump");

            coyote = 0;
            buffer = 0;
            Assert.That(PlayerPhysics.AdvanceJumpWindows(false, true, step, ref coyote, ref buffer), Is.False, "air press should buffer");
            Assert.That(PlayerPhysics.AdvanceJumpWindows(true, false, step, ref coyote, ref buffer), Is.True, "landing inside buffer should take off");
        }

        [Test]
        public void HybridMotorUsesDistinctAccelerationBrakingReversalAndAirControl()
        {
            const float step = 0.02f;
            Vector3 accelerated = PlayerPhysics.CalculateLocomotionVelocity(Vector3.zero, Vector3.forward,
                PlayerPhysics.WalkSpeed, true, Vector3.up, Vector3.zero, step);
            Vector3 braked = PlayerPhysics.CalculateLocomotionVelocity(Vector3.forward * 6f, Vector3.zero,
                PlayerPhysics.WalkSpeed, true, Vector3.up, Vector3.zero, step);
            Vector3 reversed = PlayerPhysics.CalculateLocomotionVelocity(Vector3.forward * 6f, Vector3.back,
                PlayerPhysics.WalkSpeed, true, Vector3.up, Vector3.zero, step);
            Vector3 air = PlayerPhysics.CalculateLocomotionVelocity(Vector3.zero, Vector3.forward,
                PlayerPhysics.WalkSpeed, false, Vector3.up, Vector3.zero, step);
            Vector3 external = PlayerPhysics.CalculateLocomotionVelocity(Vector3.forward * 10f, Vector3.forward,
                PlayerPhysics.WalkSpeed, true, Vector3.up, Vector3.zero, step);

            Assert.That(accelerated.z, Is.EqualTo(1.5f).Within(0.001f));
            Assert.That(braked.z, Is.EqualTo(4.2f).Within(0.001f));
            Assert.That(reversed.z, Is.EqualTo(4.08f).Within(0.001f));
            Assert.That(air.z, Is.EqualTo(0.4f).Within(0.001f));
            Assert.That(external.z, Is.EqualTo(10f).Within(0.001f), "external momentum remains intact at the current run cap");
        }

        [Test]
        public void KnockdownPreservesHitMomentumLongerThanTheNormalMotorBrake()
        {
            const float step = 1f / 60f;
            Vector3 current = Vector3.forward * 8f;
            Vector3 normalBrake = PlayerPhysics.CalculateLocomotionVelocity(current, Vector3.zero,
                PlayerPhysics.WalkSpeed, true, Vector3.up, Vector3.zero, step);
            Vector3 ragdollBrake = PlayerPhysics.CalculateKnockdownVelocity(current, true,
                Vector3.up, Vector3.zero, step);

            Assert.That(ragdollBrake.z, Is.GreaterThan(normalBrake.z + 1f),
                "a knockdown must slide from a hit instead of being pinned by normal locomotion braking");
            Assert.That(ragdollBrake.z, Is.LessThan(current.z),
                "ragdoll momentum still decays so players recover predictably");
        }

        [Test]
        public void RunAndSprintPreserveSprintScalingOnTheFasterBaseline()
        {
            Assert.That(PlayerPhysics.WalkSpeed, Is.EqualTo(10f).Within(0.001f));
            Assert.That(PlayerPhysics.SprintSpeed, Is.EqualTo(15f).Within(0.001f));
            Assert.That(PlayerPhysics.SprintSpeed, Is.EqualTo(PlayerPhysics.WalkSpeed * 1.5f).Within(0.001f));
            Assert.That(PlayerPhysics.MoveAcceleration, Is.EqualTo(75f).Within(0.001f));
            Assert.That(PlayerPhysics.AirAcceleration, Is.EqualTo(20f).Within(0.001f));
        }

        [Test]
        public void JumpArcSupportsFullAndShortHopWithFasterFall()
        {
            const float step = 0.002f;
            float fullHeight = SimulateJumpHeight(true, step);
            float shortHeight = SimulateJumpHeight(false, step);

            Assert.That(PlayerPhysics.JumpVelocity, Is.EqualTo(12f).Within(0.01f));
            Assert.That(fullHeight, Is.EqualTo(3f).Within(0.04f));
            Assert.That(shortHeight, Is.LessThan(fullHeight - 0.7f));
            Assert.That(PlayerPhysics.FallingGravity, Is.GreaterThan(PlayerPhysics.RisingGravity));
        }

        [Test]
        public void BoulderTopRequiresAnIntentionalDescendingLanding()
        {
            Vector3 validTop = new(0f, 0.8f, 0.6f);
            Assert.That(PlayerPhysics.IsValidBoulderLanding(validTop, -1f, true), Is.True);
            Assert.That(PlayerPhysics.IsValidBoulderLanding(validTop, 1f, true), Is.False);
            Assert.That(PlayerPhysics.IsValidBoulderLanding(validTop, -1f, false), Is.False);
            Assert.That(PlayerPhysics.IsValidBoulderLanding(Vector3.forward, -1f, true), Is.False);
        }

        [Test]
        public void PushLocomotionIsFasterAndStrongerWhileSprinting()
        {
            Assert.That(PlayerPhysics.PushSpeed(false), Is.EqualTo(3.6f));
            Assert.That(PlayerPhysics.PushSpeed(true), Is.EqualTo(5f));
            Assert.That(PlayerPhysics.PushForce(false), Is.EqualTo(650f));
            Assert.That(PlayerPhysics.PushForce(true), Is.EqualTo(1050f));
            Assert.That(PlayerPhysics.PushForce(true) * 2f, Is.GreaterThan(PlayerPhysics.PushForce(true)),
                "authoritative forces from two players add naturally");

            Vector3 climbing = PlayerPhysics.SuppressBoulderClimbVelocity(Vector3.up * 4f, Vector3.zero, true, false);
            Assert.That(climbing.y, Is.EqualTo(0.1f).Within(0.001f));
            Assert.That(PlayerPhysics.SuppressBoulderClimbVelocity(Vector3.up * 4f, Vector3.zero, true, true).y,
                Is.EqualTo(4f), "a real jump is allowed to rise onto the boulder");
        }

        [Test]
        public void BoulderPushStanceUsesCurrentSurfaceAndRadialGroundPlaneDirections()
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            GameObject boulder = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            try
            {
                player.transform.position = new Vector3(0f, 1f, 0f);
                boulder.transform.position = new Vector3(0f, 1f, 1.6f);
                boulder.transform.localScale = Vector3.one * 2f;
                Rigidbody boulderBody = boulder.AddComponent<Rigidbody>();
                boulder.AddComponent<BoulderController>();
                Physics.SyncTransforms();

                Assert.That(PlayerPhysics.TryGetBoulderStanceGeometry(player.GetComponent<CapsuleCollider>(),
                    player.transform, boulderBody, Vector3.up, out BoulderPushStanceGeometry geometry), Is.True);
                Assert.That(geometry.SurfaceGap, Is.EqualTo(PlayerPhysics.BoulderStanceGap).Within(0.015f));
                Assert.That(Vector3.Dot(geometry.Inward, Vector3.forward), Is.GreaterThan(0.99f));
                Assert.That(Vector3.Dot(geometry.Inward, geometry.GroundNormal), Is.Zero.Within(0.001f));
                Assert.That(Vector3.Dot(geometry.Inward, geometry.Tangent), Is.Zero.Within(0.001f));

                Vector3 firstSurface = geometry.SurfacePoint;
                boulder.transform.position += Vector3.right;
                Physics.SyncTransforms();
                Assert.That(PlayerPhysics.TryGetBoulderStanceGeometry(player.GetComponent<CapsuleCollider>(),
                    player.transform, boulderBody, Vector3.up, out geometry), Is.True);
                Assert.That(geometry.SurfacePoint, Is.Not.EqualTo(firstSurface),
                    "the stance must follow the boulder's current surface instead of a stale grab point");
            }
            finally
            {
                Object.DestroyImmediate(player);
                Object.DestroyImmediate(boulder);
            }
        }

        [Test]
        public void BoulderPushStanceSeparatesAlignmentOrbitAndInwardForce()
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            GameObject boulder = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            try
            {
                player.transform.position = new Vector3(0f, 1f, -0.4f);
                boulder.transform.position = new Vector3(0f, 1f, 1.6f);
                boulder.transform.localScale = Vector3.one * 2f;
                Rigidbody boulderBody = boulder.AddComponent<Rigidbody>();
                boulder.AddComponent<BoulderController>();
                Physics.SyncTransforms();
                Assert.That(PlayerPhysics.TryGetBoulderStanceGeometry(player.GetComponent<CapsuleCollider>(),
                    player.transform, boulderBody, Vector3.up, out BoulderPushStanceGeometry geometry), Is.True);

                const float step = 0.02f;
                Vector3 alignment = PlayerPhysics.CalculateBoulderStanceVelocity(Vector3.zero, geometry,
                    Vector2.zero, false, Vector3.zero, step);
                Assert.That(Vector3.Dot(alignment, geometry.Inward),
                    Is.EqualTo(PlayerPhysics.BoulderStanceAlignmentAcceleration * step).Within(0.001f));
                Assert.That(Vector3.Dot(alignment, geometry.Inward),
                    Is.LessThanOrEqualTo(PlayerPhysics.BoulderStanceAlignmentSpeed));

                Vector3 forward = PlayerPhysics.CalculateBoulderStanceVelocity(Vector3.zero, geometry,
                    Vector2.up, false, Vector3.zero, step);
                Vector3 strafe = PlayerPhysics.CalculateBoulderStanceVelocity(Vector3.zero, geometry,
                    Vector2.right, false, Vector3.zero, step);
                Assert.That(Vector3.Dot(forward, geometry.Inward), Is.GreaterThan(0f));
                Assert.That(Vector3.Dot(forward, geometry.Tangent), Is.Zero.Within(0.001f));
                Assert.That(Vector3.Dot(strafe, geometry.Tangent), Is.GreaterThan(0f));

                Vector3 walkingForce = PlayerPhysics.CalculateBoulderPushForce(geometry, 1f, false);
                Vector3 sprintForce = PlayerPhysics.CalculateBoulderPushForce(geometry, 1f, true);
                Assert.That(walkingForce.magnitude, Is.EqualTo(650f).Within(0.001f));
                Assert.That(sprintForce.magnitude, Is.EqualTo(1050f).Within(0.001f));
                Assert.That(Vector3.Dot(walkingForce.normalized, geometry.Inward), Is.GreaterThan(0.999f));
                Assert.That(Vector3.Dot(walkingForce, geometry.Tangent), Is.Zero.Within(0.001f));
                Assert.That(Vector3.Dot(walkingForce, geometry.GroundNormal), Is.Zero.Within(0.001f));
                Assert.That(PlayerPhysics.CalculateBoulderPushForce(geometry, 0f, false), Is.EqualTo(Vector3.zero),
                    "the original forward push calculation stays inactive at neutral input");
                Assert.That(PlayerPhysics.CalculateBoulderPushForce(geometry, -1f, false), Is.EqualTo(Vector3.zero),
                    "backward input must never pull the boulder");
                boulderBody.linearVelocity = geometry.Inward * PlayerPhysics.PushWalkSpeed;
                Assert.That(PlayerPhysics.CalculateBoulderPushForce(geometry, 1f, false), Is.EqualTo(Vector3.zero),
                    "walking force should taper out before the boulder outruns the walking stance");
                Assert.That(PlayerPhysics.CalculateBoulderPushForce(geometry, 1f, true).magnitude, Is.GreaterThan(0f),
                    "sprinting should still add force above the walking push speed");
            }
            finally
            {
                Object.DestroyImmediate(player);
                Object.DestroyImmediate(boulder);
            }
        }

        [Test]
        public void IdleBoulderStanceBrakesWithoutChangingForwardPushOrStrafeOrbit()
        {
            GameObject boulder = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            try
            {
                Rigidbody body = boulder.AddComponent<Rigidbody>();
                BoulderController controller = boulder.AddComponent<BoulderController>();
                body.linearVelocity = new Vector3(3f, 2f, 0f);
                body.angularVelocity = new Vector3(0f, 1f, 4f);
                BoulderPushStanceGeometry geometry = new(controller, body, Vector3.zero, Vector3.up,
                    Vector3.back, Vector3.right, PlayerPhysics.BoulderStanceGap);

                Vector3 brake = PlayerPhysics.CalculateBoulderHoldForce(geometry, Vector2.zero, false);
                Assert.That(Vector3.Dot(brake, Vector3.ProjectOnPlane(body.linearVelocity, Vector3.up)),
                    Is.LessThan(0f), "idle RMB must oppose the boulder's ground-plane velocity");
                Assert.That(brake.y, Is.Zero.Within(0.001f));
                Assert.That(brake.magnitude, Is.LessThanOrEqualTo(PlayerPhysics.BoulderBrakeForce + 0.001f));

                Vector3 torque = PlayerPhysics.CalculateBoulderHoldTorque(geometry, Vector2.zero);
                Assert.That(Vector3.Dot(torque, Vector3.ProjectOnPlane(body.angularVelocity, Vector3.up)),
                    Is.LessThan(0f), "holding also resists rolling rather than only sliding");
                Assert.That(torque.magnitude, Is.LessThanOrEqualTo(PlayerPhysics.BoulderBrakeTorque + 0.001f));

                Vector3 backwardBrake = PlayerPhysics.CalculateBoulderHoldForce(geometry, Vector2.down, false);
                Assert.That(backwardBrake.magnitude,
                    Is.EqualTo(brake.magnitude * PlayerPhysics.BoulderBackwardBrakeMultiplier).Within(0.001f),
                    "holding S must increase braking instead of pulling the boulder or releasing the stance");
                Vector3 backwardTorque = PlayerPhysics.CalculateBoulderHoldTorque(geometry, Vector2.down);
                Assert.That(backwardTorque.magnitude,
                    Is.EqualTo(torque.magnitude * PlayerPhysics.BoulderBackwardBrakeMultiplier).Within(0.001f));
                Assert.That(PlayerPhysics.ShouldExitBoulderStance(Vector2.down, false), Is.False);
                Assert.That(PlayerPhysics.ShouldExitBoulderStance(Vector2.zero, true), Is.True);

                Vector3 backwardPlayerVelocity = PlayerPhysics.CalculateBoulderStanceVelocity(
                    Vector3.zero, geometry, Vector2.down, false, Vector3.zero, 0.02f);
                Assert.That(Vector3.Dot(backwardPlayerVelocity, geometry.Outward),
                    Is.LessThanOrEqualTo(0.001f), "S cannot walk the player backward out of the stance");

                Vector3 forward = PlayerPhysics.CalculateBoulderHoldForce(geometry, Vector2.up, false);
                Assert.That(forward, Is.EqualTo(PlayerPhysics.CalculateBoulderPushForce(geometry, 1f, false)),
                    "forward stance input must retain the existing push calculation exactly");
                Assert.That(PlayerPhysics.CalculateBoulderHoldForce(geometry, Vector2.right, false),
                    Is.EqualTo(Vector3.zero), "A/D keeps orbiting without deliberately steering or braking");
                Assert.That(PlayerPhysics.CalculateBoulderHoldTorque(geometry, Vector2.right),
                    Is.EqualTo(Vector3.zero));
            }
            finally
            {
                Object.DestroyImmediate(boulder);
            }
        }

        [Test]
        public void ForwardPushAtTheTargetGapDoesNotDriveTheCapsuleIntoTheBoulder()
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            GameObject boulder = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            try
            {
                player.transform.position = new Vector3(0f, 1f, 0f);
                boulder.transform.position = new Vector3(0f, 1f, 1.6f);
                boulder.transform.localScale = Vector3.one * 2f;
                Rigidbody boulderBody = boulder.AddComponent<Rigidbody>();
                boulder.AddComponent<BoulderController>();
                Physics.SyncTransforms();
                Assert.That(PlayerPhysics.TryGetBoulderStanceGeometry(player.GetComponent<CapsuleCollider>(),
                    player.transform, boulderBody, Vector3.up, out BoulderPushStanceGeometry geometry), Is.True);
                Assert.That(geometry.SurfaceGap, Is.EqualTo(PlayerPhysics.BoulderStanceGap).Within(0.015f));

                Vector3 velocity = PlayerPhysics.CalculateBoulderStanceVelocity(Vector3.zero, geometry,
                    Vector2.up, false, Vector3.zero, 0.02f);
                Assert.That(Vector3.ProjectOnPlane(velocity, Vector3.up).magnitude, Is.LessThan(0.001f),
                    "W should apply boulder force, not repeatedly ram the player collider into the boulder");
                Assert.That(PlayerPhysics.CalculateBoulderPushForce(geometry, 1f, false).magnitude,
                    Is.EqualTo(PlayerPhysics.PushWalkForce).Within(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(player);
                Object.DestroyImmediate(boulder);
            }
        }

        [Test]
        public void OfflinePushFollowsTheMovingBoulderWithoutContactOscillation()
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            GameObject boulder = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            SimulationMode previousMode = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                floor.transform.SetPositionAndRotation(new Vector3(0f, -0.5f, 0f), Quaternion.identity);
                floor.transform.localScale = new Vector3(20f, 1f, 20f);
                player.transform.position = new Vector3(0f, 1f, 0f);
                boulder.transform.position = new Vector3(0f, 1f, 1.6f);
                boulder.transform.localScale = Vector3.one * 2f;
                Rigidbody playerBody = player.AddComponent<Rigidbody>();
                Rigidbody boulderBody = boulder.AddComponent<Rigidbody>();
                PhysicsMaterial movementMaterial = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(
                    "Assets/PushUp/Materials/PlayerMovement.physicMaterial");
                PlayerPhysics.ConfigureBody(playerBody, player.GetComponent<CapsuleCollider>(), movementMaterial);
                boulder.AddComponent<BoulderController>();
                BoulderController.ConfigureBody(boulderBody, boulder.GetComponent<Collider>());
                Physics.SyncTransforms();

                float start = boulderBody.position.z;
                float minimumGap = float.PositiveInfinity;
                float maximumGap = 0f;
                for (int index = 0; index < 50; index++)
                {
                    Assert.That(PlayerPhysics.TryGetBoulderStanceGeometry(player.GetComponent<CapsuleCollider>(),
                        player.transform, boulderBody, Vector3.up, out BoulderPushStanceGeometry geometry), Is.True);
                    playerBody.linearVelocity = PlayerPhysics.CalculateBoulderStanceVelocity(
                        playerBody.linearVelocity, geometry, Vector2.up, false, Vector3.zero, 0.02f);
                    boulderBody.AddForceAtPosition(PlayerPhysics.CalculateBoulderPushForce(geometry, 1f, false),
                        geometry.SurfacePoint, ForceMode.Force);
                    Physics.Simulate(0.02f);

                    Assert.That(PlayerPhysics.TryGetBoulderStanceGeometry(player.GetComponent<CapsuleCollider>(),
                        player.transform, boulderBody, Vector3.up, out geometry), Is.True);
                    minimumGap = Mathf.Min(minimumGap, geometry.SurfaceGap);
                    maximumGap = Mathf.Max(maximumGap, geometry.SurfaceGap);
                }

                Assert.That(boulderBody.position.z - start, Is.GreaterThan(0.3f));
                Assert.That(minimumGap, Is.GreaterThan(0.035f), "the capsule should not enter a contact-solver loop");
                Assert.That(maximumGap, Is.LessThan(0.28f), "surface following should keep the stance connected");
                Assert.That(maximumGap - minimumGap, Is.LessThan(0.16f));
            }
            finally
            {
                Physics.simulationMode = previousMode;
                Object.DestroyImmediate(floor);
                Object.DestroyImmediate(player);
                Object.DestroyImmediate(boulder);
            }
        }

        [Test]
        public void BoulderPushStanceExitsOnlyOnJumpNotBackForwardOrOrbit()
        {
            Assert.That(PlayerPhysics.ShouldExitBoulderStance(new Vector2(0f, -0.3f), false), Is.False,
                "backward input is a hard brake and must retain the stance");
            Assert.That(PlayerPhysics.ShouldExitBoulderStance(Vector2.zero, true), Is.True);
            Assert.That(PlayerPhysics.ShouldExitBoulderStance(Vector2.up, false), Is.False);
            Assert.That(PlayerPhysics.ShouldExitBoulderStance(Vector2.right, false), Is.False);
            Assert.That(PlayerInteraction.CanStartGrab(true, true, true), Is.False,
                "jump-exiting the stance latches RMB until it is physically released");
        }

        [Test]
        public void BoulderFacingUsesPhysicsSizedYawWithoutTiltingTheCapsule()
        {
            Quaternion next = PlayerPhysics.CalculateBoulderFacingRotation(
                Quaternion.identity, Vector3.right + Vector3.up, 0.02f);
            Assert.That(Quaternion.Angle(Quaternion.identity, next),
                Is.EqualTo(PlayerPhysics.BoulderStanceYawSpeed * 0.02f).Within(0.01f));
            Assert.That(Vector3.Angle(next * Vector3.up, Vector3.up), Is.LessThan(0.01f),
                "stance-facing must not tilt the physics capsule to match a slope normal");
        }

        [Test]
        public void CameraPresentationSmoothsStrafeCorrectionsWithoutBecomingFloaty()
        {
            Vector3 target = Vector3.right * 0.5f;
            Vector3 next = PlayerPhysics.CalculateCameraPresentationPosition(Vector3.zero, target, 1f / 135f);
            Assert.That(next.x, Is.GreaterThan(0f));
            Assert.That(next.x, Is.LessThanOrEqualTo(target.x));
            Assert.That(Vector3.Distance(next, target), Is.LessThanOrEqualTo(PlayerPhysics.CameraMaximumLag + 0.001f));
            Assert.That(PlayerPhysics.CalculateCameraPresentationPosition(Vector3.zero, Vector3.right * 2f,
                1f / 135f), Is.EqualTo(Vector3.right * 2f), "large teleports snap instead of dragging the camera");
        }

        [Test]
        public void CameraLookSmoothingIsTightAndControllerLookIsFrameRateIndependent()
        {
            Vector2 mouse = PlayerPhysics.CalculateLookDelta(new Vector2(10f, -5f), false,
                0.12f, PlayerPhysics.ControllerLookSpeed, 1f / 30f);
            Assert.That(mouse.x, Is.EqualTo(1.2f).Within(0.0001f));
            Assert.That(mouse.y, Is.EqualTo(-0.6f).Within(0.0001f));

            Vector2 controllerAtThirty = PlayerPhysics.CalculateLookDelta(Vector2.one, true,
                0.12f, PlayerPhysics.ControllerLookSpeed, 1f / 30f);
            Vector2 controllerAtSixty = PlayerPhysics.CalculateLookDelta(Vector2.one, true,
                0.12f, PlayerPhysics.ControllerLookSpeed, 1f / 60f);
            Assert.That(controllerAtThirty.x, Is.EqualTo(controllerAtSixty.x * 2f).Within(0.0001f));
            Assert.That(controllerAtThirty.y, Is.EqualTo(controllerAtSixty.y * 2f).Within(0.0001f));

            Quaternion current = Quaternion.identity;
            Quaternion target = Quaternion.Euler(-12f, 18f, 0f);
            Quaternion presented = PlayerPhysics.CalculateCameraPresentationRotation(current, target, 1f / 60f);
            Assert.That(Quaternion.Angle(presented, target), Is.LessThanOrEqualTo(
                PlayerPhysics.CameraMaximumAngularLag + 0.001f));
            Assert.That(Quaternion.Angle(current, presented), Is.GreaterThan(0f));
        }

        [Test]
        public void AutoStepAcceptsOnlyLowStaticTerrain()
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            GameObject obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                player.transform.position = Vector3.up;
                obstacle.transform.position = new Vector3(0f, 0.14f, 0.75f);
                obstacle.transform.localScale = new Vector3(2f, 0.28f, 0.4f);
                Physics.SyncTransforms();
                Assert.That(PlayerPhysics.TryFindStep(player.GetComponent<CapsuleCollider>(), player.transform,
                    Vector3.forward, out Vector3 correction), Is.True);
                Assert.That(correction.y, Is.LessThanOrEqualTo(PlayerPhysics.StepHeight + 0.01f));

                Rigidbody dynamicBody = obstacle.AddComponent<Rigidbody>();
                dynamicBody.isKinematic = true;
                Physics.SyncTransforms();
                Assert.That(PlayerPhysics.TryFindStep(player.GetComponent<CapsuleCollider>(), player.transform,
                    Vector3.forward, out _), Is.False, "dynamic props and boulders never qualify for auto-step");

                Object.DestroyImmediate(dynamicBody);
                obstacle.transform.position = new Vector3(0f, 0.175f, 0.75f);
                obstacle.transform.localScale = new Vector3(2f, 0.35f, 0.4f);
                Physics.SyncTransforms();
                Assert.That(PlayerPhysics.TryFindStep(player.GetComponent<CapsuleCollider>(), player.transform,
                    Vector3.forward, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(player);
                Object.DestroyImmediate(obstacle);
            }
        }

        [Test]
        public void TunedPlayerCanPushTunedBoulderOnLevelGround()
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            GameObject boulder = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            SimulationMode previousMode = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                floor.transform.SetPositionAndRotation(new Vector3(0f, -0.5f, 0f), Quaternion.identity);
                floor.transform.localScale = new Vector3(20f, 1f, 20f);
                player.transform.position = new Vector3(0f, 1.01f, 0f);
                boulder.transform.position = new Vector3(0f, 1.18f, 1.9f);
                boulder.transform.localScale = Vector3.one * 2.35f;
                Rigidbody playerBody = player.AddComponent<Rigidbody>();
                Rigidbody boulderBody = boulder.AddComponent<Rigidbody>();
                PlayerPhysics.ConfigureBody(playerBody);
                BoulderController.ConfigureBody(boulderBody, boulder.GetComponent<Collider>());
                Physics.SyncTransforms();
                float start = boulder.transform.position.z;

                for (int index = 0; index < 150; index++)
                {
                    Vector3 horizontal = Vector3.ProjectOnPlane(playerBody.linearVelocity, Vector3.up);
                    playerBody.AddForce((Vector3.forward * PlayerPhysics.MaxSpeed - horizontal) * PlayerPhysics.MoveAcceleration, ForceMode.Acceleration);
                    Physics.Simulate(0.02f);
                }

                Assert.That(boulder.transform.position.z - start, Is.GreaterThan(0.2f));
            }
            finally
            {
                Physics.simulationMode = previousMode;
                Object.DestroyImmediate(floor);
                Object.DestroyImmediate(player);
                Object.DestroyImmediate(boulder);
            }
        }

        [Test]
        public void PunchMakesHeavyBoulderReactWithoutLaunchingIt()
        {
            GameObject boulder = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            SimulationMode previousMode = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                Rigidbody body = boulder.AddComponent<Rigidbody>();
                body.useGravity = false;
                BoulderController.ConfigureBody(body, boulder.GetComponent<Collider>());
                body.AddForce(PlayerInteraction.CalculatePunchImpulse(Vector3.forward), ForceMode.Impulse);
                Physics.Simulate(0.02f);
                Assert.That(body.linearVelocity.z, Is.GreaterThan(1.2f));
                Assert.That(body.linearVelocity.z, Is.LessThan(1.5f));
            }
            finally
            {
                Physics.simulationMode = previousMode;
                Object.DestroyImmediate(boulder);
            }
        }

        [Test]
        public void DynamicGrabSpringReducesSeparation()
        {
            GameObject target = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            SimulationMode previousMode = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                target.transform.position = Vector3.right * 3f;
                Rigidbody body = target.AddComponent<Rigidbody>();
                body.useGravity = false;
                body.mass = 150f;
                float startDistance = target.transform.position.magnitude;

                for (int index = 0; index < 50; index++)
                {
                    Vector3 force = PlayerInteraction.CalculateGrabForce(-target.transform.position, body.linearVelocity, 650f, 40f, 1100f);
                    body.AddForce(force, ForceMode.Force);
                    Physics.Simulate(0.02f);
                }

                Assert.That(target.transform.position.magnitude, Is.LessThan(startDistance - 0.5f));
            }
            finally
            {
                Physics.simulationMode = previousMode;
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void PhysicalPlayerTransitionsThroughKnockdownAndRestoresLocomotionSettings()
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            PhysicsMaterial movementMaterial = new("Player movement test material");
            PhysicsMaterial physicalMaterial = new("Player physical test material");
            try
            {
                Rigidbody body = player.AddComponent<Rigidbody>();
                CapsuleCollider capsule = player.GetComponent<CapsuleCollider>();
                body.mass = 78f;
                body.useGravity = false;
                body.constraints = RigidbodyConstraints.FreezeRotation;
                body.linearDamping = 0.04f;
                body.angularDamping = 0.1f;
                capsule.sharedMaterial = movementMaterial;

                ActiveRagdollPuppet puppet = player.AddComponent<ActiveRagdollPuppet>();
                PlayerActorPhysics actor = player.AddComponent<PlayerActorPhysics>();
                actor.Configure(puppet, physicalMaterial);
                actor.SetExternalSimulation(true);

                const float desiredYaw = 37f;
                Assert.That(actor.TryApplyImpact(Vector3.forward * 200f,
                    body.worldCenterOfMass + Vector3.up * 0.65f, desiredYaw), Is.True);
                Assert.That(actor.ActorState, Is.EqualTo(PlayerActorState.Staggered));
                Assert.That(actor.IsMovementLocked, Is.True);
                Assert.That(body.constraints, Is.EqualTo(RigidbodyConstraints.None));
                Assert.That(body.useGravity, Is.True);
                Assert.That(capsule.sharedMaterial, Is.SameAs(physicalMaterial));

                float impactTime = Time.time;
                body.rotation = Quaternion.Euler(31f, desiredYaw, 0f);
                body.angularVelocity = Vector3.zero;
                actor.Simulate(0.02f, impactTime + 0.02f);
                Assert.That(actor.ActorState, Is.EqualTo(PlayerActorState.KnockedDown));

                actor.Simulate(0.02f, impactTime + TrainingDummy.GetUpDelay + 0.02f);
                Assert.That(actor.ActorState, Is.EqualTo(PlayerActorState.Recovering));

                body.rotation = Quaternion.identity;
                body.angularVelocity = Vector3.zero;
                actor.Simulate(0.02f, impactTime + TrainingDummy.GetUpDelay +
                    PlayerActorPhysics.MinimumStaggerDuration + 0.04f);

                Assert.That(actor.ActorState, Is.EqualTo(PlayerActorState.Locomotion));
                Assert.That(actor.IsMovementLocked, Is.False);
                Assert.That(body.useGravity, Is.False);
                Assert.That(body.constraints, Is.EqualTo(RigidbodyConstraints.FreezeRotation));
                Assert.That(capsule.sharedMaterial, Is.SameAs(movementMaterial));
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(body.rotation.eulerAngles.y, desiredYaw)), Is.LessThan(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(player);
                Object.DestroyImmediate(movementMaterial);
                Object.DestroyImmediate(physicalMaterial);
            }
        }

        [Test]
        public void FighterSizedImpactTranslatesAndTipsThePhysicalPlayerRoot()
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            SimulationMode previousMode = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                player.transform.position = Vector3.up * 10f;
                Rigidbody body = player.AddComponent<Rigidbody>();
                body.mass = 78f;
                body.useGravity = false;
                body.constraints = RigidbodyConstraints.FreezeRotation;
                PlayerActorPhysics actor = player.AddComponent<PlayerActorPhysics>();
                actor.Configure(null);
                actor.SetExternalSimulation(true);
                Physics.SyncTransforms();

                Vector3 contact = body.worldCenterOfMass + Vector3.up * 0.65f;
                Assert.That(actor.TryApplyImpact(Vector3.forward * AttackDummy.DefaultPunchImpulse, contact), Is.True);
                Physics.Simulate(0.02f);
                Assert.That(body.linearVelocity.z, Is.GreaterThan(6f),
                    "a 520 N-s hit must visibly translate a 78 kg player");
                Assert.That(body.angularVelocity.magnitude, Is.GreaterThan(0.1f),
                    "the upper-body contact point must create a real tipping moment");
                Assert.That(Vector3.Angle(body.rotation * Vector3.up, Vector3.up), Is.GreaterThan(0.1f));
            }
            finally
            {
                Physics.simulationMode = previousMode;
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void PhysicalPlayerCameraReactionClampsRootPitchAndRoll()
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            try
            {
                Rigidbody body = player.AddComponent<Rigidbody>();
                body.mass = 78f;
                body.useGravity = false;
                body.constraints = RigidbodyConstraints.FreezeRotation;
                PlayerActorPhysics actor = player.AddComponent<PlayerActorPhysics>();
                actor.Configure(null);
                actor.SetExternalSimulation(true);
                actor.TryApplyImpact(Vector3.forward * 200f, body.worldCenterOfMass + Vector3.up * 0.65f, 0f);
                body.rotation = Quaternion.Euler(120f, 0f, 100f);

                Vector3 reaction = actor.CameraReactionRotation.eulerAngles;
                float pitch = Mathf.Abs(Mathf.DeltaAngle(0f, reaction.x));
                float roll = Mathf.Abs(Mathf.DeltaAngle(0f, reaction.z));
                Assert.That(pitch, Is.LessThanOrEqualTo(PlayerActorPhysics.MaximumCameraPitchReaction + 0.01f));
                Assert.That(roll, Is.LessThanOrEqualTo(PlayerActorPhysics.MaximumCameraRollReaction + 0.01f));
                Assert.That(pitch, Is.GreaterThan(30f), "a fall should remain visibly disorienting");
                Assert.That(roll, Is.GreaterThan(40f), "a fall should retain bounded physical roll");
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void PhysicalWorldArmConfigurationIsIdempotentAndUsesDummyJointDrives()
        {
            GameObject player = new("Physical arm test player");
            try
            {
                CapsuleCollider rootCollider = player.AddComponent<CapsuleCollider>();
                Rigidbody rootBody = player.AddComponent<Rigidbody>();
                Transform worldRoot = new GameObject("World Rig").transform;
                worldRoot.SetParent(player.transform, false);
                Transform torso = new GameObject("Torso").transform;
                torso.SetParent(worldRoot, false);
                BoxCollider torsoCollider = torso.gameObject.AddComponent<BoxCollider>();
                Transform leftArm = GameObject.CreatePrimitive(PrimitiveType.Cube).transform;
                leftArm.name = "Left Arm";
                leftArm.SetParent(torso, false);
                leftArm.localPosition = new Vector3(-0.55f, 0.25f, 0f);
                Transform rightArm = GameObject.CreatePrimitive(PrimitiveType.Cube).transform;
                rightArm.name = "Right Arm";
                rightArm.SetParent(torso, false);
                rightArm.localPosition = new Vector3(0.55f, 0.25f, 0f);

                ActiveRagdollPuppet puppet = player.AddComponent<ActiveRagdollPuppet>();
                puppet.Configure(null, worldRoot, torso, leftArm, rightArm, null, null, null);
                puppet.ConfigurePhysicalWorldArms(rootBody);
                puppet.ConfigurePhysicalWorldArms(rootBody);

                Assert.That(puppet.HasPhysicalWorldArms, Is.True);
                Assert.That(leftArm.GetComponents<Rigidbody>(), Has.Length.EqualTo(1));
                Assert.That(rightArm.GetComponents<Rigidbody>(), Has.Length.EqualTo(1));
                Assert.That(leftArm.GetComponents<ConfigurableJoint>(), Has.Length.EqualTo(1));
                Assert.That(rightArm.GetComponents<ConfigurableJoint>(), Has.Length.EqualTo(1));

                ConfigurableJoint leftJoint = leftArm.GetComponent<ConfigurableJoint>();
                ConfigurableJoint rightJoint = rightArm.GetComponent<ConfigurableJoint>();
                Assert.That(leftJoint.connectedBody, Is.SameAs(rootBody));
                Assert.That(rightJoint.connectedBody, Is.SameAs(rootBody));
                Assert.That(leftJoint.slerpDrive.positionSpring, Is.EqualTo(TrainingDummy.ArmPoseSpring));
                Assert.That(leftJoint.slerpDrive.positionDamper, Is.EqualTo(TrainingDummy.ArmPoseDamper));
                Assert.That(leftJoint.slerpDrive.maximumForce, Is.EqualTo(TrainingDummy.ArmPoseMaximumForce));
                Assert.That(leftArm.gameObject.layer, Is.EqualTo(GameplayLayers.Presentation));
                Assert.That(rightArm.gameObject.layer, Is.EqualTo(GameplayLayers.Presentation));
                Assert.That((leftArm.GetComponent<Collider>().excludeLayers & (1 << GameplayLayers.Boulder)),
                    Is.Not.Zero);
                Assert.That(torsoCollider.enabled, Is.False);
                Assert.That(rootCollider.enabled, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void PrefabPuppetReconstructsPhysicalArmsAndKeepsHiddenOwnerRigActive()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/PushUp/Prefabs/Player.prefab");
            Assert.That(prefab, Is.Not.Null);
            GameObject player = Object.Instantiate(prefab);
            try
            {
                ActiveRagdollPuppet puppet = player.GetComponent<ActiveRagdollPuppet>();
                Assert.That(puppet, Is.Not.Null);
                FieldInfo configured = typeof(ActiveRagdollPuppet).GetField("_physicalWorldArmsConfigured",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo leftBody = typeof(ActiveRagdollPuppet).GetField("_leftArmBody",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo rightBody = typeof(ActiveRagdollPuppet).GetField("_rightArmBody",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo leftJoint = typeof(ActiveRagdollPuppet).GetField("_leftArmJoint",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo rightJoint = typeof(ActiveRagdollPuppet).GetField("_rightArmJoint",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(configured, Is.Not.Null);
                Assert.That(leftBody?.GetValue(puppet), Is.Not.Null);
                Assert.That(rightBody?.GetValue(puppet), Is.Not.Null);
                Assert.That(leftJoint?.GetValue(puppet), Is.Not.Null);
                Assert.That(rightJoint?.GetValue(puppet), Is.Not.Null);

                // EditMode does not automatically run ordinary MonoBehaviour lifecycle methods.
                // Clear the convenience flag and invoke Awake to exercise prefab reconstruction.
                configured.SetValue(puppet, false);
                MethodInfo awake = typeof(ActiveRagdollPuppet).GetMethod("Awake",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(awake, Is.Not.Null);
                awake.Invoke(puppet, null);
                Assert.That(puppet.HasPhysicalWorldArms, Is.True);

                Transform worldRoot = player.transform.Find("World Rig");
                Assert.That(worldRoot, Is.Not.Null);
                puppet.ConfigureLocalView(true);
                Assert.That(worldRoot.gameObject.activeSelf, Is.True,
                    "owner arm bodies must keep simulating even when their meshes are hidden");
                Renderer[] worldRenderers = worldRoot.GetComponentsInChildren<Renderer>(true);
                Assert.That(worldRenderers, Is.Not.Empty);
                foreach (Renderer worldRenderer in worldRenderers)
                    Assert.That(worldRenderer.enabled, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void PlayerPoseSnapshotIsArmsOnlyAndCannotMutateActorState()
        {
            Assert.That(typeof(PlayerPoseSnapshot).GetField("ActorState"), Is.Null);
            Assert.That(typeof(PlayerPoseSnapshot).GetFields(BindingFlags.Instance | BindingFlags.Public),
                Has.Length.EqualTo(4), "pose packets contain tick/sequence metadata plus only two arm rotations");

            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            try
            {
                player.AddComponent<Rigidbody>();
                ActiveRagdollPuppet puppet = player.AddComponent<ActiveRagdollPuppet>();
                PlayerActorPhysics actor = player.AddComponent<PlayerActorPhysics>();
                actor.Configure(puppet);
                actor.SetSimulationAuthority(false);
                actor.ApplyObservedState(PlayerActorState.KnockedDown, 25f);
                Assert.That(actor.ActorState, Is.EqualTo(PlayerActorState.KnockedDown));

                PlayerPoseSnapshot snapshot = new(Quaternion.Euler(12f, 23f, 34f),
                    Quaternion.Euler(-15f, -26f, -37f));
                puppet.ApplyPoseSnapshot(snapshot);
                Assert.That(actor.ActorState, Is.EqualTo(PlayerActorState.KnockedDown),
                    "arm presentation packets must never drive authoritative actor state");
            }
            finally
            {
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void NetworkRunRequiresAnExplicitStartAfterServerIsReady()
        {
            Assert.That(RunDirector.CanStartNetworkRun(false, false), Is.False);
            Assert.That(RunDirector.CanStartNetworkRun(true, false), Is.True);
            Assert.That(RunDirector.CanStartNetworkRun(true, true), Is.False);
        }

        private static float SimulateJumpHeight(bool held, float step)
        {
            Vector3 velocity = Vector3.up * PlayerPhysics.JumpVelocity;
            float height = 0f;
            while (velocity.y > 0f)
            {
                height += velocity.y * step;
                velocity = PlayerPhysics.ApplyJumpGravity(velocity, false, held, step);
            }
            return height;
        }
    }
}
