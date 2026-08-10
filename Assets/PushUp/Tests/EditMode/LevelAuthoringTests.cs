using System.Linq;
using System.Reflection;
using FishNet.Managing;
using NUnit.Framework;
using PushUp.Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PushUp.Tests
{
    public sealed class LevelAuthoringTests
    {
        private const string MountainScene = "Assets/PushUp/Scenes/Mountain.unity";

        [Test]
        public void MountainUsesValidatedSceneAuthoredMarkersWithoutLegacyDirectorArrays()
        {
            EditorSceneManager.OpenScene(MountainScene);
            LevelLayout layout = Object.FindFirstObjectByType<LevelLayout>(FindObjectsInactive.Include);
            Assert.That(layout, Is.Not.Null);
            Assert.That(layout.ValidateLayout(out string[] errors), Is.True, string.Join("\n", errors));
            Assert.That(layout.PlayerSpawns.Select(point => point.Slot), Is.EqualTo(new[] { 0, 1, 2, 3 }));
            Assert.That(layout.WorldSpawns.Length, Is.EqualTo(6));
            Assert.That(layout.Groups.Select(group => group.Id),
                Is.EquivalentTo(new[] { "player-starts", "core", "actors", "powerups", "goals" }));

            WorldSpawnPoint attack = layout.WorldSpawns.Single(point => point.Definition.Id == "attack-dummy");
            Assert.That(attack.MarkerId, Is.EqualTo("mountain.attack-dummy"));
            Assert.That(attack.Definition.Prefab.GetComponent<AttackDummy>(), Is.Not.Null);

            Assert.That(typeof(RunDirector).GetField("_playerPrefab", BindingFlags.Instance | BindingFlags.NonPublic), Is.Null);
            Assert.That(typeof(RunDirector).GetField("_boulderPrefab", BindingFlags.Instance | BindingFlags.NonPublic), Is.Null);
            Assert.That(typeof(RunDirector).GetField("_spawnMarkers", BindingFlags.Instance | BindingFlags.NonPublic), Is.Null);
            Assert.That(typeof(RunDirector).GetField("_attackDummyPrefab", BindingFlags.Instance | BindingFlags.NonPublic), Is.Null);
        }

        [Test]
        public void SpawnDefinitionsOwnRolesPoliciesAndPrefabReferences()
        {
            string[] assets = AssetDatabase.FindAssets("t:SpawnDefinition", new[] { "Assets/PushUp/SpawnDefinitions" })
                .Select(AssetDatabase.GUIDToAssetPath).ToArray();
            Assert.That(assets.Length, Is.EqualTo(7));
            SpawnDefinition[] definitions = assets.Select(AssetDatabase.LoadAssetAtPath<SpawnDefinition>).ToArray();
            Assert.That(definitions.All(definition => definition.IsValid(out _)), Is.True);
            Assert.That(definitions.Single(definition => definition.Id == "player").Policy,
                Is.EqualTo(SpawnPolicy.PlayerOwned));
            Assert.That(definitions.Single(definition => definition.Id == "training-dummy").Policy,
                Is.EqualTo(SpawnPolicy.Replicated));
            Assert.That(definitions.Single(definition => definition.Id == "attack-dummy").Policy,
                Is.EqualTo(SpawnPolicy.Replicated));
            Assert.That(definitions.Single(definition => definition.Id == "boulder").Role,
                Is.EqualTo(SpawnRole.PrimaryBoulder));
            Assert.That(definitions.Where(definition => definition.Policy == SpawnPolicy.Replicated)
                .All(definition => definition.HasOfflineOverride), Is.True);
            Assert.That(definitions.Where(definition => definition.HasOfflineOverride)
                .All(definition => definition.OfflinePrefab.GetComponent<FishNet.Object.NetworkObject>() == null), Is.True);
        }


        [Test]
        public void LayoutValidationRejectsDuplicateMarkerIds()
        {
            EditorSceneManager.OpenScene(MountainScene);
            LevelLayout layout = Object.FindFirstObjectByType<LevelLayout>(FindObjectsInactive.Include);
            WorldSpawnPoint first = layout.WorldSpawns[0];
            WorldSpawnPoint second = layout.WorldSpawns[1];
            SerializedObject serialized = new(second);
            serialized.FindProperty("_markerId").stringValue = first.MarkerId;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(layout.ValidateLayout(out string[] errors), Is.False);
            Assert.That(errors.Any(error => error.Contains("duplicated")), Is.True);
        }

        [Test]
        public void OfflineSpawnServiceCreatesAuthoredWorldOnceAndInitializesPowerups()
        {
            EditorSceneManager.OpenScene(MountainScene);
            LevelLayout layout = Object.FindFirstObjectByType<LevelLayout>(FindObjectsInactive.Include);
            LevelSpawnService service = Object.FindFirstObjectByType<LevelSpawnService>(FindObjectsInactive.Include);
            Vector3 authoredAttackPosition = layout.WorldSpawns
                .Single(point => point.Definition.Id == "attack-dummy").transform.position;
            SpawnGroup actorGroup = layout.Groups.Single(group => group.Id == "actors");
            SerializedObject groupSettings = new(actorGroup);
            groupSettings.FindProperty("_spawnAtRunStart").boolValue = false;
            groupSettings.ApplyModifiedPropertiesWithoutUndo();
            service.Configure(layout, null);
            try
            {
                Assert.That(service.BeginOfflineRun(), Is.True, service.LastError);
                Assert.That(service.SpawnedWorldCount, Is.EqualTo(4));
                Assert.That(service.PrimaryBoulder, Is.Not.Null);
                Assert.That(GameObject.Find("Offline Player"), Is.Not.Null);
                Assert.That(Object.FindObjectsByType<AttackDummy>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None).Length, Is.Zero);

                Assert.That(service.ActivateGroup("actors"), Is.True);
                AttackDummy[] fighters = Object.FindObjectsByType<AttackDummy>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                Assert.That(fighters.Length, Is.EqualTo(1));
                Assert.That(fighters[0].transform.position, Is.EqualTo(authoredAttackPosition));
                Assert.That(service.SpawnedWorldCount, Is.EqualTo(6));

                FieldInfo boulderField = typeof(PowerupPickup).GetField("_boulder",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                PowerupPickup[] pickups = Object.FindObjectsByType<PowerupPickup>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                Assert.That(pickups.Length, Is.EqualTo(3));
                Assert.That(pickups.All(pickup => boulderField?.GetValue(pickup) == service.PrimaryBoulder), Is.True);

                int before = service.SpawnedWorldCount;
                Assert.That(service.ActivateGroup("actors"), Is.True);
                Assert.That(service.SpawnedWorldCount, Is.EqualTo(before), "group activation is idempotent");
                Assert.That(service.ActivateGroup("missing-group"), Is.False);
            }
            finally
            {
                service.Clear();
            }
        }
    }
}
