using System;
using System.Collections.Generic;
using System.Linq;
using FishNet.Object;
using FishNet.Managing.Object;
using PushUp.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PushUp.Editor
{
    public static class LevelAuthoringTools
    {
        public const string DefinitionFolder = "Assets/PushUp/SpawnDefinitions";
        public const string OfflinePrefabFolder = "Assets/PushUp/Prefabs/Offline";
        private const string MountainScene = "Assets/PushUp/Scenes/Mountain.unity";

        [MenuItem("PushUp/Level Design/Migrate Current Mountain Scene")]
        public static void MigrateMountainScene()
        {
            EditorSceneManager.OpenScene(MountainScene);
            MigrateOpenScene(true);
        }

        public static LevelLayout MigrateOpenScene(bool saveScene)
        {
            EnsureFolder(DefinitionFolder);
            Dictionary<string, SpawnDefinition> definitions = CreateOrUpdateDefinitions();
            GameObject root = FindSceneObject("Level Authoring") ?? new GameObject("Level Authoring");
            LevelLayout layout = GetOrAdd<LevelLayout>(root);

            Transform playerGroup = Group(root.transform, "Player Starts", "player-starts", true);
            Transform coreGroup = Group(root.transform, "Core", "core", true);
            Transform actorGroup = Group(root.transform, "Actors", "actors", true);
            Transform powerupGroup = Group(root.transform, "Powerups", "powerups", true);
            Transform goalGroup = Group(root.transform, "Goals", "goals", true);

            for (int index = 0; index < 4; index++)
            {
                string objectName = $"Player Spawn {index + 1}";
                GameObject marker = FindSceneObject(objectName) ?? NewMarker(objectName,
                    new Vector3(-3f + index * 2f, 1.8f, -12f));
                MovePreservingWorld(marker.transform, playerGroup);
                ConfigurePlayerMarker(GetOrAdd<PlayerSpawnPoint>(marker), index,
                    $"mountain.player.{index}", definitions["player"]);
            }

            GameObject boulder = FindSceneObject("Boulder Spawn") ?? NewMarker("Boulder Spawn", new Vector3(0f, 2.3f, -6.5f));
            MovePreservingWorld(boulder.transform, coreGroup);
            ConfigureWorldMarker(GetOrAdd<WorldSpawnPoint>(boulder), "mountain.boulder", definitions["boulder"]);

            GameObject training = FindSceneObject("Training Dummy Spawn") ?? FindSceneObject("Dummy Spawn") ??
                                  NewMarker("Training Dummy Spawn", new Vector3(3.2f, 1.8f, -8.5f));
            MovePreservingWorld(training.transform, actorGroup);
            ConfigureWorldMarker(GetOrAdd<WorldSpawnPoint>(training), "mountain.training-dummy", definitions["training-dummy"]);

            GameObject attack = FindSceneObject("Attack Dummy Spawn");
            if (attack == null)
                attack = NewMarker("Attack Dummy Spawn", training.transform.position + new Vector3(4f, 0f, 2f));
            MovePreservingWorld(attack.transform, actorGroup);
            ConfigureWorldMarker(GetOrAdd<WorldSpawnPoint>(attack), "mountain.attack-dummy", definitions["attack-dummy"]);

            ConfigureNamedWorld("Speed Pickup", powerupGroup, "mountain.speed", definitions["speed"], new Vector3(-7f, 5f, 3f));
            ConfigureNamedWorld("Assist Pickup", powerupGroup, "mountain.assist", definitions["assist"], new Vector3(7f, 5.5f, 8f));
            ConfigureNamedWorld("Anchor Pickup", powerupGroup, "mountain.anchor", definitions["anchor"], new Vector3(0f, 5.2f, 9f));

            GameObject summit = FindSceneObject("Summit Goal") ?? NewMarker("Summit Goal", new Vector3(0f, 13f, 22f));
            MovePreservingWorld(summit.transform, goalGroup);
            ConfigureFloat(GetOrAdd<SummitGoal>(summit), "_radius", 4f);

            GameObject systems = FindSceneObject("Game Systems");
            if (systems != null)
            {
                LevelSpawnService service = GetOrAdd<LevelSpawnService>(systems);
                RunDirector director = systems.GetComponent<RunDirector>();
                if (director != null)
                {
                    SerializedObject serialized = new(director);
                    serialized.FindProperty("_levelLayout").objectReferenceValue = layout;
                    serialized.FindProperty("_spawnService").objectReferenceValue = service;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            SyncFishNetPrefabs(definitions.Values);
            EditorUtility.SetDirty(layout);
            if (saveScene)
            {
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
                AssetDatabase.SaveAssets();
            }
            return layout;
        }

        [MenuItem("GameObject/PushUp/Spawn Group", false, 20)]
        private static void CreateSpawnGroup(MenuCommand command)
        {
            GameObject created = CreateAuthoringObject("Spawn Group", command.context as GameObject);
            GetOrAdd<SpawnGroup>(created);
        }

        [MenuItem("GameObject/PushUp/World Spawn Point", false, 21)]
        private static void CreateWorldSpawn(MenuCommand command)
        {
            GameObject created = CreateAuthoringObject("World Spawn Point", command.context as GameObject);
            GetOrAdd<WorldSpawnPoint>(created);
        }

        [MenuItem("GameObject/PushUp/Player Spawn Point", false, 22)]
        private static void CreatePlayerSpawn(MenuCommand command)
        {
            GameObject created = CreateAuthoringObject("Player Spawn Point", command.context as GameObject);
            GetOrAdd<PlayerSpawnPoint>(created);
        }

        [MenuItem("GameObject/PushUp/Summit Goal", false, 23)]
        private static void CreateSummitGoal(MenuCommand command)
        {
            GameObject created = CreateAuthoringObject("Summit Goal", command.context as GameObject);
            GetOrAdd<SummitGoal>(created);
        }

        public static Dictionary<string, SpawnDefinition> CreateOrUpdateDefinitions()
        {
            EnsureFolder(DefinitionFolder);
            EnsureFolder(OfflinePrefabFolder);
            Dictionary<string, GameObject> offline = CreateOrUpdateOfflinePrefabs();
            Dictionary<string, SpawnDefinition> result = new(StringComparer.Ordinal)
            {
                ["player"] = Definition("Player", "player", "Player", "Assets/PushUp/Prefabs/Player.prefab", SpawnRole.Player, SpawnPolicy.PlayerOwned),
                ["boulder"] = Definition("Boulder", "boulder", "Boulder", "Assets/PushUp/Prefabs/Boulder.prefab", SpawnRole.PrimaryBoulder, SpawnPolicy.Replicated, offline["boulder"]),
                ["training-dummy"] = Definition("TrainingDummy", "training-dummy", "Training Dummy", "Assets/PushUp/Prefabs/TrainingDummy.prefab", SpawnRole.Actor, SpawnPolicy.Replicated, offline["training-dummy"]),
                ["attack-dummy"] = Definition("AttackDummy", "attack-dummy", "Attack Dummy", "Assets/PushUp/Prefabs/AttackDummy.prefab", SpawnRole.Actor, SpawnPolicy.Replicated, offline["attack-dummy"]),
                ["speed"] = Definition("SpeedBoost", "speed", "Speed Boost", "Assets/PushUp/Prefabs/SpeedBoost.prefab", SpawnRole.Powerup, SpawnPolicy.Replicated, offline["speed"]),
                ["assist"] = Definition("BoulderAssist", "assist", "Boulder Assist", "Assets/PushUp/Prefabs/BoulderAssist.prefab", SpawnRole.Powerup, SpawnPolicy.Replicated, offline["assist"]),
                ["anchor"] = Definition("CarryAnchor", "anchor", "Anchor", "Assets/PushUp/Prefabs/CarryAnchor.prefab", SpawnRole.Powerup, SpawnPolicy.Replicated, offline["anchor"])
            };
            AssetDatabase.SaveAssets();
            return result;
        }

        private static SpawnDefinition Definition(string assetName, string id, string displayName,
            string prefabPath, SpawnRole role, SpawnPolicy policy, GameObject offlineOverride = null)
        {
            string path = $"{DefinitionFolder}/{assetName}.asset";
            SpawnDefinition definition = AssetDatabase.LoadAssetAtPath<SpawnDefinition>(path);
            if (definition == null)
            {
                definition = ScriptableObject.CreateInstance<SpawnDefinition>();
                AssetDatabase.CreateAsset(definition, path);
            }
            SerializedObject serialized = new(definition);
            serialized.FindProperty("_id").stringValue = id;
            serialized.FindProperty("_displayName").stringValue = displayName;
            serialized.FindProperty("_prefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            serialized.FindProperty("_offlineOverride").objectReferenceValue = offlineOverride;
            serialized.FindProperty("_role").enumValueIndex = (int)role;
            serialized.FindProperty("_policy").enumValueIndex = (int)policy;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return definition;
        }

        [MenuItem("PushUp/Level Design/Rebuild Offline Spawn Prefabs")]
        public static void RebuildOfflineSpawnPrefabs()
        {
            CreateOrUpdateDefinitions();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Offline spawn prefabs rebuilt and SpawnDefinitions updated.");
        }

        private static Dictionary<string, GameObject> CreateOrUpdateOfflinePrefabs()
        {
            EnsureAttackDummyNetworkRelay();
            return new Dictionary<string, GameObject>(StringComparer.Ordinal)
            {
                ["boulder"] = CreateOfflinePrefab("Assets/PushUp/Prefabs/Boulder.prefab", "Boulder"),
                ["training-dummy"] = CreateOfflinePrefab("Assets/PushUp/Prefabs/TrainingDummy.prefab", "TrainingDummy"),
                ["attack-dummy"] = CreateOfflinePrefab("Assets/PushUp/Prefabs/AttackDummy.prefab", "AttackDummy"),
                ["speed"] = CreateOfflinePrefab("Assets/PushUp/Prefabs/SpeedBoost.prefab", "SpeedBoost"),
                ["assist"] = CreateOfflinePrefab("Assets/PushUp/Prefabs/BoulderAssist.prefab", "BoulderAssist"),
                ["anchor"] = CreateOfflinePrefab("Assets/PushUp/Prefabs/CarryAnchor.prefab", "CarryAnchor")
            };
        }

        private static void EnsureAttackDummyNetworkRelay()
        {
            const string path = "Assets/PushUp/Prefabs/AttackDummy.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            if (root == null)
                return;
            try
            {
                if (root.GetComponent<AttackDummy>() != null && root.GetComponent<AttackDummyNetworkRelay>() == null)
                {
                    root.AddComponent<AttackDummyNetworkRelay>();
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static GameObject CreateOfflinePrefab(string sourcePath, string name)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (source == null)
                throw new InvalidOperationException($"Cannot build offline spawn prefab because '{sourcePath}' is missing.");

            GameObject instance = PrefabUtility.InstantiatePrefab(source) as GameObject;
            if (instance == null)
                throw new InvalidOperationException($"Could not instantiate '{sourcePath}' for its offline copy.");
            try
            {
                if (PrefabUtility.IsPartOfPrefabInstance(instance))
                    PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely,
                        InteractionMode.AutomatedAction);
                foreach (NetworkBehaviour behaviour in instance.GetComponentsInChildren<NetworkBehaviour>(true))
                    UnityEngine.Object.DestroyImmediate(behaviour);
                foreach (NetworkObject networkObject in instance.GetComponentsInChildren<NetworkObject>(true))
                    UnityEngine.Object.DestroyImmediate(networkObject);
                instance.name = name;
                string path = $"{OfflinePrefabFolder}/{name}.prefab";
                return PrefabUtility.SaveAsPrefabAsset(instance, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static void SyncFishNetPrefabs(IEnumerable<SpawnDefinition> definitions)
        {
            DefaultPrefabObjects collection = AssetDatabase.LoadAssetAtPath<DefaultPrefabObjects>("Assets/DefaultPrefabObjects.asset");
            if (collection == null)
                return;
            foreach (SpawnDefinition definition in definitions)
            {
                if (definition == null || definition.Policy == SpawnPolicy.HostLocal || definition.Prefab == null)
                    continue;
                NetworkObject networkObject = definition.Prefab.GetComponent<NetworkObject>();
                if (networkObject == null)
                    continue;
                bool exists = false;
                for (int index = 0; index < collection.GetObjectCount(); index++)
                {
                    if (collection.GetObject(false, index) == networkObject)
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                    collection.AddObject(networkObject, true, true);
            }
            EditorUtility.SetDirty(collection);
        }

        private static void ConfigureNamedWorld(string name, Transform parent, string markerId,
            SpawnDefinition definition, Vector3 fallback)
        {
            GameObject marker = FindSceneObject(name) ?? NewMarker(name, fallback);
            MovePreservingWorld(marker.transform, parent);
            ConfigureWorldMarker(GetOrAdd<WorldSpawnPoint>(marker), markerId, definition);
        }

        private static Transform Group(Transform root, string name, string id, bool autoStart)
        {
            Transform child = root.Find(name);
            if (child == null)
            {
                child = new GameObject(name).transform;
                child.SetParent(root, false);
            }
            SpawnGroup group = GetOrAdd<SpawnGroup>(child.gameObject);
            SerializedObject serialized = new(group);
            serialized.FindProperty("_id").stringValue = id;
            serialized.FindProperty("_spawnAtRunStart").boolValue = autoStart;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return child;
        }

        private static void ConfigureWorldMarker(WorldSpawnPoint marker, string id, SpawnDefinition definition)
        {
            SerializedObject serialized = new(marker);
            serialized.FindProperty("_markerId").stringValue = id;
            serialized.FindProperty("_definition").objectReferenceValue = definition;
            serialized.FindProperty("_enabled").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigurePlayerMarker(PlayerSpawnPoint marker, int slot, string id, SpawnDefinition definition)
        {
            SerializedObject serialized = new(marker);
            serialized.FindProperty("_markerId").stringValue = id;
            serialized.FindProperty("_slot").intValue = slot;
            serialized.FindProperty("_definition").objectReferenceValue = definition;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureFloat(UnityEngine.Object target, string field, float value)
        {
            SerializedObject serialized = new(target);
            serialized.FindProperty(field).floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject CreateAuthoringObject(string name, GameObject context)
        {
            GameObject created = new(name);
            Undo.RegisterCreatedObjectUndo(created, "Create " + name);
            Transform parent = context != null ? context.transform : Selection.activeTransform;
            if (parent != null)
                GameObjectUtility.SetParentAndAlign(created, parent.gameObject);
            Selection.activeGameObject = created;
            return created;
        }

        private static GameObject FindSceneObject(string name) => Resources.FindObjectsOfTypeAll<GameObject>()
            .FirstOrDefault(candidate => candidate.scene == SceneManager.GetActiveScene() && candidate.name == name);

        private static GameObject NewMarker(string name, Vector3 position)
        {
            GameObject marker = new(name);
            marker.transform.position = position;
            return marker;
        }

        private static void MovePreservingWorld(Transform child, Transform parent)
        {
            if (child.parent != parent)
                child.SetParent(parent, true);
        }

        private static T GetOrAdd<T>(GameObject target) where T : Component =>
            target.TryGetComponent(out T component) ? component : target.AddComponent<T>();

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            string name = System.IO.Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }

    [CustomEditor(typeof(WorldSpawnPoint))]
    public sealed class WorldSpawnPointEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            WorldSpawnPoint point = (WorldSpawnPoint)target;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Inherited Group", point.GroupId);
                EditorGUILayout.ObjectField("Resolved Prefab",
                    point.Definition != null ? point.Definition.Prefab : null, typeof(GameObject), false);
            }
        }
    }

    [CustomEditor(typeof(LevelLayout))]
    public sealed class LevelLayoutEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            LevelLayout layout = (LevelLayout)target;
            if (GUILayout.Button("Validate Level Layout"))
            {
                if (layout.ValidateLayout(out string[] errors))
                    Debug.Log("Level layout is valid.", layout);
                else
                    Debug.LogError(string.Join("\n", errors), layout);
            }
        }
    }

    [CustomEditor(typeof(PlayerSpawnPoint))]
    public sealed class PlayerSpawnPointEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            PlayerSpawnPoint point = (PlayerSpawnPoint)target;
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Resolved Prefab",
                    point.Definition != null ? point.Definition.Prefab : null, typeof(GameObject), false);
        }
    }

    public static class LevelAuthoringGizmos
    {
        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected)]
        private static void DrawWorld(WorldSpawnPoint point, GizmoType gizmoType)
        {
            Color color = point.Definition == null ? Color.magenta : point.Definition.Role switch
            {
                SpawnRole.PrimaryBoulder => Color.gray,
                SpawnRole.Actor => new Color(1f, 0.25f, 0.12f),
                SpawnRole.Powerup => Color.yellow,
                SpawnRole.Prop => Color.white,
                _ => Color.cyan
            };
            Gizmos.color = color;
            Gizmos.DrawWireSphere(point.transform.position, 0.45f);
            Gizmos.DrawLine(point.transform.position, point.transform.position + point.transform.forward * 1.2f);
            Handles.color = color;
            Handles.Label(point.transform.position + Vector3.up * 0.55f,
                point.Definition != null ? point.Definition.DisplayName : "Missing definition");
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected)]
        private static void DrawPlayer(PlayerSpawnPoint point, GizmoType gizmoType)
        {
            Gizmos.color = Color.cyan;
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(point.transform.position + Vector3.up, point.transform.rotation,
                Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero,
                new Vector3(PlayerPhysics.CapsuleRadius * 2f, PlayerPhysics.CapsuleHeight,
                    PlayerPhysics.CapsuleRadius * 2f));
            Gizmos.matrix = previous;
            Gizmos.DrawLine(point.transform.position, point.transform.position + point.transform.forward * 1.2f);
            Handles.Label(point.transform.position + Vector3.up * 2f, $"Player {point.Slot + 1}");
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected)]
        private static void DrawGoal(SummitGoal goal, GizmoType gizmoType)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(goal.transform.position, goal.Radius);
            Handles.Label(goal.transform.position + Vector3.up * 0.5f, "Summit Goal");
        }
    }
}
