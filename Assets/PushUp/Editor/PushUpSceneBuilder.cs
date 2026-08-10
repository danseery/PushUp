using System.Text;
using FishNet.Component.Transforming;
using FishNet.Managing;
using FishNet.Managing.Predicting;
using FishNet.Managing.Timing;
using FishNet.Managing.Transporting;
using FishNet.Object;
using FishNet.Transporting.Tugboat;
using GameKit.Dependencies.Utilities;
using PushUp.Gameplay;
using PushUp.Networking;
using PushUp.Steam;
using PushUp.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PushUp.Editor
{
    /// <summary>Regenerates the deliberately small, inspectable prototype scene and its network prefabs.</summary>
    public static class PushUpSceneBuilder
    {
        private const string Root = "Assets/PushUp";
        private const string Prefabs = Root + "/Prefabs";
        private const string Materials = Root + "/Materials";
        private const string ScenePath = Root + "/Scenes/Mountain.unity";
        private static readonly Vector3 DefaultBoulderScale = Vector3.one * 2.35f;

        [MenuItem("PushUp/Regenerate Prototype Scene")]
        public static void Build()
        {
            EnsureFolder(Root + "/Scenes");
            EnsureFolder(Prefabs);
            EnsureFolder(Prefabs + "/Offline");
            EnsureFolder(Materials);

            Material terrain = GetMaterial("Terrain", new Color(0.28f, 0.35f, 0.31f));
            Material path = GetMaterial("Path", new Color(0.44f, 0.43f, 0.38f));
            Material boulder = GetMaterial("Boulder", new Color(0.30f, 0.28f, 0.25f));
            Material speed = GetMaterial("Speed", new Color(0.18f, 0.68f, 1f));
            Material assist = GetMaterial("Assist", new Color(1f, 0.72f, 0.14f));
            Material anchor = GetMaterial("Anchor", new Color(0.82f, 0.28f, 0.85f));
            Material dummy = GetEditableMaterial("Dummy", new Color(0.72f, 0.18f, 0.16f));
            Material attackDummy = GetEditableMaterial("AttackDummy", new Color(0.95f, 0.38f, 0.08f));
            Material punchImpact = GetPunchImpactMaterial();
            PhysicsMaterial playerMovement = GetPhysicsMaterial("PlayerMovement", 0f, 0f, 0f,
                PhysicsMaterialCombine.Minimum, PhysicsMaterialCombine.Minimum);
            PhysicsMaterial dummyPhysics = GetPhysicsMaterial("DummyPhysics", 0.85f, 1f, 0f,
                PhysicsMaterialCombine.Maximum, PhysicsMaterialCombine.Minimum);
            PhysicsMaterial boulderPhysics = GetPhysicsMaterial("BoulderPhysics", 0.52f, 0.6f, 0f,
                PhysicsMaterialCombine.Average, PhysicsMaterialCombine.Minimum);
            GameObject punchImpactPrefab = BuildPunchImpactPrefab(punchImpact);

            NetworkObject playerPrefab = BuildPlayerPrefab(terrain, punchImpactPrefab, playerMovement, dummyPhysics);
            GameObject offlineDummyPrefab = BuildTrainingDummyOfflinePrefab(playerPrefab.gameObject, dummy, dummyPhysics);
            NetworkObject dummyPrefab = BuildNetworkTrainingDummyPrefab(offlineDummyPrefab);
            NetworkObject attackDummyPrefab = BuildAttackDummyPrefab(offlineDummyPrefab, attackDummy);
            NetworkObject boulderPrefab = BuildBoulderPrefab(boulder,
                ExistingPrefabScale(Prefabs + "/Boulder.prefab", DefaultBoulderScale), boulderPhysics);
            BuildOfflineBoulderPrefab(boulderPrefab.gameObject);
            NetworkObject speedPrefab = BuildPowerupPrefab("SpeedBoost", PowerupKind.Speed, speed);
            NetworkObject assistPrefab = BuildPowerupPrefab("BoulderAssist", PowerupKind.BoulderAssist, assist);
            NetworkObject anchorPrefab = BuildPowerupPrefab("CarryAnchor", PowerupKind.Anchor, anchor);

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateLighting();
            CreateWorld(terrain, path, out Transform[] spawns, out Transform boulderSpawn, out Transform dummySpawn, out Transform[] pickupSpawns, out Transform summit);
            CreateSystems();
            LevelAuthoringTools.MigrateOpenScene(false);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("PushUp scene generated at " + ScenePath);
        }

        /// <summary>
        /// Migrates only the Player prefab. This is intentionally separate from <see cref="Build"/>
        /// so networking/presentation upgrades cannot overwrite a designer-edited Mountain scene.
        /// </summary>
        [MenuItem("PushUp/Upgrade Player Prefab Only")]
        public static void UpgradePlayerPrefabOnly()
        {
            EnsureFolder(Prefabs);
            EnsureFolder(Materials);
            Material playerMaterial = AssetDatabase.LoadAssetAtPath<Material>(Materials + "/Terrain.mat") ??
                                      GetMaterial("Terrain", new Color(0.28f, 0.35f, 0.31f));
            PhysicsMaterial movementMaterial =
                AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(Materials + "/PlayerMovement.physicMaterial") ??
                GetPhysicsMaterial("PlayerMovement", 0f, 0f, 0f,
                    PhysicsMaterialCombine.Minimum, PhysicsMaterialCombine.Minimum);
            PhysicsMaterial physicalMaterial =
                AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(Materials + "/DummyPhysics.physicMaterial") ??
                GetPhysicsMaterial("DummyPhysics", 0.85f, 1f, 0f,
                    PhysicsMaterialCombine.Maximum, PhysicsMaterialCombine.Minimum);
            GameObject impactPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + "/PunchImpact.prefab");
            if (impactPrefab == null)
                impactPrefab = BuildPunchImpactPrefab(GetPunchImpactMaterial());

            BuildPlayerPrefab(playerMaterial, impactPrefab, movementMaterial, physicalMaterial);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("PushUp Player prefab upgraded without modifying the scene.");
        }

        [MenuItem("PushUp/Upgrade Boulder Prefab Only")]
        public static void UpgradeBoulderPrefabOnly()
        {
            EnsureFolder(Prefabs);
            EnsureFolder(Materials);
            Material boulderMaterial = AssetDatabase.LoadAssetAtPath<Material>(Materials + "/Boulder.mat") ??
                                       GetMaterial("Boulder", new Color(0.30f, 0.28f, 0.25f));
            PhysicsMaterial physicsMaterial =
                AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(Materials + "/BoulderPhysics.physicMaterial") ??
                GetPhysicsMaterial("BoulderPhysics", 0.52f, 0.6f, 0f,
                    PhysicsMaterialCombine.Average, PhysicsMaterialCombine.Minimum);
            NetworkObject boulder = BuildBoulderPrefab(boulderMaterial,
                ExistingPrefabScale(Prefabs + "/Boulder.prefab", DefaultBoulderScale), physicsMaterial);
            BuildOfflineBoulderPrefab(boulder.gameObject);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("PushUp Boulder prefab upgraded without modifying the scene.");
        }

        public static void UpgradeNetworkPrefabsOnly()
        {
            UpgradePlayerPrefabOnly();
            UpgradeBoulderPrefabOnly();
        }

        private static void CreateSystems()
        {
            GameObject systems = new GameObject("Game Systems");
            systems.AddComponent<SteamBootstrap>();
            systems.AddComponent<SteamSessionService>();
            systems.AddComponent<Tugboat>();
            systems.AddComponent<SteamSocketsTransport>();
            systems.AddComponent<TransportManager>();
            TimeManager timeManager = systems.AddComponent<TimeManager>();
            Set(timeManager, "_tickRate", 60);
            Set(timeManager, "_maximumFrameTicks", 4);
            Set(timeManager, "_physicsMode", PhysicsMode.TimeManager);
            PredictionManager predictionManager = systems.AddComponent<PredictionManager>();
            Set(predictionManager, "_stateInterpolation", 2);
            systems.AddComponent<SteamLobbyAuthenticator>();
            systems.AddComponent<NetworkManager>();
            systems.AddComponent<TransportSelector>();
            systems.AddComponent<SteamNetworkCoordinator>();
            RunDirector director = systems.AddComponent<RunDirector>();
            systems.AddComponent<LevelSpawnService>();
            SessionFlowController flow = systems.AddComponent<SessionFlowController>();
            Set(flow, "_runDirector", director);
            Set(flow, "_steamSession", systems.GetComponent<SteamSessionService>());
            Set(flow, "_steamCoordinator", systems.GetComponent<SteamNetworkCoordinator>());
            Set(flow, "_transportSelector", systems.GetComponent<TransportSelector>());
            Set(flow, "_networkManager", systems.GetComponent<NetworkManager>());
            PushUpMenu menu = systems.AddComponent<PushUpMenu>();
            Set(menu, "_runDirector", director);
            Set(menu, "_steamSession", systems.GetComponent<SteamSessionService>());
            Set(menu, "_steamCoordinator", systems.GetComponent<SteamNetworkCoordinator>());
            Set(menu, "_transportSelector", systems.GetComponent<TransportSelector>());
            Set(menu, "_networkManager", systems.GetComponent<NetworkManager>());
            Set(menu, "_flow", flow);
        }

        private static NetworkObject BuildPlayerPrefab(Material material, GameObject impactPrefab,
            PhysicsMaterial movementMaterial, PhysicsMaterial physicalMaterial)
        {
            GameObject existingPlayer = AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + "/Player.prefab");
            PlayerMotor existingMotor = existingPlayer != null ? existingPlayer.GetComponent<PlayerMotor>() : null;
            PlayerInteraction existingInteraction = existingPlayer != null
                ? existingPlayer.GetComponent<PlayerInteraction>()
                : null;
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            player.name = "Player";
            GameplayLayers.ApplyRole(player, SpawnRole.Player);
            player.transform.localScale = Vector3.one;
            Renderer bodyRenderer = player.GetComponent<Renderer>();
            bodyRenderer.sharedMaterial = material;
            CapsuleCollider capsule = player.GetComponent<CapsuleCollider>();
            capsule.height = PlayerPhysics.CapsuleHeight;
            capsule.radius = PlayerPhysics.CapsuleRadius;
            capsule.sharedMaterial = movementMaterial;
            Rigidbody body = player.AddComponent<Rigidbody>();
            PlayerPhysics.ConfigureBody(body, capsule, movementMaterial);
            NetworkObject network = player.AddComponent<NetworkObject>();
            NetworkTransform networkTransform = player.AddComponent<NetworkTransform>();
            player.AddComponent<PlayerInputReader>();
            PlayerMotor motor = player.AddComponent<PlayerMotor>();
            RemotePlayerPresentation remotePresentation = player.AddComponent<RemotePlayerPresentation>();
            player.AddComponent<PlayerNameplate>();
            PlayerInteraction interaction = player.AddComponent<PlayerInteraction>();
            ActiveRagdollPuppet puppet = player.AddComponent<ActiveRagdollPuppet>();
            PlayerActorPhysics actorPhysics = player.AddComponent<PlayerActorPhysics>();
            PunchImpactFeedback impact = player.AddComponent<PunchImpactFeedback>();

            Transform pivot = CreateChild(player.transform, "Camera Pivot", new Vector3(0f, 1.258f, 0f));
            Transform worldRoot = CreateChild(player.transform, "World Rig", Vector3.zero);
            Set(network, "_enableStateForwarding", false);
            Set(network, "_networkTransform", networkTransform);
            Set(network, "_enablePrediction", false);
            Set(network, "_graphicalObject", null);
            Set(network, "_detachGraphicalObject", false);
            Set(networkTransform, "_clientAuthoritative", true);
            Set(networkTransform, "_sendToOwner", false);
            Set(networkTransform, "_componentConfiguration", NetworkTransform.ComponentConfigurationType.Rigidbody);
            Set(networkTransform, "_interval", 1);
            Set(networkTransform, "_interpolation", 1);
            Set(networkTransform, "_synchronizeScale", false);
            worldRoot.localScale = new Vector3(1f, 1.85f, 1f);
            Transform torso = CreateLimb(worldRoot, "Torso", PrimitiveType.Cube, new Vector3(0f, 0.15f, 0f), new Vector3(0.7f, 0.9f, 0.42f), material);
            Transform left = CreateLimb(torso, "Left Arm", PrimitiveType.Cube, new Vector3(-0.55f, 0.18f, 0f), new Vector3(0.22f, 0.65f, 0.22f), material);
            Transform right = CreateLimb(torso, "Right Arm", PrimitiveType.Cube, new Vector3(0.55f, 0.18f, 0f), new Vector3(0.22f, 0.65f, 0.22f), material);
            Transform viewRoot = CreateChild(pivot, "First Person Rig", Vector3.zero);
            viewRoot.localScale = new Vector3(1f, 1.85f, 1f);
            Transform viewLeft = CreateLimb(viewRoot, "Left Arm", PrimitiveType.Cube, new Vector3(-0.24f, -0.22f, 0.48f), new Vector3(0.13f, 0.13f, 0.55f), material);
            Transform viewRight = CreateLimb(viewRoot, "Right Arm", PrimitiveType.Cube, new Vector3(0.24f, -0.22f, 0.48f), new Vector3(0.13f, 0.13f, 0.55f), material);
            Set(motor, "_cameraPivot", pivot);
            Set(motor, "_movementMaterial", movementMaterial);
            CopyFloatSettings(existingInteraction, interaction, "_grabRange", "_grabForce", "_grabDamping",
                "_maxGrabForce", "_punchRange", "_punchImpulse", "_punchCooldown");
            Set(puppet, "_bodyRenderer", bodyRenderer);
            Set(puppet, "_worldRoot", worldRoot);
            Set(puppet, "_torso", torso);
            Set(puppet, "_leftArm", left);
            Set(puppet, "_rightArm", right);
            Set(puppet, "_viewRoot", viewRoot);
            Set(puppet, "_viewLeftArm", viewLeft);
            Set(puppet, "_viewRightArm", viewRight);
            remotePresentation.Configure(networkTransform, worldRoot);
            Set(actorPhysics, "_physicalMaterial", physicalMaterial);
            Set(impact, "_impactPrefab", impactPrefab);
            bodyRenderer.enabled = false;
            actorPhysics.Configure(puppet);
            puppet.ConfigurePhysicalWorldArms(body);
            SerializeFishNetNetworkBehaviours(network);
            if (network.NetworkBehaviours.Count != 2 ||
                network.NetworkBehaviours[0] != networkTransform ||
                network.NetworkBehaviours[1] != motor)
            {
                throw new System.InvalidOperationException(
                    "Player FishNet behaviour order must be NetworkTransform (0), PlayerMotor (1).");
            }
            return SavePrefab(player, Prefabs + "/Player.prefab").GetComponent<NetworkObject>();
        }

        private static GameObject BuildPunchImpactPrefab(Material material)
        {
            GameObject root = new("PunchImpact");
            root.layer = GameplayLayers.Presentation;
            Transform center = CreateLimb(root.transform, "Center", PrimitiveType.Sphere, Vector3.zero,
                new Vector3(0.34f, 0.34f, 0.10f), material);
            Renderer centerRenderer = center.GetComponent<Renderer>();
            centerRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            centerRenderer.receiveShadows = false;
            for (int index = 0; index < 4; index++)
            {
                Transform bar = CreateLimb(root.transform, $"Spark {index + 1}", PrimitiveType.Quad, Vector3.zero, new Vector3(1f, 0.105f, 1f), material);
                bar.localRotation = Quaternion.Euler(0f, 0f, index * 45f);
                Renderer renderer = bar.GetComponent<Renderer>();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            return SavePrefab(root, Prefabs + "/PunchImpact.prefab");
        }

        private static GameObject BuildTrainingDummyOfflinePrefab(GameObject playerVisualSource, Material material,
            PhysicsMaterial dummyPhysicsMaterial)
        {
            GameObject dummy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            dummy.name = "TrainingDummy";
            dummy.transform.localScale = Vector3.one;
            CapsuleCollider capsule = dummy.GetComponent<CapsuleCollider>();
            CapsuleCollider playerCapsule = playerVisualSource != null
                ? playerVisualSource.GetComponent<CapsuleCollider>()
                : null;
            capsule.height = playerCapsule != null ? playerCapsule.height : PlayerPhysics.CapsuleHeight;
            capsule.radius = playerCapsule != null ? playerCapsule.radius : PlayerPhysics.CapsuleRadius;
            capsule.center = playerCapsule != null ? playerCapsule.center : Vector3.zero;
            capsule.sharedMaterial = dummyPhysicsMaterial;
            dummy.GetComponent<Renderer>().sharedMaterial = material;
            Rigidbody body = dummy.AddComponent<Rigidbody>();
            TrainingDummy.ConfigureBody(body);

            Transform sourceRig = playerVisualSource != null ? playerVisualSource.transform.Find("World Rig") : null;
            if (sourceRig != null)
            {
                dummy.GetComponent<Renderer>().enabled = false;
                Transform rig = Object.Instantiate(sourceRig.gameObject, dummy.transform, false).transform;
                rig.name = sourceRig.name;
                foreach (Renderer renderer in rig.GetComponentsInChildren<Renderer>(true))
                    renderer.sharedMaterial = material;
                TrainingDummy.AddRagdollLimb(rig.Find("Torso/Left Arm"), body, capsule);
                TrainingDummy.AddRagdollLimb(rig.Find("Torso/Right Arm"), body, capsule);
            }

            dummy.AddComponent<TrainingDummy>();
            GameplayLayers.ApplyRole(dummy, SpawnRole.Actor);
            return SavePrefab(dummy, Prefabs + "/Offline/TrainingDummy.prefab");
        }

        private static NetworkObject BuildNetworkTrainingDummyPrefab(GameObject offlineDummy)
        {
            if (offlineDummy == null)
                throw new System.InvalidOperationException("Offline TrainingDummy prefab must exist first.");
            GameObject dummy = Object.Instantiate(offlineDummy);
            dummy.name = "TrainingDummy";
            NetworkObject network = dummy.AddComponent<NetworkObject>();
            NetworkTransform networkTransform = dummy.AddComponent<NetworkTransform>();
            Set(network, "_networkTransform", networkTransform);
            Set(networkTransform, "_clientAuthoritative", false);
            Set(networkTransform, "_componentConfiguration", NetworkTransform.ComponentConfigurationType.Rigidbody);
            Set(networkTransform, "_interval", 3);
            Set(networkTransform, "_synchronizeScale", false);
            GameplayLayers.ApplyRole(dummy, SpawnRole.Actor);
            return SavePrefab(dummy, Prefabs + "/TrainingDummy.prefab").GetComponent<NetworkObject>();
        }

        [MenuItem("PushUp/Rebuild Attack Dummy Assets")]
        public static void BuildAttackDummyAssets()
        {
            EnsureFolder(Prefabs);
            EnsureFolder(Materials);
            GameObject trainingDummy = AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + "/Offline/TrainingDummy.prefab");
            if (trainingDummy == null)
                throw new System.InvalidOperationException("Offline/TrainingDummy.prefab must exist before building AttackDummy.");
            Material material = GetEditableMaterial("AttackDummy", new Color(0.95f, 0.38f, 0.08f));
            BuildAttackDummyPrefab(trainingDummy, material);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Attack dummy prefab rebuilt without modifying the current scene.");
        }

        private static NetworkObject BuildAttackDummyPrefab(GameObject trainingDummy, Material material)
        {
            GameObject attackDummy = Object.Instantiate(trainingDummy);
            attackDummy.name = "AttackDummy";
            foreach (Renderer renderer in attackDummy.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterial = material;
            NetworkObject network = attackDummy.AddComponent<NetworkObject>();
            NetworkTransform networkTransform = attackDummy.AddComponent<NetworkTransform>();
            Set(networkTransform, "_clientAuthoritative", false);
            Set(networkTransform, "_componentConfiguration", NetworkTransform.ComponentConfigurationType.Rigidbody);
            Set(networkTransform, "_interval", 3);
            Set(networkTransform, "_synchronizeScale", false);
            attackDummy.AddComponent<AttackDummy>();
            attackDummy.AddComponent<AttackDummyNetworkRelay>();
            GameplayLayers.ApplyRole(attackDummy, SpawnRole.Actor);
            return SavePrefab(attackDummy, Prefabs + "/AttackDummy.prefab").GetComponent<NetworkObject>();
        }

        private static NetworkObject BuildBoulderPrefab(Material material, Vector3 scale,
            PhysicsMaterial physicsMaterial)
        {
            GameObject existingBoulder = AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + "/Boulder.prefab");
            BoulderController existingController = existingBoulder != null
                ? existingBoulder.GetComponent<BoulderController>()
                : null;
            GameObject boulder = new("Boulder");
            boulder.name = "Boulder";
            GameplayLayers.ApplyRole(boulder, SpawnRole.PrimaryBoulder);
            boulder.transform.localScale = scale;
            SphereCollider collider = boulder.AddComponent<SphereCollider>();
            collider.sharedMaterial = physicsMaterial;
            Rigidbody body = boulder.AddComponent<Rigidbody>();
            BoulderController.ConfigureBody(body, collider, BoulderController.DefaultMass, physicsMaterial);
            NetworkObject network = boulder.AddComponent<NetworkObject>();
            BoulderController controller = boulder.AddComponent<BoulderController>();
            BoulderNetworkState networkState = boulder.AddComponent<BoulderNetworkState>();
            boulder.AddComponent<NetworkRunState>();
            Transform presentation = CreateLimb(boulder.transform, "Presentation", PrimitiveType.Sphere,
                Vector3.zero, Vector3.one, material);
            Collider presentationCollider = presentation.GetComponent<Collider>();
            if (presentationCollider != null)
                Object.DestroyImmediate(presentationCollider);
            presentation.gameObject.layer = GameplayLayers.Presentation;
            networkState.Configure(presentation);
            CopyFloatSettings(existingController, controller, "_baseMass", "_assistMass");
            body.mass = controller.BaseMass;
            body.linearDamping = 0.08f;
            body.angularDamping = 0.3f;
            Set(controller, "_physicsMaterial", physicsMaterial);
            SerializeFishNetNetworkBehaviours(network);
            return SavePrefab(boulder, Prefabs + "/Boulder.prefab").GetComponent<NetworkObject>();
        }

        private static GameObject BuildOfflineBoulderPrefab(GameObject networkPrefab)
        {
            GameObject instance = Object.Instantiate(networkPrefab);
            instance.name = "Boulder";
            foreach (NetworkBehaviour behaviour in instance.GetComponentsInChildren<NetworkBehaviour>(true))
                Object.DestroyImmediate(behaviour);
            foreach (NetworkObject networkObject in instance.GetComponentsInChildren<NetworkObject>(true))
                Object.DestroyImmediate(networkObject);
            return SavePrefab(instance, Prefabs + "/Offline/Boulder.prefab");
        }

        private static Vector3 ExistingPrefabScale(string path, Vector3 fallback)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            return prefab != null ? prefab.transform.localScale : fallback;
        }

        private static NetworkObject BuildPowerupPrefab(string name, PowerupKind kind, Material material)
        {
            GameObject pickup = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pickup.name = name;
            GameplayLayers.ApplyRole(pickup, SpawnRole.Powerup);
            pickup.transform.localScale = new Vector3(0.65f, 0.22f, 0.65f);
            pickup.GetComponent<Renderer>().sharedMaterial = material;
            Collider collider = pickup.GetComponent<Collider>();
            collider.isTrigger = true;
            pickup.AddComponent<NetworkObject>();
            PowerupPickup component = pickup.AddComponent<PowerupPickup>();
            Set(component, "_kind", kind);
            return SavePrefab(pickup, Prefabs + "/" + name + ".prefab").GetComponent<NetworkObject>();
        }

        private static void CreateWorld(Material terrain, Material path, out Transform[] spawns, out Transform boulderSpawn, out Transform dummySpawn, out Transform[] pickupSpawns, out Transform summit)
        {
            CreateBlock("Base Camp", new Vector3(0f, -0.5f, -9f), new Vector3(18f, 1f, 14f), terrain);
            CreateRamp("Teaching Slope", new Vector3(0f, 1.3f, -1f), new Vector3(10f, 1f, 16f), 16f, terrain);
            CreateBlock("Rest Shelf", new Vector3(0f, 4.2f, 8.3f), new Vector3(12f, 1f, 7f), terrain);
            CreateRamp("Final Ramp", new Vector3(0f, 7.2f, 15f), new Vector3(8f, 1f, 15f), 31f, terrain);
            CreateBlock("Summit", new Vector3(0f, 11.7f, 22f), new Vector3(11f, 1f, 7f), terrain);
            CreateRamp("Left Side Path", new Vector3(-7f, 3.1f, 3f), new Vector3(3f, 0.7f, 15f), 10f, path);
            CreateRamp("Right Side Path", new Vector3(7f, 3.6f, 8f), new Vector3(3f, 0.7f, 17f), 12f, path);
            CreateBlock("Fall Catch", new Vector3(0f, -4f, 8f), new Vector3(40f, 1f, 50f), terrain);

            spawns = new Transform[4];
            for (int index = 0; index < spawns.Length; index++)
                spawns[index] = Marker("Player Spawn " + (index + 1), new Vector3(-3f + index * 2f, 1.8f, -12f));
            boulderSpawn = Marker("Boulder Spawn", new Vector3(0f, 2.3f, -6.5f));
            dummySpawn = Marker("Training Dummy Spawn", new Vector3(3.2f, 1.8f, -8.5f));
            pickupSpawns = new[]
            {
                Marker("Speed Pickup", new Vector3(-7f, 5f, 3f)),
                Marker("Assist Pickup", new Vector3(7f, 5.5f, 8f)),
                Marker("Anchor Pickup", new Vector3(0f, 5.2f, 9f))
            };
            summit = Marker("Summit Goal", new Vector3(0f, 13f, 22f));
        }

        private static void CreateLighting()
        {
            GameObject light = new GameObject("Sun");
            Light component = light.AddComponent<Light>();
            component.type = LightType.Directional;
            component.intensity = 1.25f;
            light.transform.rotation = Quaternion.Euler(48f, -28f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.38f, 0.43f, 0.52f);
            RenderSettings.skybox = null;
            Camera camera = new GameObject("Player Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.transform.position = new Vector3(0f, 5f, -16f);
            camera.transform.rotation = Quaternion.Euler(12f, 0f, 0f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.36f, 0.51f, 0.69f);
        }

        private static GameObject CreateBlock(string name, Vector3 position, Vector3 scale, Material material)
        {
            GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = name;
            block.layer = GameplayLayers.Terrain;
            block.transform.position = position;
            block.transform.localScale = scale;
            block.GetComponent<Renderer>().sharedMaterial = material;
            return block;
        }

        private static GameObject CreateRamp(string name, Vector3 position, Vector3 scale, float angle, Material material)
        {
            GameObject ramp = CreateBlock(name, position, scale, material);
            ramp.transform.rotation = Quaternion.Euler(-angle, 0f, 0f);
            return ramp;
        }

        private static Transform Marker(string name, Vector3 position)
        {
            GameObject marker = new GameObject(name);
            marker.layer = GameplayLayers.Presentation;
            marker.transform.position = position;
            return marker.transform;
        }

        private static Transform CreateChild(Transform parent, string name, Vector3 localPosition)
        {
            GameObject child = new GameObject(name);
            child.transform.SetParent(parent, false);
            child.layer = parent.gameObject.layer;
            child.transform.localPosition = localPosition;
            return child.transform;
        }

        private static Transform CreateLimb(Transform parent, string name, PrimitiveType type, Vector3 localPosition, Vector3 localScale, Material material)
        {
            GameObject limb = GameObject.CreatePrimitive(type);
            limb.name = name;
            limb.transform.SetParent(parent, false);
            limb.layer = parent.gameObject.layer;
            limb.transform.localPosition = localPosition;
            limb.transform.localScale = localScale;
            limb.GetComponent<Collider>().enabled = false;
            limb.GetComponent<Renderer>().sharedMaterial = material;
            return limb.transform;
        }

        private static Material GetMaterial(string name, Color color)
        {
            string path = Materials + "/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.color = color;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material GetEditableMaterial(string name, Color defaultColor)
        {
            string path = Materials + "/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null)
                return material;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            material = new Material(shader) { color = defaultColor };
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static Material GetPunchImpactMaterial()
        {
            string path = Materials + "/PunchImpact.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
            }

            Color color = new(1f, 0.82f, 0.18f, 1f);
            material.SetColor("_BaseColor", color);
            material.SetColor("_Color", color);
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            material.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.LessEqual);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static PhysicsMaterial GetPhysicsMaterial(string name, float dynamicFriction,
            float staticFriction, float bounciness, PhysicsMaterialCombine frictionCombine,
            PhysicsMaterialCombine bounceCombine)
        {
            string path = Materials + "/" + name + ".physicMaterial";
            PhysicsMaterial material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            if (material == null)
            {
                material = new PhysicsMaterial(name);
                AssetDatabase.CreateAsset(material, path);
            }

            material.dynamicFriction = dynamicFriction;
            material.staticFriction = staticFriction;
            material.bounciness = bounciness;
            material.frictionCombine = frictionCombine;
            material.bounceCombine = bounceCombine;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static GameObject SavePrefab(GameObject source, string path)
        {
            NetworkObject networkObject = source.GetComponent<NetworkObject>();
            if (networkObject != null)
            {
                SerializeFishNetNetworkBehaviours(networkObject);
                networkObject.SetAssetPathHash(CalculateFishNetAssetPathHash(path, source.name));
            }
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(source, path);
            Object.DestroyImmediate(source);
            return prefab;
        }

        /// <summary>
        /// Persists the FishNet lookup data normally established by its editor/runtime initialization path.
        /// Prefabs assembled entirely through script do not receive that initialization before they are saved,
        /// leaving NetworkBehaviours empty and each behaviour's component index at 255.
        /// </summary>
        private static void SerializeFishNetNetworkBehaviours(NetworkObject networkObject)
        {
            NetworkBehaviour[] discovered = networkObject.GetComponentsInChildren<NetworkBehaviour>(true);
            networkObject.NetworkBehaviours ??= new System.Collections.Generic.List<NetworkBehaviour>();
            networkObject.NetworkBehaviours.Clear();

            foreach (NetworkBehaviour behaviour in discovered)
            {
                if (FindNearestNetworkObject(behaviour.transform) != networkObject)
                    continue;
                if (networkObject.NetworkBehaviours.Count >= NetworkBehaviour.MAXIMUM_NETWORKBEHAVIOURS)
                {
                    throw new System.InvalidOperationException(
                        $"{networkObject.name} exceeds FishNet's NetworkBehaviour limit.");
                }

                byte componentIndex = (byte)networkObject.NetworkBehaviours.Count;
                networkObject.NetworkBehaviours.Add(behaviour);

                SerializedObject serializedBehaviour = new(behaviour);
                SerializedProperty indexCache = serializedBehaviour.FindProperty("_componentIndexCache");
                SerializedProperty networkObjectCache = serializedBehaviour.FindProperty("_networkObjectCache");
                SerializedProperty addedNetworkObject = serializedBehaviour.FindProperty("_addedNetworkObject");
                if (indexCache == null || networkObjectCache == null || addedNetworkObject == null)
                {
                    throw new System.InvalidOperationException(
                        $"FishNet serialization fields changed for {behaviour.GetType().Name}.");
                }

                indexCache.intValue = componentIndex;
                networkObjectCache.objectReferenceValue = networkObject;
                addedNetworkObject.objectReferenceValue = networkObject;
                serializedBehaviour.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(behaviour);
            }

            EditorUtility.SetDirty(networkObject);
        }

        private static NetworkObject FindNearestNetworkObject(Transform transform)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (current.TryGetComponent(out NetworkObject result))
                    return result;
            }

            return null;
        }

        private static ulong CalculateFishNetAssetPathHash(string path, string objectName)
        {
            string pathAndName = (path + objectName).Trim().ToLowerInvariant();
            StringBuilder normalized = new(pathAndName.Length);
            foreach (char character in pathAndName)
            {
                if ((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9'))
                    normalized.Append(character);
            }
            return normalized.ToString().GetStableHashU64();
        }

        private static void Set(Object target, string property, object value)
        {
            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty field = serialized.FindProperty(property);
            if (field == null)
                throw new System.InvalidOperationException("Missing serialized property: " + property);
            if (value == null && field.propertyType == SerializedPropertyType.ObjectReference)
            {
                field.objectReferenceValue = null;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                return;
            }
            switch (value)
            {
                case Object objectReference:
                    field.objectReferenceValue = objectReference;
                    break;
                case Object[] objectReferences:
                    field.arraySize = objectReferences.Length;
                    for (int index = 0; index < objectReferences.Length; index++)
                        field.GetArrayElementAtIndex(index).objectReferenceValue = objectReferences[index];
                    break;
                case PowerupKind enumValue:
                    field.enumValueIndex = (int)enumValue;
                    break;
                case System.Enum enumValue:
                    field.enumValueIndex = System.Convert.ToInt32(enumValue);
                    break;
                case bool boolValue:
                    field.boolValue = boolValue;
                    break;
                case int intValue:
                    field.intValue = intValue;
                    break;
                default:
                    throw new System.InvalidOperationException("Unsupported property assignment: " + property);
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CopyFloatSettings(Component source, Component target, params string[] properties)
        {
            if (source == null || target == null)
                return;
            SerializedObject sourceObject = new(source);
            SerializedObject targetObject = new(target);
            foreach (string property in properties)
            {
                SerializedProperty sourceField = sourceObject.FindProperty(property);
                SerializedProperty targetField = targetObject.FindProperty(property);
                if (sourceField == null || targetField == null ||
                    sourceField.propertyType != SerializedPropertyType.Float ||
                    targetField.propertyType != SerializedPropertyType.Float)
                    throw new System.InvalidOperationException("Missing float prefab tuning property: " + property);
                targetField.floatValue = sourceField.floatValue;
            }
            targetObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            string name = System.IO.Path.GetFileName(path);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
