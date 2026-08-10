using FishNet.Object;
using UnityEngine;

namespace PushUp.Gameplay
{
    public enum PowerupKind
    {
        Speed,
        BoulderAssist,
        Anchor
    }

    [RequireComponent(typeof(Collider))]
    public sealed class PowerupPickup : MonoBehaviour, ILevelSpawned
    {
        [SerializeField] private PowerupKind _kind;
        [SerializeField] private float _duration = 20f;

        private BoulderController _boulder;
        private bool _consumed;
        private NetworkObject _networkObject;

        private void Awake() => _networkObject = GetComponent<NetworkObject>();

        public void Initialize(LevelSpawnContext context) => _boulder = context.PrimaryBoulder;

        public void Initialize(BoulderController boulder, PowerupKind kind)
        {
            _boulder = boulder;
            _kind = kind;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_consumed || !ShouldProcessTrigger(_networkObject != null,
                    _networkObject != null && _networkObject.IsSpawned,
                    _networkObject != null && _networkObject.IsServerStarted))
                return;
            if (other.TryGetComponent(out PlayerMotor networkPlayer))
            {
                Consume(networkPlayer, null);
                return;
            }
            if (other.TryGetComponent(out StandalonePlayerController offlinePlayer))
                Consume(null, offlinePlayer);
        }

        private void Consume(PlayerMotor networkPlayer, StandalonePlayerController offlinePlayer)
        {
            _consumed = true;
            switch (_kind)
            {
                case PowerupKind.Speed when networkPlayer != null:
                    networkPlayer.ApplySpeedBoost(1.35f, _duration);
                    break;
                case PowerupKind.Speed:
                    offlinePlayer?.ApplySpeedBoost(1.35f, _duration);
                    break;
                case PowerupKind.BoulderAssist:
                    _boulder?.ApplyTeamAssist(_duration);
                    break;
                case PowerupKind.Anchor when networkPlayer != null:
                    networkPlayer.GrantAnchor();
                    break;
                case PowerupKind.Anchor:
                    offlinePlayer.GetComponent<PlayerInteraction>()?.GiveAnchor();
                    break;
            }
            if (_networkObject != null && _networkObject.IsSpawned)
                _networkObject.Despawn();
            else
                gameObject.SetActive(false);
        }

        public static bool ShouldProcessTrigger(bool hasNetworkObject, bool isSpawned, bool isServerStarted) =>
            !hasNetworkObject || !isSpawned || isServerStarted;
    }

    /// <summary>Local-only counterpart to the networked pickup prefab.</summary>
    [RequireComponent(typeof(Collider))]
    public sealed class StandalonePowerupPickup : MonoBehaviour
    {
        private PowerupKind _kind;
        private BoulderController _boulder;
        private bool _consumed;

        public void Initialize(BoulderController boulder, PowerupKind kind)
        {
            _boulder = boulder;
            _kind = kind;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_consumed || !other.TryGetComponent(out StandalonePlayerController player))
                return;

            _consumed = true;
            switch (_kind)
            {
                case PowerupKind.Speed:
                    player.ApplySpeedBoost(1.35f, 20f);
                    break;
                case PowerupKind.BoulderAssist:
                    _boulder?.ApplyTeamAssist(30f);
                    break;
                case PowerupKind.Anchor:
                    player.GetComponent<PlayerInteraction>()?.GiveAnchor();
                    break;
            }

            GetComponent<Collider>().enabled = false;
            GetComponent<Renderer>().enabled = false;
        }
    }
}
