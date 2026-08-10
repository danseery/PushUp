using FishNet.Object;
using UnityEngine;

namespace PushUp.Gameplay
{
    /// <summary>
    /// Offline-capable physics punching bag. Networked instances simulate only on the server.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    public sealed class TrainingDummy : MonoBehaviour
    {
        public const float Mass = 55f;
        public const float MaximumStandingTiltDegrees = 30f;
        public const float KnockedDownDot = 0.8660254f;
        public const float GetUpDelay = 1.35f;
        public const float MaximumUprightAcceleration = 55f;
        public const float ArmAngularDamping = 1.8f;
        public const float ArmPoseSpring = 35f;
        public const float ArmPoseDamper = 7f;
        public const float ArmPoseMaximumForce = 140f;
        public const float PhysicalLinearDamping = 0.12f;
        public const float PhysicalAngularDamping = 0.2f;
        public const float MaximumAngularVelocity = 18f;
        public const float CenterOfMassY = -0.38f;

        [SerializeField] private float _standingSpring = 32f;
        [SerializeField] private float _getUpSpring = 42f;
        [SerializeField] private float _angularDamping = 7.5f;
        [SerializeField] private float _maximumUprightAcceleration = MaximumUprightAcceleration;

        private Rigidbody _body;
        private bool _knockedDown;
        private bool _simulationEnabled = true;
        private float _knockedDownAt;
        private float _lastImpactAt;
        private Rigidbody[] _simulationBodies;
        private CollisionDetectionMode[] _simulationCollisionModes;
        private RigidbodyInterpolation[] _simulationInterpolationModes;
        private NetworkObject _networkObject;
        private bool _networkAuthorityConfigured;

        public Rigidbody Body => _body != null ? _body : GetComponent<Rigidbody>();
        public bool IsKnockedDown => _knockedDown;
        public bool SimulationEnabled => _simulationEnabled;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            ConfigureBody(_body);
            ConfigureRigCollisions();
            CacheSimulationBodies();
            _networkObject = GetComponent<NetworkObject>();
        }

        private void Update() => RefreshNetworkAuthority();

        private void RefreshNetworkAuthority()
        {
            if (_networkObject == null || !_networkObject.IsSpawned)
                return;
            bool shouldSimulate = _networkObject.IsServerStarted;
            if (_networkAuthorityConfigured && _simulationEnabled == shouldSimulate)
                return;
            _networkAuthorityConfigured = true;
            SetSimulationEnabled(shouldSimulate);
        }

        private void FixedUpdate()
        {
            RefreshNetworkAuthority();
            if (!_simulationEnabled)
                return;
            float upright = Vector3.Dot(transform.up, Vector3.up);
            if (!_knockedDown && IsDown(transform.up))
            {
                _knockedDown = true;
                _knockedDownAt = Time.time;
            }

            if (_knockedDown && Time.time < Mathf.Max(_knockedDownAt, _lastImpactAt) + GetUpDelay)
                return;

            float spring = _knockedDown ? _getUpSpring : _standingSpring;
            _body.AddTorque(CalculateUprightTorque(_body.rotation, _body.angularVelocity, spring,
                _angularDamping, _maximumUprightAcceleration), ForceMode.Acceleration);

            if (_knockedDown && upright > 0.97f && _body.angularVelocity.sqrMagnitude < 0.8f * 0.8f)
                _knockedDown = false;
        }

        public void NotifyImpact() => _lastImpactAt = Time.time;

        public void SetSimulationEnabled(bool enabled)
        {
            _simulationEnabled = enabled;
            CacheSimulationBodies();
            for (int index = 0; index < _simulationBodies.Length; index++)
            {
                Rigidbody simulatedBody = _simulationBodies[index];
                if (simulatedBody == null)
                    continue;
                if (enabled)
                {
                    simulatedBody.isKinematic = false;
                    simulatedBody.collisionDetectionMode = _simulationCollisionModes[index];
                    simulatedBody.interpolation = _simulationInterpolationModes[index];
                }
                else
                {
                    simulatedBody.collisionDetectionMode = CollisionDetectionMode.Discrete;
                    simulatedBody.interpolation = RigidbodyInterpolation.None;
                    simulatedBody.isKinematic = true;
                }
            }
        }

        private void CacheSimulationBodies()
        {
            if (_simulationBodies != null)
                return;
            _simulationBodies = GetComponentsInChildren<Rigidbody>(true);
            _simulationCollisionModes = new CollisionDetectionMode[_simulationBodies.Length];
            _simulationInterpolationModes = new RigidbodyInterpolation[_simulationBodies.Length];
            for (int index = 0; index < _simulationBodies.Length; index++)
            {
                _simulationCollisionModes[index] = _simulationBodies[index].collisionDetectionMode;
                _simulationInterpolationModes[index] = _simulationBodies[index].interpolation;
            }
        }

        public static bool IsDown(Vector3 up) => Vector3.Dot(up.normalized, Vector3.up) < KnockedDownDot;

        public static Vector3 CalculateUprightTorque(Quaternion rotation, Vector3 angularVelocity, float spring,
            float damping, float maximumAcceleration = float.PositiveInfinity)
        {
            Quaternion correction = Quaternion.FromToRotation(rotation * Vector3.up, Vector3.up);
            correction.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f)
                angle -= 360f;
            if (!float.IsFinite(axis.x) || axis.sqrMagnitude < 0.0001f)
                axis = Vector3.zero;
            Vector3 acceleration = axis.normalized * (angle * Mathf.Deg2Rad * Mathf.Max(0f, spring)) -
                                   angularVelocity * Mathf.Max(0f, damping);
            return Vector3.ClampMagnitude(acceleration, Mathf.Max(0f, maximumAcceleration));
        }

        public static void ConfigureBody(Rigidbody body)
        {
            ConfigurePhysicalResponseBody(body, Mass, new Vector3(0f, CenterOfMassY, 0f));
        }

        /// <summary>Shared root-body tuning used by dummies and a player during physical response.</summary>
        public static void ConfigurePhysicalResponseBody(Rigidbody body, float mass, Vector3 centerOfMass)
        {
            if (body == null)
                return;
            body.mass = Mathf.Max(0.01f, mass);
            body.linearDamping = PhysicalLinearDamping;
            body.angularDamping = PhysicalAngularDamping;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.Continuous;
            body.constraints = RigidbodyConstraints.None;
            body.useGravity = true;
            body.maxAngularVelocity = MaximumAngularVelocity;
            body.centerOfMass = centerOfMass;
        }

        public static TrainingDummy CreateFromPrefab(Vector3 position, GameObject prefab)
        {
            if (prefab == null)
                return null;

            GameObject instance = Instantiate(prefab, position, Quaternion.identity);
            TrainingDummy dummy = instance.GetComponent<TrainingDummy>();
            if (dummy == null)
            {
                Destroy(instance);
                return null;
            }
            dummy.name = "Training Dummy (Local Prop)";
            return dummy;
        }

        /// <summary>Legacy fallback for scenes generated before TrainingDummy.prefab existed.</summary>
        public static TrainingDummy CreateLegacyLocal(Vector3 position, GameObject playerVisualSource)
        {
            GameObject dummy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            dummy.name = "Training Dummy (Local Prop)";
            dummy.transform.SetPositionAndRotation(position, Quaternion.identity);
            Rigidbody body = dummy.AddComponent<Rigidbody>();
            ConfigureBody(body);

            if (playerVisualSource != null)
            {
                dummy.transform.localScale = Vector3.one;
                CapsuleCollider sourceCapsule = playerVisualSource.GetComponent<CapsuleCollider>();
                CapsuleCollider dummyCapsule = dummy.GetComponent<CapsuleCollider>();
                if (sourceCapsule != null)
                {
                    dummyCapsule.radius = sourceCapsule.radius;
                    dummyCapsule.height = sourceCapsule.height;
                    dummyCapsule.center = sourceCapsule.center;
                    dummyCapsule.material = sourceCapsule.sharedMaterial;
                }
                Renderer sourceRenderer = playerVisualSource.GetComponent<Renderer>();
                if (sourceRenderer != null)
                    dummy.GetComponent<Renderer>().sharedMaterial = sourceRenderer.sharedMaterial;

                Transform sourceRig = playerVisualSource.transform.Find("World Rig");
                if (sourceRig != null)
                {
                    dummy.GetComponent<Renderer>().enabled = false;
                    Transform rig = Instantiate(sourceRig.gameObject, dummy.transform, false).transform;
                    rig.name = sourceRig.name;
                    AddRagdollLimb(rig.Find("Torso/Left Arm"), body, dummy.GetComponent<Collider>());
                    AddRagdollLimb(rig.Find("Torso/Right Arm"), body, dummy.GetComponent<Collider>());
                }
            }

            return dummy.AddComponent<TrainingDummy>();
        }

        public static void AddRagdollLimb(Transform limb, Rigidbody rootBody, Collider rootCollider)
        {
            EnsureRagdollLimb(limb, rootBody, rootCollider);
        }

        /// <summary>Creates or repairs a dummy-style arm joint without adding duplicate components.</summary>
        public static ConfigurableJoint EnsureRagdollLimb(Transform limb, Rigidbody rootBody,
            Collider rootCollider)
        {
            if (limb == null || rootBody == null)
                return null;

            Collider limbCollider = limb.GetComponent<Collider>();
            if (limbCollider != null)
            {
                limbCollider.enabled = true;
                if (rootCollider != null)
                    Physics.IgnoreCollision(limbCollider, rootCollider, true);
            }

            Rigidbody limbBody = limb.GetComponent<Rigidbody>();
            if (limbBody == null)
                limbBody = limb.gameObject.AddComponent<Rigidbody>();
            limbBody.mass = 6f;
            limbBody.linearDamping = 0.08f;
            limbBody.angularDamping = ArmAngularDamping;
            limbBody.interpolation = RigidbodyInterpolation.Interpolate;
            limbBody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            ConfigurableJoint joint = limb.GetComponent<ConfigurableJoint>();
            if (joint == null)
                joint = limb.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = rootBody;
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = new Vector3(0f, 0.5f, 0f);
            joint.connectedAnchor = rootBody.transform.InverseTransformPoint(limb.TransformPoint(joint.anchor));
            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = ConfigurableJointMotion.Limited;
            joint.angularYMotion = ConfigurableJointMotion.Limited;
            joint.angularZMotion = ConfigurableJointMotion.Limited;
            joint.lowAngularXLimit = new SoftJointLimit { limit = -75f };
            joint.highAngularXLimit = new SoftJointLimit { limit = 75f };
            joint.angularYLimit = new SoftJointLimit { limit = 55f };
            joint.angularZLimit = new SoftJointLimit { limit = 55f };
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.targetRotation = Quaternion.identity;
            joint.slerpDrive = new JointDrive
            {
                positionSpring = ArmPoseSpring,
                positionDamper = ArmPoseDamper,
                maximumForce = ArmPoseMaximumForce,
                useAcceleration = true
            };
            joint.enableCollision = false;
            return joint;
        }

        private void ConfigureRigCollisions()
        {
            Collider rootCollider = GetComponent<Collider>();
            if (rootCollider == null)
                return;

            Transform rig = transform.Find("World Rig/Torso");
            if (rig == null)
                return;

            IgnoreRootCollision(rig.Find("Left Arm"), rootCollider);
            IgnoreRootCollision(rig.Find("Right Arm"), rootCollider);
        }

        private static void IgnoreRootCollision(Transform limb, Collider rootCollider)
        {
            Collider limbCollider = limb != null ? limb.GetComponent<Collider>() : null;
            if (limbCollider != null)
                Physics.IgnoreCollision(limbCollider, rootCollider, true);
        }
    }
}
