using UnityEngine;

namespace PushUp.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class SpawnGroup : MonoBehaviour
    {
        [SerializeField] private string _id = "core";
        [SerializeField] private bool _spawnAtRunStart = true;

        public string Id => string.IsNullOrWhiteSpace(_id) ? "core" : _id.Trim();
        public bool SpawnAtRunStart => _spawnAtRunStart;
    }
}
