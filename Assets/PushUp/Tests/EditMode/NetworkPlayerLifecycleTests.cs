using System.Collections.Generic;
using System.Reflection;
using FishNet.Connection;
using FishNet.Object;
using NUnit.Framework;
using PushUp.Gameplay;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PushUp.Tests
{
    public sealed class NetworkPlayerLifecycleTests
    {
        private const string MountainScene = "Assets/PushUp/Scenes/Mountain.unity";

        [Test]
        public void UnloadedAuthenticatedConnectionIsQueuedExactlyOnceAndDisconnectIsIdempotent()
        {
            LevelSpawnService service = CreateStartedNetworkService();
            NetworkConnection connection = CreateAuthenticatedConnection(21);
            try
            {
                Assert.That(service.EnsureNetworkPlayer(connection, out _),
                    Is.EqualTo(NetworkPlayerSpawnStatus.WaitingForStartScenes));
                Assert.That(service.EnsureNetworkPlayer(connection, out _),
                    Is.EqualTo(NetworkPlayerSpawnStatus.WaitingForStartScenes));
                Assert.That(service.PendingNetworkPlayerCount, Is.EqualTo(1));

                service.ReleaseNetworkPlayer(connection);
                service.ReleaseNetworkPlayer(connection);
                Assert.That(service.PendingNetworkPlayerCount, Is.Zero,
                    "duplicate transport stop callbacks must be harmless");
                Assert.That(service.IsStarted, Is.True, "a client leaving must not end the host run");
            }
            finally
            {
                service.Clear();
            }
        }

        [Test]
        public void StalePendingConnectionCannotRemoveOrBlockItsReplacement()
        {
            LevelSpawnService service = CreateStartedNetworkService();
            NetworkConnection stale = CreateAuthenticatedConnection(22);
            NetworkConnection replacement = CreateAuthenticatedConnection(22);
            try
            {
                Assert.That(service.EnsureNetworkPlayer(stale, out _),
                    Is.EqualTo(NetworkPlayerSpawnStatus.WaitingForStartScenes));
                // FishNet resets the old connection object after delivering the stop callback.
                // Simulate a callback which was missed until after that reset.
                stale.ClientId = NetworkConnection.UNSET_CLIENTID_VALUE;

                Assert.That(service.EnsureNetworkPlayer(replacement, out _),
                    Is.EqualTo(NetworkPlayerSpawnStatus.WaitingForStartScenes));
                Assert.That(service.PendingNetworkPlayerCount, Is.EqualTo(1));

                service.ReleaseNetworkPlayer(stale);
                Assert.That(service.PendingNetworkPlayerCount, Is.EqualTo(1),
                    "a late stop from the old connection must not cancel the replacement");
                service.ReleaseNetworkPlayer(replacement);
                Assert.That(service.PendingNetworkPlayerCount, Is.Zero);
            }
            finally
            {
                service.Clear();
            }
        }

        [Test]
        public void ReleasedPlayerSlotIsImmediatelyReusableWithoutClearingWorldState()
        {
            LevelSpawnService service = CreateStartedNetworkService();
            NetworkConnection connection = CreateAuthenticatedConnection(23);
            MethodInfo claim = typeof(LevelSpawnService).GetMethod("ClaimNextPlayerSpawn",
                BindingFlags.Instance | BindingFlags.NonPublic);
            PlayerSpawnPoint first = (PlayerSpawnPoint)claim!.Invoke(service, null);
            GameObject playerObject = new("Lifecycle Test Player");
            NetworkObject player = playerObject.AddComponent<NetworkObject>();

            GetField<Dictionary<int, NetworkObject>>(service, "_networkPlayers").Add(23, player);
            GetField<Dictionary<int, int>>(service, "_networkPlayerSlots").Add(23, first.Slot);
            GetField<Dictionary<int, NetworkConnection>>(service, "_networkPlayerConnections").Add(23, connection);

            try
            {
                service.ReleaseNetworkPlayer(connection);
                service.ReleaseNetworkPlayer(connection);

                PlayerSpawnPoint reclaimed = (PlayerSpawnPoint)claim.Invoke(service, null);
                Assert.That(reclaimed.Slot, Is.EqualTo(first.Slot));
                Assert.That(reclaimed.transform.position, Is.EqualTo(first.transform.position),
                    "a returning player should use the next available authored base spawn");
                Assert.That(service.SpawnedNetworkPlayerCount, Is.Zero);
                Assert.That(service.IsStarted, Is.True);
            }
            finally
            {
                service.Clear();
                if (playerObject != null)
                    Object.DestroyImmediate(playerObject);
            }
        }

        private static LevelSpawnService CreateStartedNetworkService()
        {
            EditorSceneManager.OpenScene(MountainScene);
            LevelLayout layout = Object.FindFirstObjectByType<LevelLayout>(FindObjectsInactive.Include);
            LevelSpawnService service = Object.FindFirstObjectByType<LevelSpawnService>(FindObjectsInactive.Include);
            SetField(service, "_snapshot", layout.Snapshot);
            SetField(service, "_started", true);
            SetField(service, "_networked", true);
            return service;
        }

        private static NetworkConnection CreateAuthenticatedConnection(int clientId)
        {
            NetworkConnection connection = new() { ClientId = clientId };
            SetPrivateProperty(connection, "IsAuthenticated", true);
            return connection;
        }

        private static void SetPrivateProperty(object owner, string name, object value)
        {
            PropertyInfo property = owner.GetType().GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            property!.SetValue(owner, value);
        }

        private static T GetField<T>(object owner, string name) =>
            (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);

        private static void SetField(object owner, string name, object value) =>
            owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    }
}
