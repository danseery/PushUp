using System;
using System.Linq;
using FishNet.Managing.Object;
using FishNet.Object;
using PushUp.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PushUp.Editor
{
    public static class PushUpProjectValidator
    {
        private const string ScenePath = "Assets/PushUp/Scenes/Mountain.unity";

        public static void Validate()
        {
            bool sceneEnabled = Array.Exists(EditorBuildSettings.scenes, scene => scene.enabled && scene.path == ScenePath);
            if (!sceneEnabled)
                throw new InvalidOperationException("Mountain scene is missing from Build Settings.");

            EditorSceneManager.OpenScene(ScenePath);
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (!gameObject.scene.IsValid())
                    continue;
                foreach (Component component in gameObject.GetComponents<Component>())
                {
                    if (component == null)
                        throw new InvalidOperationException("Missing script on " + gameObject.name);
                }
            }

            LevelLayout layout = UnityEngine.Object.FindFirstObjectByType<LevelLayout>(FindObjectsInactive.Include);
            if (layout == null)
                throw new InvalidOperationException("Mountain scene has no LevelLayout.");
            if (!layout.ValidateLayout(out string[] layoutErrors))
                throw new InvalidOperationException(string.Join(Environment.NewLine, layoutErrors));

            ValidateNetworkPrefab("Assets/PushUp/Prefabs/Player.prefab");
            ValidateNetworkPrefab("Assets/PushUp/Prefabs/Boulder.prefab");
            ValidateNetworkPrefab("Assets/PushUp/Prefabs/SpeedBoost.prefab");
            ValidateNetworkPrefab("Assets/PushUp/Prefabs/AttackDummy.prefab");
            DefaultPrefabObjects prefabs = AssetDatabase.LoadAssetAtPath<DefaultPrefabObjects>("Assets/DefaultPrefabObjects.asset");
            if (prefabs == null || prefabs.GetObjectCount() < 6)
                throw new InvalidOperationException("FishNet default prefab collection was not generated.");

            foreach (SpawnDefinition definition in layout.WorldSpawns.Select(point => point.Definition)
                         .Concat(layout.PlayerSpawns.Select(point => point.Definition)).Where(value => value != null).Distinct())
            {
                if (definition.Policy == SpawnPolicy.HostLocal)
                    continue;
                NetworkObject networkObject = definition.Prefab != null
                    ? definition.Prefab.GetComponent<NetworkObject>()
                    : null;
                if (networkObject == null)
                    throw new InvalidOperationException($"Spawn definition '{definition.DisplayName}' requires a NetworkObject prefab.");
                bool registered = false;
                for (int index = 0; index < prefabs.GetObjectCount(); index++)
                    registered |= prefabs.GetObject(false, index) == networkObject;
                if (!registered)
                    throw new InvalidOperationException($"Spawn definition '{definition.DisplayName}' is missing from FishNet DefaultPrefabObjects.");
            }

            Debug.Log("PushUp validation passed: authored level, build settings, components, and FishNet prefab collection are valid.");
        }

        private static void ValidateNetworkPrefab(string path)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || prefab.GetComponent<NetworkObject>() == null)
                throw new InvalidOperationException("Network prefab missing or invalid: " + path);
        }
    }
}
