using UnityEngine;

namespace PushUp.Gameplay
{
    /// <summary>Single source for the project's physics/query layer contract.</summary>
    public static class GameplayLayers
    {
        public const int Terrain = 8;
        public const int Player = 9;
        public const int Boulder = 10;
        public const int Actor = 11;
        public const int Interactable = 12;
        public const int Pickup = 13;
        public const int GameplayTrigger = 14;
        public const int Presentation = 15;
        public const int RemotePlayerProxy = 16;
        public const int LegacyDefault = 0;

        public const int GroundQueryMask = (1 << LegacyDefault) | (1 << Terrain) | (1 << Boulder);
        public const int InteractionQueryMask = (1 << LegacyDefault) | (1 << Terrain) | (1 << Player) |
                                                (1 << Boulder) | (1 << Actor) | (1 << Interactable) |
                                                (1 << Pickup) | (1 << RemotePlayerProxy);
        public const int BlockingQueryMask = InteractionQueryMask & ~(1 << Pickup);

        private static bool _staticTerrainDefaultsApplied;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeState()
        {
            _staticTerrainDefaultsApplied = false;
            // A remote owner is represented by a kinematic query proxy on the host. It must remain
            // punch/grab raycastable, but it must not inject infinite-mass contact impulses into the
            // host-authoritative boulder or other actors.
            Physics.IgnoreLayerCollision(RemotePlayerProxy, Player, true);
            Physics.IgnoreLayerCollision(RemotePlayerProxy, RemotePlayerProxy, true);
            Physics.IgnoreLayerCollision(RemotePlayerProxy, Boulder, true);
            Physics.IgnoreLayerCollision(RemotePlayerProxy, Actor, true);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void ApplyStaticTerrainDefaults()
        {
            if (_staticTerrainDefaultsApplied)
                return;
            _staticTerrainDefaultsApplied = true;
            Collider[] colliders = Object.FindObjectsByType<Collider>(FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (Collider collider in colliders)
            {
                if (ShouldPromoteToStaticTerrain(collider))
                    collider.gameObject.layer = Terrain;
            }
        }

        public static bool ShouldPromoteToStaticTerrain(Collider collider) => collider != null &&
            collider.gameObject.layer == LegacyDefault && !collider.isTrigger && collider.attachedRigidbody == null;

        public static int ForRole(SpawnRole role) => role switch
        {
            SpawnRole.Player => Player,
            SpawnRole.PrimaryBoulder => Boulder,
            SpawnRole.Actor => Actor,
            SpawnRole.Powerup => Pickup,
            _ => Interactable
        };

        /// <summary>Applies an authored role once at spawn; gameplay hot paths never search by hierarchy name.</summary>
        public static void ApplyRole(GameObject root, SpawnRole role)
        {
            if (root == null)
                return;
            int layer = ForRole(role);
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                // Presentation-only children (for example the player's jointed world arms)
                // deliberately use collider exclusion masks. Spawning the actor must not turn
                // those limbs back into gameplay colliders or independent interaction targets.
                if (child != root.transform && child.gameObject.layer == Presentation)
                    continue;
                child.gameObject.layer = layer;
            }
        }
    }
}
