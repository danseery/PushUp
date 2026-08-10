using FishNet.Object;
using UnityEngine;

namespace PushUp.Gameplay
{
    public enum SpawnRole
    {
        Player,
        PrimaryBoulder,
        Actor,
        Powerup,
        Prop
    }

    public enum SpawnPolicy
    {
        PlayerOwned,
        Replicated,
        HostLocal
    }

    [CreateAssetMenu(menuName = "PushUp/Spawn Definition", fileName = "SpawnDefinition")]
    public sealed class SpawnDefinition : ScriptableObject
    {
        [SerializeField] private string _id;
        [SerializeField] private string _displayName;
        [SerializeField] private GameObject _prefab;
        [SerializeField] private GameObject _offlineOverride;
        [SerializeField] private SpawnRole _role;
        [SerializeField] private SpawnPolicy _policy;

        public string Id => _id;
        public string DisplayName => string.IsNullOrWhiteSpace(_displayName) ? name : _displayName;
        public GameObject Prefab => _prefab;
        public GameObject OfflinePrefab => _offlineOverride != null ? _offlineOverride : _prefab;
        public bool HasOfflineOverride => _offlineOverride != null;
        public SpawnRole Role => _role;
        public SpawnPolicy Policy => _policy;

        public bool IsValid(out string error)
        {
            if (string.IsNullOrWhiteSpace(_id))
            {
                error = $"Spawn definition '{name}' has no stable ID.";
                return false;
            }
            if (_prefab == null)
            {
                error = $"Spawn definition '{DisplayName}' has no prefab.";
                return false;
            }
            if (_role == SpawnRole.Player && _policy != SpawnPolicy.PlayerOwned)
            {
                error = $"Player definition '{DisplayName}' must use PlayerOwned policy.";
                return false;
            }
            if (_role != SpawnRole.Player && _policy == SpawnPolicy.PlayerOwned)
            {
                error = $"Non-player definition '{DisplayName}' cannot use PlayerOwned policy.";
                return false;
            }
            if (_role != SpawnRole.Player && _prefab.GetComponent<NetworkObject>() != null &&
                _offlineOverride == null)
            {
                error = $"Networked definition '{DisplayName}' needs a networking-free offline override.";
                return false;
            }
            if (_offlineOverride != null && _offlineOverride.GetComponent<NetworkObject>() != null)
            {
                error = $"Offline override for '{DisplayName}' still contains a NetworkObject.";
                return false;
            }
            error = string.Empty;
            return true;
        }
    }

    public readonly struct LevelSpawnContext
    {
        public readonly LevelSpawnService Service;
        public readonly SpawnDefinition Definition;
        public readonly BoulderController PrimaryBoulder;
        public readonly bool Networked;

        public LevelSpawnContext(LevelSpawnService service, SpawnDefinition definition,
            BoulderController primaryBoulder, bool networked)
        {
            Service = service;
            Definition = definition;
            PrimaryBoulder = primaryBoulder;
            Networked = networked;
        }
    }

    public interface ILevelSpawned
    {
        void Initialize(LevelSpawnContext context);
    }
}
