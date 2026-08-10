using FishNet.Object;
using UnityEngine;

namespace PushUp.Gameplay
{
    [RequireComponent(typeof(Rigidbody))]
    public sealed class BoulderController : MonoBehaviour
    {
        public const float DefaultMass = 150f;
        public const float AssistedMass = 85f;

        [SerializeField] private float _baseMass = DefaultMass;
        [SerializeField] private float _assistMass = AssistedMass;
        [SerializeField] private PhysicsMaterial _physicsMaterial;

        private static PhysicsMaterial _fallbackMaterial;

        private Rigidbody _body;
        private bool _anchored;
        private Vector3 _spawnPosition;
        private Quaternion _spawnRotation;
        private int _assistTicksRemaining;
        private NetworkObject _networkObject;
        private bool _initialized;
        private bool _resetRequested;

        public bool IsAnchored => _anchored;
        public Rigidbody Body
        {
            get
            {
                EnsureInitialized();
                return _body;
            }
        }
        public float BaseMass => _baseMass;
        public float CurrentMass => _body != null ? _body.mass : _baseMass;
        public int AssistTicksRemaining => _assistTicksRemaining;
        public bool HasSimulationAuthority => _networkObject == null || !_networkObject.IsSpawned ||
                                              _networkObject.IsServerStarted;
        public Transform PresentationRoot
        {
            get
            {
                BoulderNetworkState networkState = GetComponent<BoulderNetworkState>();
                return networkState != null ? networkState.PresentationRoot : transform;
            }
        }

        private void Awake() => EnsureInitialized();

        private void EnsureInitialized()
        {
            if (_initialized)
                return;
            _initialized = true;
            _body = GetComponent<Rigidbody>();
            _networkObject = GetComponent<NetworkObject>();
            ConfigureBody(_body, GetComponent<Collider>(), _baseMass, _physicsMaterial);
            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;
        }

        public static void ConfigureBody(Rigidbody body, Collider collider, float mass = DefaultMass,
            PhysicsMaterial physicsMaterial = null)
        {
            body.mass = mass;
            body.linearDamping = 0.08f;
            body.angularDamping = 0.3f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.maxAngularVelocity = 12f;
            body.solverIterations = 12;
            body.solverVelocityIterations = 4;

            if (collider != null)
            {
                _fallbackMaterial ??= new PhysicsMaterial("PushUp Boulder Physics Fallback")
                {
                    dynamicFriction = 0.52f,
                    staticFriction = 0.6f,
                    bounciness = 0f,
                    frictionCombine = PhysicsMaterialCombine.Average,
                    bounceCombine = PhysicsMaterialCombine.Minimum
                };
                collider.material = physicsMaterial != null ? physicsMaterial : _fallbackMaterial;
            }
        }

        private void Update()
        {
            EnsureInitialized();
            if (HasSimulationAuthority && transform.position.y < -25f)
                ResetToSpawn();
        }

        private void FixedUpdate()
        {
            EnsureInitialized();
            if (!HasSimulationAuthority || (_networkObject != null && _networkObject.IsSpawned))
                return;
            SimulateAuthorityTick();
        }

        /// <summary>Runs exactly once per authority physics tick. Returns true when a queued reset teleported.</summary>
        public bool SimulateAuthorityTick()
        {
            EnsureInitialized();
            if (!HasSimulationAuthority)
                return false;
            bool reset = false;
            if (_resetRequested)
            {
                _resetRequested = false;
                ApplyResetNow();
                reset = true;
            }
            if (_assistTicksRemaining > 0)
            {
                _assistTicksRemaining--;
                if (_assistTicksRemaining == 0)
                    _body.mass = _baseMass;
            }
            return reset;
        }

        public void ToggleAnchor()
        {
            EnsureInitialized();
            if (!HasSimulationAuthority)
                return;
            _anchored = !_anchored;
            _body.isKinematic = _anchored;
            if (_anchored)
            {
                _body.linearVelocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }
        }

        public void ApplyTeamAssist(float seconds)
        {
            EnsureInitialized();
            if (!HasSimulationAuthority)
                return;
            _body.mass = _assistMass;
            _assistTicksRemaining = PlayerPhysics.DurationToTicks(seconds, Time.fixedDeltaTime);
        }

        public void ResetToSpawn()
        {
            EnsureInitialized();
            if (!HasSimulationAuthority)
                return;
            if (_networkObject != null && _networkObject.IsSpawned)
            {
                _resetRequested = true;
                return;
            }
            ApplyResetNow();
        }

        private void ApplyResetNow()
        {
            _body.isKinematic = false;
            _anchored = false;
            _assistTicksRemaining = 0;
            _body.mass = _baseMass;
            transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);
            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
        }

    }
}
