using System;
using UnityEngine;

namespace PushUp.Gameplay
{
    /// <summary>The physical state of a player actor, independent of input or network transport.</summary>
    public enum PlayerActorState : byte
    {
        Locomotion = 0,
        Staggered = 1,
        KnockedDown = 2,
        Recovering = 3
    }

    /// <summary>
    /// Switches the real player root between constrained arcade locomotion and owner-simulated
    /// impact physics. Network code is responsible for granting simulation authority only to
    /// the owning client (or the server-local host); offline players are authoritative by default.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    public sealed class PlayerActorPhysics : MonoBehaviour
    {
        public const float MinimumStaggerDuration = 0.22f;
        public const float UprightRecoveryDot = 0.97f;
        public const float MaximumRecoveryAngularSpeed = 0.8f;
        public const float PlayerCenterOfMassY = -0.38f;
        public const float MaximumCameraPitchReaction = 35f;
        public const float MaximumCameraRollReaction = 45f;

        [SerializeField] private float _standingSpring = 32f;
        [SerializeField] private float _getUpSpring = 42f;
        [SerializeField] private float _angularDamping = 7.5f;
        [SerializeField] private float _maximumUprightAcceleration = TrainingDummy.MaximumUprightAcceleration;
        [SerializeField] private PhysicsMaterial _physicalMaterial;
        [SerializeField] private bool _simulateInFixedUpdate = true;

        private Rigidbody _body;
        private CapsuleCollider _capsule;
        private ActiveRagdollPuppet _puppet;
        private PlayerActorState _state;
        private bool _hasSimulationAuthority = true;
        private bool _locomotionSettingsCaptured;
        private bool _locomotionUseGravity;
        private bool _locomotionAutomaticCenterOfMass;
        private Vector3 _locomotionCenterOfMass;
        private float _locomotionLinearDamping;
        private float _locomotionAngularDamping;
        private float _locomotionMaximumAngularVelocity;
        private RigidbodyConstraints _locomotionConstraints;
        private CollisionDetectionMode _locomotionCollisionMode;
        private RigidbodyInterpolation _locomotionInterpolation;
        private PhysicsMaterial _locomotionMaterial;
        private float _desiredYaw;
        private float _stateEnteredAt;
        private float _lastImpactAt = float.NegativeInfinity;

        public event Action<PlayerActorState, PlayerActorState> StateChanged;

        public Rigidbody Body => _body != null ? _body : GetComponent<Rigidbody>();
        public PlayerActorState ActorState => _state;
        public bool HasSimulationAuthority => _hasSimulationAuthority;
        public bool IsMovementLocked => _state != PlayerActorState.Locomotion;
        public bool IsKnockedDown => _state == PlayerActorState.KnockedDown || _state == PlayerActorState.Recovering;
        public float DesiredYaw => _desiredYaw;
        public PhysicsMaterial PhysicalMaterial => _physicalMaterial;

        /// <summary>
        /// The camera already follows a point transformed by the physical root, so physical falls
        /// need no additional synthetic position drop.
        /// </summary>
        public Vector3 CameraReactionOffset => Vector3.zero;

        /// <summary>
        /// Returns only the root's tilt relative to the owner's desired yaw. PlayerMotor composes
        /// this after presentation yaw, allowing a smooth but genuinely physical fall camera.
        /// </summary>
        public Quaternion CameraReactionRotation
        {
            get
            {
                if (_state == PlayerActorState.Locomotion || _body == null)
                    return Quaternion.identity;
                Quaternion yaw = Quaternion.Euler(0f, _desiredYaw, 0f);
                Vector3 relativeEuler = (Quaternion.Inverse(yaw) * _body.rotation).eulerAngles;
                float pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, relativeEuler.x),
                    -MaximumCameraPitchReaction, MaximumCameraPitchReaction);
                float roll = Mathf.Clamp(Mathf.DeltaAngle(0f, relativeEuler.z),
                    -MaximumCameraRollReaction, MaximumCameraRollReaction);
                return Quaternion.Euler(pitch, 0f, roll);
            }
        }

        private void Awake()
        {
            EnsureInitialized();
            _desiredYaw = transform.eulerAngles.y;
            _puppet?.BindPhysicalActor(this);
        }

        private void FixedUpdate()
        {
            if (_simulateInFixedUpdate)
                Simulate(Time.fixedDeltaTime, Time.time);
        }

        public void Configure(ActiveRagdollPuppet puppet, PhysicsMaterial physicalMaterial = null)
        {
            _body ??= GetComponent<Rigidbody>();
            _capsule ??= GetComponent<CapsuleCollider>();
            _puppet = puppet != null ? puppet : GetComponent<ActiveRagdollPuppet>();
            if (physicalMaterial != null)
                _physicalMaterial = physicalMaterial;
            CaptureLocomotionSettings();
            _puppet?.BindPhysicalActor(this);
            _puppet?.SetWorldArmSimulationEnabled(_hasSimulationAuthority);
        }

        /// <summary>
        /// Use external simulation when the owning motor is driven by a networking tick rather
        /// than Unity FixedUpdate. Call <see cref="Simulate"/> exactly once per physics tick.
        /// </summary>
        public void SetExternalSimulation(bool externalSimulation) =>
            _simulateInFixedUpdate = !externalSimulation;

        public void SetSimulationAuthority(bool authoritative)
        {
            if (_hasSimulationAuthority == authoritative)
                return;
            _hasSimulationAuthority = authoritative;
            _puppet?.SetWorldArmSimulationEnabled(authoritative);
        }

        public void SetDesiredYaw(float yawDegrees) => _desiredYaw = Mathf.Repeat(yawDegrees, 360f);

        /// <summary>
        /// Applies one already-validated impact to the authoritative root. Callers must deduplicate
        /// network commands before invoking this method.
        /// </summary>
        public bool TryApplyImpact(Vector3 impulse, Vector3 worldPoint, float desiredYawDegrees)
        {
            EnsureInitialized();
            if (_body == null || !_hasSimulationAuthority || !IsFinite(impulse) || !IsFinite(worldPoint) ||
                impulse.sqrMagnitude <= 0.000001f)
                return false;

            SetDesiredYaw(desiredYawDegrees);
            EnterPhysicalResponse(Time.time);
            _body.AddForceAtPosition(impulse, worldPoint, ForceMode.Impulse);
            _lastImpactAt = Time.time;
            _puppet?.NotifyPhysicalImpact(impulse);
            return true;
        }

        public bool TryApplyImpact(Vector3 impulse, Vector3 worldPoint) =>
            TryApplyImpact(impulse, worldPoint, _desiredYaw);

        /// <summary>
        /// Compatibility bridge for the previous impact path, which already applies translation
        /// through PlayerMotor. It unlocks root rotation and adds only the missing contact torque.
        /// New networking code should call <see cref="TryApplyImpact(Vector3,Vector3,float)"/> instead.
        /// </summary>
        public bool TryBeginLegacyImpact(Vector3 impulse)
        {
            EnsureInitialized();
            if (_body == null || !_hasSimulationAuthority || !IsFinite(impulse) ||
                impulse.sqrMagnitude <= 0.000001f)
                return false;

            EnterPhysicalResponse(Time.time);
            float capsuleHeight = _capsule != null ? _capsule.height : PlayerPhysics.CapsuleHeight;
            Vector3 lever = transform.up * Mathf.Max(0.35f, capsuleHeight * 0.32f);
            _body.AddTorque(Vector3.Cross(lever, impulse), ForceMode.Impulse);
            _lastImpactAt = Time.time;
            NotifyPresentationImpact(impulse);
            return true;
        }

        /// <summary>Loosens presentation without applying gameplay physics.</summary>
        public void NotifyPresentationImpact(Vector3 impulse) => _puppet?.NotifyPhysicalImpact(impulse);

        /// <summary>
        /// Applies replicated state on a non-authoritative observer. Root pose continues to come
        /// from the owner-authoritative NetworkTransform.
        /// </summary>
        public void ApplyObservedState(PlayerActorState state, float desiredYawDegrees)
        {
            if (_hasSimulationAuthority)
                return;
            SetDesiredYaw(desiredYawDegrees);
            SetState(state, Time.time);
        }

        public void Simulate(float deltaTime, float now)
        {
            EnsureInitialized();
            if (!_hasSimulationAuthority || _state == PlayerActorState.Locomotion || _body == null)
                return;

            Vector3 up = _body.rotation * Vector3.up;
            if (_state == PlayerActorState.Staggered && TrainingDummy.IsDown(up))
                SetState(PlayerActorState.KnockedDown, now);

            if (_state == PlayerActorState.KnockedDown)
            {
                if (now >= _lastImpactAt + TrainingDummy.GetUpDelay)
                    SetState(PlayerActorState.Recovering, now);
                else
                    return;
            }

            float spring = _state == PlayerActorState.Recovering ? _getUpSpring : _standingSpring;
            _body.AddTorque(TrainingDummy.CalculateUprightTorque(_body.rotation, _body.angularVelocity,
                spring, _angularDamping, _maximumUprightAcceleration), ForceMode.Acceleration);

            float upright = Vector3.Dot(up.normalized, Vector3.up);
            bool minimumStaggerComplete = now >= _stateEnteredAt + MinimumStaggerDuration;
            if (minimumStaggerComplete && upright > UprightRecoveryDot &&
                _body.angularVelocity.sqrMagnitude < MaximumRecoveryAngularSpeed * MaximumRecoveryAngularSpeed)
                RestoreLocomotion();
        }

        public void ForceLocomotion(float desiredYawDegrees)
        {
            SetDesiredYaw(desiredYawDegrees);
            RestoreLocomotion();
        }

        private void EnterPhysicalResponse(float now)
        {
            CaptureLocomotionSettings();
            if (_state == PlayerActorState.Locomotion || _state == PlayerActorState.Recovering)
                SetState(PlayerActorState.Staggered, now);

            TrainingDummy.ConfigurePhysicalResponseBody(_body, _body.mass,
                new Vector3(0f, PlayerCenterOfMassY, 0f));
            if (_capsule != null && _physicalMaterial != null)
                _capsule.sharedMaterial = _physicalMaterial;
        }

        private void RestoreLocomotion()
        {
            if (_body == null)
                return;

            Quaternion uprightYaw = Quaternion.Euler(0f, _desiredYaw, 0f);
            _body.angularVelocity = Vector3.zero;
            _body.rotation = uprightYaw;
            _body.useGravity = _locomotionUseGravity;
            _body.linearDamping = _locomotionLinearDamping;
            _body.angularDamping = _locomotionAngularDamping;
            _body.maxAngularVelocity = _locomotionMaximumAngularVelocity;
            _body.collisionDetectionMode = _locomotionCollisionMode;
            _body.interpolation = _locomotionInterpolation;
            if (_locomotionAutomaticCenterOfMass)
                _body.ResetCenterOfMass();
            else
                _body.centerOfMass = _locomotionCenterOfMass;
            if (_capsule != null)
                _capsule.sharedMaterial = _locomotionMaterial;
            _body.constraints = _locomotionConstraints;
            SetState(PlayerActorState.Locomotion, Time.time);
        }

        private void CaptureLocomotionSettings()
        {
            if (_locomotionSettingsCaptured || _body == null)
                return;
            _locomotionSettingsCaptured = true;
            _locomotionUseGravity = _body.useGravity;
            _locomotionAutomaticCenterOfMass = _body.automaticCenterOfMass;
            _locomotionCenterOfMass = _body.centerOfMass;
            _locomotionLinearDamping = _body.linearDamping;
            _locomotionAngularDamping = _body.angularDamping;
            _locomotionMaximumAngularVelocity = _body.maxAngularVelocity;
            _locomotionConstraints = _body.constraints;
            _locomotionCollisionMode = _body.collisionDetectionMode;
            _locomotionInterpolation = _body.interpolation;
            _locomotionMaterial = _capsule != null ? _capsule.sharedMaterial : null;
        }

        private void EnsureInitialized()
        {
            _body ??= GetComponent<Rigidbody>();
            _capsule ??= GetComponent<CapsuleCollider>();
            _puppet ??= GetComponent<ActiveRagdollPuppet>();
            CaptureLocomotionSettings();
        }

        private void SetState(PlayerActorState next, float now)
        {
            if (_state == next)
                return;
            PlayerActorState previous = _state;
            _state = next;
            _stateEnteredAt = now;
            _puppet?.SetPhysicalActorState(next);
            StateChanged?.Invoke(previous, next);
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
