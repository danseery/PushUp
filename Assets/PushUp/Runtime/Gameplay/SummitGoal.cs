using System;
using UnityEngine;

namespace PushUp.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class SummitGoal : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float _radius = 4f;

        public event Action<BoulderController> BoulderEntered;

        public float Radius => Mathf.Max(0.1f, _radius);
        public bool Contains(Vector3 position) => Vector3.Distance(transform.position, position) <= Radius;

        private void Awake() => EnsureTrigger();

        private void OnTriggerEnter(Collider other)
        {
            BoulderController boulder = other != null ? other.GetComponentInParent<BoulderController>() : null;
            if (boulder != null)
                BoulderEntered?.Invoke(boulder);
        }

        private void EnsureTrigger()
        {
            gameObject.layer = GameplayLayers.GameplayTrigger;
            SphereCollider trigger = GetComponent<SphereCollider>();
            if (trigger == null)
                trigger = gameObject.AddComponent<SphereCollider>();
            trigger.isTrigger = true;
            trigger.radius = Radius;
        }
    }
}
