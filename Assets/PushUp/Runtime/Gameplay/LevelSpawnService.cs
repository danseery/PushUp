using System;
using System.Collections.Generic;
using System.Linq;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using PushUp.Networking;
using Unity.Profiling;
using UnityEngine;

namespace PushUp.Gameplay
{
    public enum NetworkPlayerSpawnStatus : byte
    {
        Rejected,
        WaitingForStartScenes,
        Spawned
    }

    /// <summary>Turns scene-authored level markers into offline or host-authoritative run instances.</summary>
    [DisallowMultipleComponent]
    public sealed class LevelSpawnService : MonoBehaviour
    {
        private sealed class SpawnedRecord
        {
            public WorldSpawnPoint Marker;
            public GameObject Instance;
            public NetworkObject NetworkObject;
        }

        private LevelLayout _layout;
        private LevelLayoutSnapshot _snapshot;
        private NetworkManager _networkManager;
        private readonly Dictionary<string, SpawnedRecord> _worldInstances = new(StringComparer.Ordinal);
        private readonly Dictionary<int, NetworkObject> _networkPlayers = new();
        private readonly Dictionary<int, int> _networkPlayerSlots = new();
        private readonly Dictionary<int, NetworkConnection> _networkPlayerConnections = new();
        private readonly Dictionary<int, NetworkConnection> _pendingNetworkPlayers = new();
        private readonly HashSet<int> _claimedPlayerSlots = new();
        private readonly HashSet<string> _activeGroups = new(StringComparer.Ordinal);
        private static readonly ProfilerMarker BeginRunSpawnMarker = new("PushUp.Spawn.BeginRun");
        private NetworkObject _serverLocalPlayer;
        private GameObject _offlinePlayer;
        private bool _networked;
        private bool _started;

        public bool IsStarted => _started;
        public bool IsNetworked => _networked;
        public int SpawnedWorldCount => _worldInstances.Count;
        public int SpawnedNetworkPlayerCount => _networkPlayers.Count;
        public int PendingNetworkPlayerCount => _pendingNetworkPlayers.Count;
        public BoulderController PrimaryBoulder { get; private set; }
        public SummitGoal Summit => _snapshot != null ? _snapshot.Summit : _layout != null ? _layout.Summit : null;
        public string LastError { get; private set; } = string.Empty;

        public void Configure(LevelLayout layout, NetworkManager networkManager)
        {
            if (_layout != layout)
                _snapshot = null;
            _layout = layout;
            _networkManager = networkManager;
        }

        public bool BeginOfflineRun()
        {
            if (!Begin(false))
                return false;
            _offlinePlayer = SpawnOfflinePlayer(ClaimNextPlayerSpawn());
            if (_offlinePlayer == null)
            {
                LastError = "Failed to create the offline player from the authored player spawn.";
                Clear();
                return false;
            }
            return true;
        }

        public bool BeginNetworkRun(bool spawnServerLocalPlayer)
        {
            if (!Begin(true))
                return false;
            if (spawnServerLocalPlayer && SpawnServerLocalPlayer() == null)
            {
                LastError = "Failed to create the server-local host player.";
                Clear();
                return false;
            }
            return true;
        }

        private bool Begin(bool networked)
        {
            using ProfilerMarker.AutoScope profilerScope = BeginRunSpawnMarker.Auto();
            LastError = string.Empty;
            if (_started)
                return true;
            if (_layout == null)
            {
                LastError = "No LevelLayout exists in the active scene.";
                return false;
            }
            if (!_layout.ValidateLayout(out string[] errors))
            {
                LastError = string.Join("\n", errors);
                return false;
            }
            _snapshot = _layout.Snapshot;
            if (networked && (_networkManager == null || !_networkManager.ServerManager.Started))
            {
                LastError = "Network spawning requires a started FishNet server.";
                return false;
            }

            _networked = networked;
            _started = true;
            WorldSpawnPoint[] initial = _snapshot.WorldSpawns
                .Where(marker => marker.IsEnabled && marker.SpawnAtRunStart)
                .OrderBy(marker => RolePriority(marker.Definition != null ? marker.Definition.Role : SpawnRole.Prop))
                .ThenBy(marker => marker.MarkerId, StringComparer.Ordinal).ToArray();
            foreach (WorldSpawnPoint marker in initial)
                _activeGroups.Add(marker.GroupId);
            if (!SpawnMarkers(initial))
            {
                Clear();
                return false;
            }
            return true;
        }

        public bool ActivateGroup(string groupId)
        {
            if (!_started || string.IsNullOrWhiteSpace(groupId))
                return false;
            string normalized = groupId.Trim();
            if (!_activeGroups.Add(normalized))
                return true;
            if (_snapshot == null || !_snapshot.Groups.Any(group => group.Id == normalized))
            {
                _activeGroups.Remove(normalized);
                LastError = $"Spawn group '{normalized}' does not exist in this level.";
                return false;
            }
            WorldSpawnPoint[] markers = _snapshot.WorldSpawns
                .Where(marker => marker.IsEnabled && marker.GroupId == normalized)
                .OrderBy(marker => RolePriority(marker.Definition != null ? marker.Definition.Role : SpawnRole.Prop))
                .ThenBy(marker => marker.MarkerId, StringComparer.Ordinal).ToArray();
            bool spawned = SpawnMarkers(markers);
            if (!spawned)
                _activeGroups.Remove(normalized);
            return spawned;
        }

        public bool IsGroupActive(string groupId) => !string.IsNullOrWhiteSpace(groupId) &&
                                                     _activeGroups.Contains(groupId.Trim());

        private bool SpawnMarkers(IEnumerable<WorldSpawnPoint> markers)
        {
            List<SpawnedRecord> created = new();
            foreach (WorldSpawnPoint marker in markers)
            {
                if (_worldInstances.ContainsKey(marker.MarkerId))
                    continue;
                if (!TrySpawnWorld(marker, out SpawnedRecord record))
                    return false;
                _worldInstances.Add(marker.MarkerId, record);
                created.Add(record);
                if (marker.Definition.Role == SpawnRole.PrimaryBoulder)
                    PrimaryBoulder = record.Instance.GetComponent<BoulderController>();
            }

            foreach (SpawnedRecord record in created)
            {
                LevelSpawnContext context = new(this, record.Marker.Definition, PrimaryBoulder, _networked);
                foreach (MonoBehaviour component in record.Instance.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (component is ILevelSpawned spawned)
                        spawned.Initialize(context);
                }
            }
            return true;
        }

        private bool TrySpawnWorld(WorldSpawnPoint marker, out SpawnedRecord record)
        {
            record = null;
            SpawnDefinition definition = marker.Definition;
            GameObject prefab = _networked ? definition.Prefab : definition.OfflinePrefab;
            if (prefab == null)
            {
                LastError = $"Spawn '{marker.name}' has no prefab for this run mode.";
                return false;
            }
            if (_networked && definition.Policy == SpawnPolicy.PlayerOwned)
            {
                LastError = $"World spawn '{marker.name}' uses a PlayerOwned definition.";
                return false;
            }
            if (!_networked && prefab.GetComponent<NetworkObject>() != null)
            {
                LastError = $"Offline spawn '{definition.DisplayName}' still contains FishNet components. " +
                            "Assign a networking-free offline override in its SpawnDefinition.";
                return false;
            }

            GameObject instance = Instantiate(prefab, marker.transform.position, marker.transform.rotation);
            instance.name = $"{definition.DisplayName} ({marker.name})";
            GameplayLayers.ApplyRole(instance, definition.Role);
            NetworkObject networkObject = instance.GetComponent<NetworkObject>();
            if (_networked && definition.Policy == SpawnPolicy.Replicated)
            {
                if (networkObject == null)
                {
                    DestroyGameObject(instance);
                    LastError = $"Replicated definition '{definition.DisplayName}' has no NetworkObject.";
                    return false;
                }
                _networkManager.ServerManager.Spawn(networkObject);
            }
            record = new SpawnedRecord { Marker = marker, Instance = instance, NetworkObject = networkObject };
            return true;
        }

        /// <summary>
        /// Ensures one player exists for an authenticated connection. FishNet does not consider a
        /// connection ready for spawned gameplay objects until its start scenes have loaded, so a
        /// late join is queued until <see cref="NetworkConnection.OnLoadedStartScenes"/> fires.
        /// Repeated authentication/readiness notifications are deliberately idempotent.
        /// </summary>
        public NetworkPlayerSpawnStatus EnsureNetworkPlayer(NetworkConnection connection,
            out NetworkObject player)
        {
            player = null;
            LastError = string.Empty;
            if (!_started || !_networked || !IsConnectionReadyForRun(connection))
            {
                LastError = "Player spawning requires an active, authenticated connection and network run.";
                return NetworkPlayerSpawnStatus.Rejected;
            }

            int clientId = connection.ClientId;
            if (_networkPlayers.TryGetValue(clientId, out NetworkObject existing))
            {
                _networkPlayerConnections.TryGetValue(clientId, out NetworkConnection owner);
                bool sameConnection = ReferenceEquals(owner, connection);
                bool healthyPlayer = existing != null && existing.IsSpawned;
                if (sameConnection && healthyPlayer)
                {
                    player = existing;
                    return NetworkPlayerSpawnStatus.Spawned;
                }

                // A transport may recycle an ID after the old connection has stopped. A missed or
                // delayed disconnect notification must not permanently consume that player's slot.
                bool staleLease = owner == null || !owner.IsActive;
                if (!sameConnection && !staleLease)
                {
                    LastError = $"Connection ID {clientId} is still leased to an active connection.";
                    return NetworkPlayerSpawnStatus.Rejected;
                }
                RemoveNetworkPlayerLease(clientId, owner, true);
            }

            if (_pendingNetworkPlayers.TryGetValue(clientId, out NetworkConnection pending))
            {
                if (!ReferenceEquals(pending, connection))
                {
                    if (pending != null && pending.IsActive)
                    {
                        LastError = $"Connection ID {clientId} already has a pending active connection.";
                        return NetworkPlayerSpawnStatus.Rejected;
                    }
                    CancelPendingNetworkPlayer(clientId, pending);
                }
                else if (!connection.LoadedStartScenes(true))
                {
                    return NetworkPlayerSpawnStatus.WaitingForStartScenes;
                }
                else
                {
                    // Recover if a scene-ready event was missed by script disable/enable ordering.
                    CancelPendingNetworkPlayer(clientId, connection);
                }
            }

            if (!connection.LoadedStartScenes(true))
            {
                _pendingNetworkPlayers.Add(clientId, connection);
                connection.OnLoadedStartScenes += OnConnectionLoadedStartScenes;
                return NetworkPlayerSpawnStatus.WaitingForStartScenes;
            }

            return TrySpawnNetworkPlayerNow(connection, out player)
                ? NetworkPlayerSpawnStatus.Spawned
                : NetworkPlayerSpawnStatus.Rejected;
        }

        /// <summary>Compatibility wrapper for existing callers which only need an already-spawned object.</summary>
        public NetworkObject SpawnNetworkPlayer(NetworkConnection connection)
        {
            EnsureNetworkPlayer(connection, out NetworkObject player);
            return player;
        }

        private bool TrySpawnNetworkPlayerNow(NetworkConnection connection, out NetworkObject player)
        {
            player = null;
            if (!IsConnectionReadyForRun(connection) || !connection.LoadedStartScenes(true))
                return false;

            PlayerSpawnPoint marker = ClaimNextPlayerSpawn();
            if (marker == null)
                return false;
            if (!TryGetNetworkPlayerPrefab(marker, out NetworkObject prefab))
            {
                _claimedPlayerSlots.Remove(marker.Slot);
                LastError = $"Player spawn slot {marker.Slot} has no network player prefab.";
                return false;
            }

            int clientId = connection.ClientId;
            NetworkObject created = Instantiate(prefab, marker.transform.position, marker.transform.rotation);
            GameplayLayers.ApplyRole(created.gameObject, SpawnRole.Player);
            _networkPlayers.Add(clientId, created);
            _networkPlayerSlots.Add(clientId, marker.Slot);
            _networkPlayerConnections.Add(clientId, connection);
            try
            {
                _networkManager.ServerManager.Spawn(created, connection);
                if (!created.IsSpawned)
                    throw new InvalidOperationException("FishNet did not initialize the player NetworkObject.");
                AssignPlayerIdentity(created, connection);
                player = created;
                return true;
            }
            catch (Exception exception)
            {
                _networkPlayers.Remove(clientId);
                _networkPlayerSlots.Remove(clientId);
                _networkPlayerConnections.Remove(clientId);
                _claimedPlayerSlots.Remove(marker.Slot);
                DestroyGameObject(created.gameObject);
                LastError = $"Could not spawn the player for connection {clientId}: {exception.Message}";
                Debug.LogException(exception);
                return false;
            }
        }

        public NetworkObject SpawnServerLocalPlayer()
        {
            if (!_started || !_networked || _serverLocalPlayer != null)
                return _serverLocalPlayer;
            PlayerSpawnPoint marker = ClaimNextPlayerSpawn();
            if (marker == null || !TryGetNetworkPlayerPrefab(marker, out NetworkObject prefab))
                return null;
            _serverLocalPlayer = Instantiate(prefab, marker.transform.position, marker.transform.rotation);
            GameplayLayers.ApplyRole(_serverLocalPlayer.gameObject, SpawnRole.Player);
            _serverLocalPlayer.GetComponent<PlayerMotor>()?.ConfigureAsServerLocalPlayer();
            _networkManager.ServerManager.Spawn(_serverLocalPlayer);
            AssignPlayerIdentity(_serverLocalPlayer, null);
            return _serverLocalPlayer;
        }

        private void AssignPlayerIdentity(NetworkObject player, NetworkConnection connection)
        {
            if (player == null)
                return;
            SteamLobbyAuthenticator authenticator = _networkManager != null
                ? _networkManager.GetComponent<SteamLobbyAuthenticator>()
                : null;
            SteamLobbyAuthenticator.AuthenticatedPlayerIdentity identity;
            if (connection != null && authenticator != null &&
                authenticator.TryGetPlayerIdentity(connection, out identity))
            {
                player.GetComponent<PlayerMotor>()?.SetServerPlayerIdentity(identity.SteamId,
                    identity.DisplayName);
                return;
            }
            identity = authenticator != null
                ? authenticator.GetServerLocalIdentity()
                : new SteamLobbyAuthenticator.AuthenticatedPlayerIdentity(null, 0UL,
                    connection == null ? "Host" : $"Player {connection.ClientId + 1}");
            player.GetComponent<PlayerMotor>()?.SetServerPlayerIdentity(identity.SteamId, identity.DisplayName);
        }

        public void ReleaseNetworkPlayer(NetworkConnection connection)
        {
            if (connection == null)
                return;

            int clientId = connection.ClientId;
            CancelPendingNetworkPlayer(clientId, connection);
            RemoveNetworkPlayerLease(clientId, connection, true);
        }

        public void Clear()
        {
            CancelAllPendingNetworkPlayers();
            foreach (SpawnedRecord record in _worldInstances.Values)
                DestroySpawned(record.Instance, record.NetworkObject, record.Marker.Definition.Policy);
            foreach (NetworkObject player in _networkPlayers.Values)
                DestroyNetworkObject(player);
            DestroyNetworkObject(_serverLocalPlayer);
            if (_offlinePlayer != null)
                DestroyGameObject(_offlinePlayer);

            _worldInstances.Clear();
            _networkPlayers.Clear();
            _networkPlayerSlots.Clear();
            _networkPlayerConnections.Clear();
            _claimedPlayerSlots.Clear();
            _activeGroups.Clear();
            _serverLocalPlayer = null;
            _offlinePlayer = null;
            PrimaryBoulder = null;
            _started = false;
            _networked = false;
        }

        private void OnDestroy() => CancelAllPendingNetworkPlayers();

        private void OnConnectionLoadedStartScenes(NetworkConnection connection, bool asServer)
        {
            if (!asServer || connection == null)
                return;
            if (!_pendingNetworkPlayers.TryGetValue(connection.ClientId, out NetworkConnection pending) ||
                !ReferenceEquals(pending, connection))
                return;

            CancelPendingNetworkPlayer(connection.ClientId, connection);
            if (!TrySpawnNetworkPlayerNow(connection, out _))
            {
                string reason = string.IsNullOrWhiteSpace(LastError)
                    ? "No player spawn slot was available after loading the hill."
                    : LastError;
                connection.Kick(FishNet.Managing.Server.KickReason.UnexpectedProblem, log: reason);
            }
        }

        private void CancelPendingNetworkPlayer(int clientId, NetworkConnection connection)
        {
            if (connection == null ||
                !_pendingNetworkPlayers.TryGetValue(clientId, out NetworkConnection pending) ||
                !ReferenceEquals(pending, connection))
                return;
            _pendingNetworkPlayers.Remove(clientId);
            connection.OnLoadedStartScenes -= OnConnectionLoadedStartScenes;
        }

        private void CancelAllPendingNetworkPlayers()
        {
            foreach (NetworkConnection pending in _pendingNetworkPlayers.Values)
            {
                if (pending != null)
                    pending.OnLoadedStartScenes -= OnConnectionLoadedStartScenes;
            }
            _pendingNetworkPlayers.Clear();
        }

        private bool RemoveNetworkPlayerLease(int clientId, NetworkConnection expectedConnection,
            bool destroyPlayer)
        {
            if (_networkPlayerConnections.TryGetValue(clientId, out NetworkConnection owner) &&
                expectedConnection != null && !ReferenceEquals(owner, expectedConnection))
                return false;
            if (!_networkPlayers.Remove(clientId, out NetworkObject player))
                return false;

            _networkPlayerConnections.Remove(clientId);
            if (_networkPlayerSlots.Remove(clientId, out int slot))
                _claimedPlayerSlots.Remove(slot);
            if (!destroyPlayer || player == null)
                return true;
            if (_networkManager != null && _networkManager.ServerManager.Started && player.IsSpawned)
                _networkManager.ServerManager.Despawn(player);
            else
                DestroyGameObject(player.gameObject);
            return true;
        }

        public static bool IsConnectionReadyForRun(NetworkConnection connection) =>
            connection != null && connection.IsActive && connection.IsAuthenticated;

        private void DestroySpawned(GameObject instance, NetworkObject networkObject, SpawnPolicy policy)
        {
            if (instance == null)
                return;
            if (_networked && policy == SpawnPolicy.Replicated && networkObject != null &&
                _networkManager != null && _networkManager.ServerManager.Started)
                _networkManager.ServerManager.Despawn(networkObject);
            else
                DestroyGameObject(instance);
        }

        private void DestroyNetworkObject(NetworkObject networkObject)
        {
            if (networkObject == null)
                return;
            if (_networkManager != null && _networkManager.ServerManager.Started)
                _networkManager.ServerManager.Despawn(networkObject);
            else
                DestroyGameObject(networkObject.gameObject);
        }

        private PlayerSpawnPoint ClaimNextPlayerSpawn()
        {
            if (_snapshot == null)
                return null;
            PlayerSpawnPoint marker = _snapshot.PlayerSpawns.FirstOrDefault(point => !_claimedPlayerSlots.Contains(point.Slot));
            if (marker != null)
                _claimedPlayerSlots.Add(marker.Slot);
            else
                LastError = "No unclaimed player spawn point remains.";
            return marker;
        }

        private static bool TryGetNetworkPlayerPrefab(PlayerSpawnPoint marker, out NetworkObject prefab)
        {
            prefab = marker != null && marker.Definition != null && marker.Definition.Prefab != null
                ? marker.Definition.Prefab.GetComponent<NetworkObject>()
                : null;
            return prefab != null;
        }

        private static int RolePriority(SpawnRole role) => role switch
        {
            SpawnRole.PrimaryBoulder => 0,
            SpawnRole.Actor => 1,
            SpawnRole.Prop => 2,
            SpawnRole.Powerup => 3,
            _ => 4
        };

        private static void DestroyGameObject(GameObject target)
        {
            if (target == null)
                return;
            if (Application.isPlaying)
                Destroy(target);
            else
                DestroyImmediate(target);
        }

        private GameObject SpawnOfflinePlayer(PlayerSpawnPoint marker)
        {
            if (marker == null || marker.Definition == null || marker.Definition.Prefab == null)
                return null;
            if (marker.Definition.OfflinePrefab != marker.Definition.Prefab)
            {
                GameObject offline = Instantiate(marker.Definition.OfflinePrefab, marker.transform.position,
                    marker.transform.rotation);
                GameplayLayers.ApplyRole(offline, SpawnRole.Player);
                return offline;
            }
            return CreateStandalonePlayer(marker.transform.position, marker.transform.rotation,
                marker.Definition.Prefab.GetComponent<NetworkObject>());
        }

        public static GameObject CreateStandalonePlayer(Vector3 position, Quaternion rotation, NetworkObject prefab)
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            player.name = "Offline Player";
            GameplayLayers.ApplyRole(player, SpawnRole.Player);
            player.transform.SetPositionAndRotation(position, rotation);
            player.transform.localScale = Vector3.one;
            CapsuleCollider sourceCapsule = prefab != null ? prefab.GetComponent<CapsuleCollider>() : null;
            CapsuleCollider capsule = player.GetComponent<CapsuleCollider>();
            if (sourceCapsule != null)
            {
                capsule.radius = sourceCapsule.radius;
                capsule.height = sourceCapsule.height;
                capsule.center = sourceCapsule.center;
                capsule.material = sourceCapsule.sharedMaterial;
            }
            Renderer sourceRenderer = prefab != null ? prefab.GetComponent<Renderer>() : null;
            if (sourceRenderer != null)
                player.GetComponent<Renderer>().sharedMaterial = sourceRenderer.sharedMaterial;
            Rigidbody body = player.AddComponent<Rigidbody>();
            PlayerPhysics.ConfigureBody(body, capsule, sourceCapsule != null ? sourceCapsule.sharedMaterial : null);
            player.AddComponent<PlayerInputReader>();
            StandalonePlayerController controller = player.AddComponent<StandalonePlayerController>();
            controller.EnsureInitialized();
            controller.CopyConfigurationFrom(prefab != null ? prefab.GetComponent<PlayerMotor>() : null);

            ActiveRagdollPuppet templatePuppet = prefab != null ? prefab.GetComponent<ActiveRagdollPuppet>() : null;
            templatePuppet?.CreateStandaloneClone(player, controller.CameraPivot, player.GetComponent<Renderer>());
            PunchImpactFeedback impact = player.AddComponent<PunchImpactFeedback>();
            PunchImpactFeedback templateImpact = prefab != null ? prefab.GetComponent<PunchImpactFeedback>() : null;
            impact.Configure(templateImpact != null ? templateImpact.ImpactPrefab : null);
            PlayerInteraction interaction = player.AddComponent<PlayerInteraction>();
            interaction.CopyConfigurationFrom(prefab != null ? prefab.GetComponent<PlayerInteraction>() : null);
            GameplayLayers.ApplyRole(player, SpawnRole.Player);
            return player;
        }
    }
}
