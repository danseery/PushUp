using System;
using UnityEngine;

namespace PushUp.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class PlayerSpawnPoint : MonoBehaviour
    {
        [SerializeField] private string _markerId;
        [SerializeField, Range(0, 3)] private int _slot;
        [SerializeField] private SpawnDefinition _definition;

        public string MarkerId => _markerId;
        public int Slot => _slot;
        public SpawnDefinition Definition => _definition;

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(_markerId))
                _markerId = Guid.NewGuid().ToString("N");
            _slot = Mathf.Clamp(_slot, 0, 3);
        }
    }
}
