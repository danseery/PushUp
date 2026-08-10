using FishNet.Component.Transforming;
using FishNet.Object;
using NUnit.Framework;
using PushUp.Gameplay;
using UnityEditor;
using UnityEngine;

namespace PushUp.Tests
{
    public sealed class PlayerPrefabSerializationTests
    {
        [Test]
        public void PlayerPrefabPersistsFishNetBehaviourOrderAndCaches()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/PushUp/Prefabs/Player.prefab");
            Assert.That(prefab, Is.Not.Null);

            NetworkObject networkObject = prefab.GetComponent<NetworkObject>();
            NetworkTransform networkTransform = prefab.GetComponent<NetworkTransform>();
            PlayerMotor playerMotor = prefab.GetComponent<PlayerMotor>();
            PlayerNameplate nameplate = prefab.GetComponent<PlayerNameplate>();
            Assert.That(networkObject, Is.Not.Null);
            Assert.That(networkTransform, Is.Not.Null);
            Assert.That(playerMotor, Is.Not.Null);
            Assert.That(nameplate, Is.Not.Null);

            Assert.That(networkObject.NetworkBehaviours, Has.Count.EqualTo(2));
            Assert.That(networkObject.NetworkBehaviours[0], Is.SameAs(networkTransform));
            Assert.That(networkObject.NetworkBehaviours[1], Is.SameAs(playerMotor));
            AssertBehaviourCache(networkTransform, networkObject, 0);
            AssertBehaviourCache(playerMotor, networkObject, 1);
        }

        [Test]
        public void SteamDisplayNamesAreSanitizedForWorldSpaceLabels()
        {
            Assert.That(PlayerNameplate.SanitizeDisplayName("  Friend Name  "), Is.EqualTo("Friend Name"));
            Assert.That(PlayerNameplate.SanitizeDisplayName("Bad\nName\t"), Is.EqualTo("BadName"));
            Assert.That(PlayerNameplate.SanitizeDisplayName(new string('A', 50)).Length,
                Is.EqualTo(PlayerNameplate.MaximumDisplayNameLength));
            Assert.That(PlayerNameplate.SanitizeDisplayName(string.Empty, "7656119"), Is.EqualTo("7656119"));
        }

        private static void AssertBehaviourCache(NetworkBehaviour behaviour, NetworkObject expectedObject,
            byte expectedIndex)
        {
            Assert.That(behaviour.ComponentIndex, Is.EqualTo(expectedIndex));
            Assert.That(behaviour.NetworkObject, Is.SameAs(expectedObject));

            SerializedObject serialized = new(behaviour);
            Assert.That(serialized.FindProperty("_componentIndexCache").intValue, Is.EqualTo(expectedIndex));
            Assert.That(serialized.FindProperty("_networkObjectCache").objectReferenceValue,
                Is.SameAs(expectedObject));
            Assert.That(serialized.FindProperty("_addedNetworkObject").objectReferenceValue,
                Is.SameAs(expectedObject));
        }
    }
}
