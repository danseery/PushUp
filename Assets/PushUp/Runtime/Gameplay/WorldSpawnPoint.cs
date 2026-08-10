using System;
using UnityEngine;

namespace PushUp.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class WorldSpawnPoint : MonoBehaviour
    {
        [SerializeField] private string _markerId;
        [SerializeField] private SpawnDefinition _definition;
        [SerializeField] private bool _enabled = true;

        public string MarkerId => _markerId;
        public SpawnDefinition Definition => _definition;
        public bool IsEnabled => _enabled && enabled && gameObject.activeInHierarchy;
        public SpawnGroup Group => GetComponentInParent<SpawnGroup>();
        public string GroupId => Group != null ? Group.Id : "core";
        public bool SpawnAtRunStart => Group == null || Group.SpawnAtRunStart;

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(_markerId))
                _markerId = Guid.NewGuid().ToString("N");
        }
    }
}
