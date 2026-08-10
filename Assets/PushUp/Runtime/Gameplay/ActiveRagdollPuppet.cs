using UnityEngine;

namespace PushUp.Gameplay
{
    /// <summary>Compact owner-authored arm pose consumed by remote presentation.</summary>
    public struct PlayerPoseSnapshot
    {
        public uint Tick;
        public uint Sequence;
        public Quaternion LeftArmLocalRotation;
        public Quaternion RightArmLocalRotation;

        public PlayerPoseSnapshot(Quaternion leftArmLocalRotation, Quaternion rightArmLocalRotation)
            : this(0u, 0u, leftArmLocalRotation, rightArmLocalRotation)
        {
        }

        public PlayerPoseSnapshot(uint tick, uint sequence, Quaternion leftArmLocalRotation,
            Quaternion rightArmLocalRotation)
        {
            Tick = tick;
            Sequence = sequence;
            LeftArmLocalRotation = NetworkQuaternion.NormalizeOrIdentity(leftArmLocalRotation);
            RightArmLocalRotation = NetworkQuaternion.NormalizeOrIdentity(rightArmLocalRotation);
        }

        public bool TrySanitize(out PlayerPoseSnapshot sanitized)
        {
            bool leftValid = NetworkQuaternion.TryNormalize(LeftArmLocalRotation, out Quaternion left);
            bool rightValid = NetworkQuaternion.TryNormalize(RightArmLocalRotation, out Quaternion right);
            sanitized = new PlayerPoseSnapshot(Tick, Sequence, left, right);
            return leftValid && rightValid;
        }
    }

    /// <summary>Procedural first-person and remote-player arm presentation.</summary>
    public sealed class ActiveRagdollPuppet : MonoBehaviour
    {
        private const int RightPunchAction = 1;
        private const int PushAction = 2;
        private const int LeftPunchAction = 3;
        private const float RollingSpeedThreshold = 0.35f;
        private const int PlayerArmExcludedCollisionMask =
            (1 << GameplayLayers.Player) | (1 << GameplayLayers.Boulder) | (1 << GameplayLayers.Actor) |
            (1 << GameplayLayers.Interactable) | (1 << GameplayLayers.Pickup) |
            (1 << GameplayLayers.GameplayTrigger);
        public const float PlayerKnockdownTiltDegrees = TrainingDummy.MaximumStandingTiltDegrees;
        public const float PlayerControlLockDuration = 0.95f;
        public const float PlayerRagdollHoldDuration = 1.15f;
        public const float CameraFallPositionSharpness = 6.5f;
        public const float CameraFallRotationSharpness = 7.5f;
        public const float CameraRecoveryPositionSharpness = 4.5f;
        public const float CameraRecoveryRotationSharpness = 5.5f;

        [SerializeField] private Renderer _bodyRenderer;
        [SerializeField] private Transform _worldRoot;
        [SerializeField] private Transform _torso;
        [SerializeField] private Transform _leftArm;
        [SerializeField] private Transform _rightArm;
        [SerializeField] private Transform _viewRoot;
        [SerializeField] private Transform _viewLeftArm;
        [SerializeField] private Transform _viewRightArm;
        [SerializeField] private Rigidbody _leftArmBody;
        [SerializeField] private Rigidbody _rightArmBody;
        [SerializeField] private ConfigurableJoint _leftArmJoint;
        [SerializeField] private ConfigurableJoint _rightArmJoint;
        [SerializeField] private float _spring = 12f;

        private Vector3 _leftRestPosition;
        private Vector3 _rightRestPosition;
        private Vector3 _viewLeftRestPosition;
        private Vector3 _viewRightRestPosition;
        private Quaternion _torsoRestRotation;
        private Quaternion _leftRestRotation;
        private Quaternion _rightRestRotation;
        private Quaternion _viewLeftRestRotation;
        private Quaternion _viewRightRestRotation;
        private bool _grabPose;
        private int _action;
        private float _actionUntil;
        private float _impact;
        private bool _nextPunchLeft;
        private bool _grabbingBoulder;
        private float _rollCycle;
        private float _rollBlend;
        private Vector3 _lastWorldPosition;
        private Vector2 _reactionTilt;
        private Vector2 _reactionAxis = Vector2.right;
        private bool _reactionKnockedDown;
        private float _reactionGetUpAt;
        private float _reactionControlUnlockAt;
        private Vector3 _cameraReactionOffset;
        private Quaternion _cameraReactionRotation = Quaternion.identity;
        private PlayerActorPhysics _actorPhysics;
        private bool _localFirstPersonView;
        private Renderer[] _worldRenderers;
        private bool _physicalWorldArmsConfigured;
        private Quaternion _leftJointStartRotation;
        private Quaternion _rightJointStartRotation;
        private bool _hasObservedPose;
        private PlayerPoseSnapshot _observedPose;
        private uint _lastObservedPoseSequence;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private bool _invalidPoseWarningIssued;
#endif

        public bool IsKnockedDown => _actorPhysics != null ? _actorPhysics.IsKnockedDown : _reactionKnockedDown;
        public bool IsMovementLocked => _actorPhysics != null
            ? _actorPhysics.IsMovementLocked
            : _reactionKnockedDown && Time.time < _reactionControlUnlockAt;
        public Vector3 CameraReactionOffset => _cameraReactionOffset;
        public Quaternion CameraReactionRotation => _cameraReactionRotation;
        public bool HasPhysicalWorldArms => _physicalWorldArmsConfigured;

        private void Awake()
        {
            // These references are serialized by the prefab builder, while the convenience flag is
            // intentionally runtime-only. Reconstruct it before ownership presentation is applied so
            // hiding the owner's world renderers does not deactivate their physical arm bodies.
            _physicalWorldArmsConfigured = _leftArmBody != null && _rightArmBody != null &&
                                           _leftArmJoint != null && _rightArmJoint != null;
            _leftJointStartRotation = NetworkQuaternion.NormalizeOrIdentity(
                _leftArm != null ? _leftArm.localRotation : Quaternion.identity);
            _rightJointStartRotation = NetworkQuaternion.NormalizeOrIdentity(
                _rightArm != null ? _rightArm.localRotation : Quaternion.identity);
            _worldRenderers = _worldRoot != null
                ? _worldRoot.GetComponentsInChildren<Renderer>(true)
                : null;
            CaptureRestPose();
            _lastWorldPosition = transform.position;
            BindPhysicalActor(GetComponent<PlayerActorPhysics>());
        }

        public void Configure(
            Renderer bodyRenderer,
            Transform worldRoot,
            Transform torso,
            Transform leftArm,
            Transform rightArm,
            Transform viewRoot,
            Transform viewLeftArm,
            Transform viewRightArm)
        {
            _bodyRenderer = bodyRenderer;
            _worldRoot = worldRoot;
            _torso = torso;
            _leftArm = leftArm;
            _rightArm = rightArm;
            _viewRoot = viewRoot;
            _viewLeftArm = viewLeftArm;
            _viewRightArm = viewRightArm;
            CaptureRestPose();
        }

        public ActiveRagdollPuppet CreateStandaloneClone(GameObject player, Transform cameraPivot, Renderer bodyRenderer)
        {
            if (player == null || cameraPivot == null || _worldRoot == null || _viewRoot == null)
                return null;

            Transform worldRoot = Instantiate(_worldRoot.gameObject, player.transform, false).transform;
            Transform viewRoot = Instantiate(_viewRoot.gameObject, cameraPivot, false).transform;
            worldRoot.name = _worldRoot.name;
            viewRoot.name = _viewRoot.name;
            Transform torso = worldRoot.Find("Torso");
            Transform left = torso != null ? torso.Find("Left Arm") : null;
            Transform right = torso != null ? torso.Find("Right Arm") : null;
            Transform viewLeft = viewRoot.Find("Left Arm");
            Transform viewRight = viewRoot.Find("Right Arm");

            ActiveRagdollPuppet clone = player.AddComponent<ActiveRagdollPuppet>();
            clone._spring = _spring;
            clone.Configure(bodyRenderer, worldRoot, torso, left, right, viewRoot, viewLeft, viewRight);
            Rigidbody rootBody = player.GetComponent<Rigidbody>();
            PlayerActorPhysics actorPhysics = player.GetComponent<PlayerActorPhysics>();
            if (actorPhysics == null)
                actorPhysics = player.AddComponent<PlayerActorPhysics>();
            clone.ConfigurePhysicalWorldArms(rootBody);
            PlayerActorPhysics templateActor = GetComponent<PlayerActorPhysics>();
            actorPhysics.Configure(clone, templateActor != null ? templateActor.PhysicalMaterial : null);
            actorPhysics.SetSimulationAuthority(true);
            clone.ConfigureLocalView(true);
            return clone;
        }

        public void ConfigureLocalView(bool local)
        {
            _localFirstPersonView = local;
            if (_bodyRenderer != null)
                _bodyRenderer.enabled = ShouldShowBodyRenderer(local);
            if (_worldRoot != null)
            {
                // A local owner's physical arms must remain active even though their renderers are hidden.
                _worldRoot.gameObject.SetActive(_physicalWorldArmsConfigured || ShouldShowWorldRig(local));
                SetWorldRenderersVisible(ShouldShowWorldRig(local));
            }
            if (_viewRoot != null)
                _viewRoot.gameObject.SetActive(local);
        }

        public static bool ShouldShowBodyRenderer(bool localFirstPersonView) => false;
        public static bool ShouldShowWorldRig(bool localFirstPersonView) => !localFirstPersonView;

        public void BindPhysicalActor(PlayerActorPhysics actorPhysics)
        {
            _actorPhysics = actorPhysics;
            if (_actorPhysics != null && _actorPhysics != GetComponent<PlayerActorPhysics>())
                Debug.LogWarning("ActiveRagdollPuppet was bound to a physical actor on another GameObject.", this);
        }

        /// <summary>
        /// Adds dummy-style arm bodies and joints once. Safe for prefab migration and standalone cloning.
        /// </summary>
        public void ConfigurePhysicalWorldArms(Rigidbody rootBody)
        {
            if (rootBody == null || _leftArm == null || _rightArm == null)
                return;

            Collider rootCollider = rootBody.GetComponent<Collider>();
            _leftArmJoint = TrainingDummy.EnsureRagdollLimb(_leftArm, rootBody, rootCollider);
            _rightArmJoint = TrainingDummy.EnsureRagdollLimb(_rightArm, rootBody, rootCollider);
            _leftArmBody = _leftArm.GetComponent<Rigidbody>();
            _rightArmBody = _rightArm.GetComponent<Rigidbody>();
            ConfigurePlayerArmCollider(_leftArm);
            ConfigurePlayerArmCollider(_rightArm);
            _leftJointStartRotation = _leftArm.localRotation;
            _rightJointStartRotation = _rightArm.localRotation;
            _physicalWorldArmsConfigured = _leftArmJoint != null && _rightArmJoint != null;

            // The root capsule owns player collision. A detached graphical torso collider would become
            // a conflicting compound/static collider when FishNet smooths the visual hierarchy.
            Collider torsoCollider = _torso != null ? _torso.GetComponent<Collider>() : null;
            if (torsoCollider != null)
                torsoCollider.enabled = false;

            _worldRenderers = _worldRoot.GetComponentsInChildren<Renderer>(true);
            SetWorldArmSimulationEnabled(_actorPhysics == null || _actorPhysics.HasSimulationAuthority);
            ConfigureLocalView(_localFirstPersonView);
        }

        public void SetWorldArmSimulationEnabled(bool enabled)
        {
            ConfigureLimbSimulation(_leftArmBody, enabled);
            ConfigureLimbSimulation(_rightArmBody, enabled);
        }

        public PlayerPoseSnapshot CapturePoseSnapshot(uint tick = 0u, uint sequence = 0u) => new(
            tick, sequence,
            _leftArm != null ? _leftArm.localRotation : Quaternion.identity,
            _rightArm != null ? _rightArm.localRotation : Quaternion.identity);

        public void ApplyPoseSnapshot(PlayerPoseSnapshot snapshot)
        {
            if (!snapshot.TrySanitize(out PlayerPoseSnapshot sanitized) ||
                (_hasObservedPose && sanitized.Sequence != 0u &&
                 !NetworkQuaternion.IsNewer(sanitized.Sequence, _lastObservedPoseSequence)))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (!_invalidPoseWarningIssued)
                {
                    _invalidPoseWarningIssued = true;
                    Debug.LogWarning("Discarded an invalid or stale remote arm pose. Further pose warnings are suppressed.", this);
                }
#endif
                return;
            }
            _observedPose = sanitized;
            _lastObservedPoseSequence = sanitized.Sequence;
            _hasObservedPose = true;
        }

        public void SetPhysicalActorState(PlayerActorState state)
        {
            // Root physics owns the state. This hook intentionally does not clear a remote arm
            // snapshot when the actor returns to locomotion; locomotion poses are replicated too.
        }

        public void SetGrabPose(bool active, bool grabbingBoulder = false)
        {
            _grabPose = active;
            _grabbingBoulder = active && grabbingBoulder;
            if (!_grabbingBoulder)
            {
                _rollCycle = 0f;
                _rollBlend = 0f;
            }
        }

        public static bool ShouldRollHands(bool grabPose, bool grabbingBoulder, float horizontalSpeed)
        {
            return grabPose && grabbingBoulder && horizontalSpeed >= RollingSpeedThreshold;
        }

        public void PlayInteraction(bool push)
        {
            if (push)
            {
                _action = PushAction;
            }
            else
            {
                _action = _nextPunchLeft ? LeftPunchAction : RightPunchAction;
                _nextPunchLeft = !_nextPunchLeft;
            }
            _actionUntil = Time.time + (push ? 0.25f : 0.18f);
        }

        public void PlayPunch(bool left)
        {
            _action = left ? LeftPunchAction : RightPunchAction;
            _nextPunchLeft = !left;
            _actionUntil = Time.time + 0.18f;
        }

        public void Loosen(float strength)
        {
            _impact = Mathf.Clamp01(Mathf.Max(_impact, strength));
        }

        /// <summary>
        /// Legacy presentation entry point. Migrated players unlock the real root and add only
        /// contact torque here because the old motor path already owns translational impulse.
        /// </summary>
        public void ReactToImpact(Vector3 worldImpulse)
        {
            if (_actorPhysics != null)
            {
                if (!_actorPhysics.TryBeginLegacyImpact(worldImpulse))
                    NotifyPhysicalImpact(worldImpulse);
                return;
            }

            Vector3 localImpulse = transform.InverseTransformDirection(worldImpulse);
            Vector2 axis = new(-localImpulse.z, localImpulse.x);
            if (axis.sqrMagnitude < 0.0001f)
                axis = Vector2.right;
            axis.Normalize();
            _reactionAxis = axis;
            _reactionTilt = Vector2.ClampMagnitude(
                _reactionTilt + axis * ImpactTiltDegrees(worldImpulse.magnitude), 72f);
            _impact = 1f;
            if (_reactionTilt.magnitude > PlayerKnockdownTiltDegrees)
            {
                if (!_reactionKnockedDown)
                    _reactionControlUnlockAt = Time.time + PlayerControlLockDuration;
                _reactionKnockedDown = true;
                _reactionGetUpAt = Time.time + PlayerRagdollHoldDuration;
            }
        }

        /// <summary>Presentation-only impact feedback for the new validated impact path.</summary>
        public void NotifyPhysicalImpact(Vector3 worldImpulse)
        {
            _impact = Mathf.Clamp01(Mathf.Max(_impact,
                worldImpulse.magnitude / Mathf.Max(PlayerInteraction.PunchImpulse, 0.001f)));
        }

        public static float ImpactTiltDegrees(float impulseMagnitude) =>
            Mathf.Clamp(Mathf.Max(0f, impulseMagnitude) / PlayerInteraction.PunchImpulse * 24f, 0f, 60f);

        private void LateUpdate()
        {
            float deltaTime = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 movement = transform.position - _lastWorldPosition;
            _lastWorldPosition = transform.position;
            float horizontalSpeed = Vector3.ProjectOnPlane(movement, Vector3.up).magnitude / deltaTime;
            bool rollingBoulder = ShouldRollHands(_grabPose, _grabbingBoulder, horizontalSpeed);
            _rollBlend = Mathf.MoveTowards(_rollBlend, rollingBoulder ? 1f : 0f, deltaTime * 7f);
            if (rollingBoulder)
            {
                float cycleRate = Mathf.Lerp(5f, 10f, Mathf.Clamp01(horizontalSpeed / PlayerPhysics.MaxSpeed));
                _rollCycle += deltaTime * cycleRate;
            }

            if (_action != 0 && Time.time >= _actionUntil)
                _action = 0;

            _impact = Mathf.MoveTowards(_impact, 0f, Time.deltaTime * 1.7f);
            UpdateHitReaction(deltaTime);
            UpdateCameraReaction(deltaTime);
            float rate = _spring * (1f - _impact * 0.75f) * (_reactionKnockedDown ? 0.28f : 1f);

            Quaternion leftWorld = _leftRestRotation;
            Quaternion rightWorld = _rightRestRotation;
            Vector3 leftView = _viewLeftRestPosition;
            Vector3 rightView = _viewRightRestPosition;
            Quaternion leftViewRotation = _viewLeftRestRotation;
            Quaternion rightViewRotation = _viewRightRestRotation;

            if (_grabPose)
            {
                leftWorld *= Quaternion.Euler(-58f, -10f, 0f);
                rightWorld *= Quaternion.Euler(-58f, 10f, 0f);
                leftView += new Vector3(0.02f, 0.08f, 0.22f);
                rightView += new Vector3(-0.02f, 0.08f, 0.22f);
                leftViewRotation *= Quaternion.Euler(-8f, 8f, 0f);
                rightViewRotation *= Quaternion.Euler(-8f, -8f, 0f);

                // Only a moving boulder grab uses the alternating rolling pose.
                // Terrain braces, players, the dummy, and ordinary props retain
                // the steady two-hand reach even while the player is moving.
                float rollWave = Mathf.Sin(_rollCycle) * _rollBlend;
                leftWorld *= Quaternion.Euler(-18f * rollWave, 0f, 0f);
                rightWorld *= Quaternion.Euler(18f * rollWave, 0f, 0f);
                leftView += Vector3.forward * (0.15f * rollWave);
                rightView -= Vector3.forward * (0.15f * rollWave);
                leftViewRotation *= Quaternion.Euler(-6f * rollWave, 0f, 0f);
                rightViewRotation *= Quaternion.Euler(6f * rollWave, 0f, 0f);
            }

            if (_action == RightPunchAction)
            {
                rightWorld = _rightRestRotation * Quaternion.Euler(-82f, 12f, 0f);
                rightView = _viewRightRestPosition + new Vector3(-0.02f, 0.06f, 0.52f);
                rightViewRotation = _viewRightRestRotation * Quaternion.Euler(-4f, -5f, 0f);
            }
            else if (_action == LeftPunchAction)
            {
                leftWorld = _leftRestRotation * Quaternion.Euler(-82f, -12f, 0f);
                leftView = _viewLeftRestPosition + new Vector3(0.02f, 0.06f, 0.52f);
                leftViewRotation = _viewLeftRestRotation * Quaternion.Euler(-4f, 5f, 0f);
            }
            else if (_action == PushAction)
            {
                leftWorld = _leftRestRotation * Quaternion.Euler(-78f, -8f, 0f);
                rightWorld = _rightRestRotation * Quaternion.Euler(-78f, 8f, 0f);
                leftView = _viewLeftRestPosition + new Vector3(0.02f, 0.09f, 0.5f);
                rightView = _viewRightRestPosition + new Vector3(-0.02f, 0.09f, 0.5f);
            }

            if (IsMovementLocked)
            {
                leftWorld *= Quaternion.Euler(35f, -28f, -25f);
                rightWorld *= Quaternion.Euler(35f, 28f, 25f);
                leftView += new Vector3(-0.08f, -0.18f, -0.18f);
                rightView += new Vector3(0.08f, -0.18f, -0.18f);
            }

            MoveWorldArm(_leftArm, _leftArmBody, _leftArmJoint, _leftJointStartRotation,
                _leftRestPosition, leftWorld, _observedPose.LeftArmLocalRotation, rate);
            MoveWorldArm(_rightArm, _rightArmBody, _rightArmJoint, _rightJointStartRotation,
                _rightRestPosition, rightWorld, _observedPose.RightArmLocalRotation, rate);
            Move(_viewLeftArm, leftView, leftViewRotation, rate);
            Move(_viewRightArm, rightView, rightViewRotation, rate);
            if (_torso != null)
            {
                Quaternion reaction = _actorPhysics != null
                    ? Quaternion.identity
                    : Quaternion.Euler(_reactionTilt.x, 0f, -_reactionTilt.y);
                _torso.localRotation = Quaternion.Slerp(_torso.localRotation,
                    _torsoRestRotation * reaction, Time.deltaTime * Mathf.Max(3f, rate));
            }
        }

        private void UpdateHitReaction(float deltaTime)
        {
            if (_actorPhysics != null)
            {
                _reactionKnockedDown = _actorPhysics.IsKnockedDown;
                _reactionTilt = Vector2.zero;
                return;
            }

            if (_reactionKnockedDown)
            {
                if (Time.time < _reactionGetUpAt)
                {
                    _reactionTilt = Vector2.MoveTowards(_reactionTilt, _reactionAxis * 68f, deltaTime * 95f);
                    return;
                }

                _reactionTilt = Vector2.MoveTowards(_reactionTilt, Vector2.zero, deltaTime * 62f);
                if (_reactionTilt.sqrMagnitude < 1.5f * 1.5f)
                {
                    _reactionTilt = Vector2.zero;
                    _reactionKnockedDown = false;
                }
                return;
            }

            _reactionTilt = Vector2.MoveTowards(_reactionTilt, Vector2.zero, deltaTime * 42f);
        }

        private void UpdateCameraReaction(float deltaTime)
        {
            bool physicalReaction = _actorPhysics != null && _actorPhysics.IsMovementLocked;
            Vector3 targetOffset = physicalReaction
                ? _actorPhysics.CameraReactionOffset
                : _reactionKnockedDown ? new Vector3(0f, -0.72f, -0.2f) : Vector3.zero;
            Quaternion targetRotation = physicalReaction
                ? _actorPhysics.CameraReactionRotation
                : _reactionKnockedDown
                    ? Quaternion.Euler(-_reactionTilt.x * 0.42f, 0f, -_reactionTilt.y * 0.65f)
                    : Quaternion.identity;
            bool falling = physicalReaction || _reactionKnockedDown;
            float positionSharpness = falling
                ? CameraFallPositionSharpness
                : CameraRecoveryPositionSharpness;
            float rotationSharpness = falling
                ? CameraFallRotationSharpness
                : CameraRecoveryRotationSharpness;
            _cameraReactionOffset = LerpCameraReaction(
                _cameraReactionOffset, targetOffset, positionSharpness, deltaTime);
            _cameraReactionRotation = SlerpCameraReaction(
                _cameraReactionRotation, targetRotation, rotationSharpness, deltaTime);
        }

        public static Vector3 LerpCameraReaction(Vector3 current, Vector3 target, float sharpness, float deltaTime)
        {
            float amount = 1f - Mathf.Exp(-Mathf.Max(0f, sharpness) * Mathf.Max(0f, deltaTime));
            return Vector3.Lerp(current, target, amount);
        }

        public static Quaternion SlerpCameraReaction(Quaternion current, Quaternion target, float sharpness,
            float deltaTime)
        {
            float amount = 1f - Mathf.Exp(-Mathf.Max(0f, sharpness) * Mathf.Max(0f, deltaTime));
            return Quaternion.Slerp(current, target, amount);
        }

        private void CaptureRestPose()
        {
            if (_torso != null) _torsoRestRotation = _torso.localRotation;
            if (_leftArm != null)
            {
                _leftRestPosition = _leftArm.localPosition;
                _leftRestRotation = _leftArm.localRotation;
            }
            if (_rightArm != null)
            {
                _rightRestPosition = _rightArm.localPosition;
                _rightRestRotation = _rightArm.localRotation;
            }
            if (_viewLeftArm != null)
            {
                _viewLeftRestPosition = _viewLeftArm.localPosition;
                _viewLeftRestRotation = _viewLeftArm.localRotation;
            }
            if (_viewRightArm != null)
            {
                _viewRightRestPosition = _viewRightArm.localPosition;
                _viewRightRestRotation = _viewRightArm.localRotation;
            }
        }

        private static void Move(Transform limb, Vector3 position, Quaternion rotation, float rate)
        {
            if (limb == null)
                return;
            float amount = 1f - Mathf.Exp(-Mathf.Max(0f, rate) * Time.deltaTime);
            limb.localPosition = Vector3.Lerp(limb.localPosition, position, amount);
            limb.localRotation = Quaternion.Slerp(limb.localRotation, rotation, amount);
        }

        private void MoveWorldArm(Transform limb, Rigidbody limbBody, ConfigurableJoint joint,
            Quaternion jointStartRotation, Vector3 position, Quaternion rotation,
            Quaternion observedRotation, float rate)
        {
            if (limb == null)
                return;

            bool authoritativePhysics = _actorPhysics != null && _actorPhysics.HasSimulationAuthority &&
                                        limbBody != null && !limbBody.isKinematic && joint != null;
            if (authoritativePhysics)
            {
                float driveMultiplier = _actorPhysics.ActorState switch
                {
                    PlayerActorState.Locomotion => 1f,
                    PlayerActorState.Staggered => 0.5f,
                    PlayerActorState.KnockedDown => 0.12f,
                    PlayerActorState.Recovering => 0.35f,
                    _ => 1f
                };
                JointDrive drive = joint.slerpDrive;
                drive.positionSpring = TrainingDummy.ArmPoseSpring * driveMultiplier;
                drive.positionDamper = TrainingDummy.ArmPoseDamper * Mathf.Max(0.35f, driveMultiplier);
                drive.maximumForce = TrainingDummy.ArmPoseMaximumForce * Mathf.Max(0.2f, driveMultiplier);
                drive.useAcceleration = true;
                joint.slerpDrive = drive;
                Quaternion safeRotation = NetworkQuaternion.NormalizeOrIdentity(rotation);
                Quaternion safeStart = NetworkQuaternion.NormalizeOrIdentity(jointStartRotation);
                joint.targetRotation = NetworkQuaternion.NormalizeOrIdentity(
                    Quaternion.Inverse(safeRotation) * safeStart);
                return;
            }

            if (_hasObservedPose && _actorPhysics != null && !_actorPhysics.HasSimulationAuthority)
                rotation = observedRotation;
            Move(limb, position, rotation, rate);
        }

        private void SetWorldRenderersVisible(bool visible)
        {
            _worldRenderers ??= _worldRoot != null ? _worldRoot.GetComponentsInChildren<Renderer>(true) : null;
            if (_worldRenderers == null)
                return;
            foreach (Renderer worldRenderer in _worldRenderers)
            {
                if (worldRenderer != null)
                    worldRenderer.enabled = visible;
            }
        }

        private static void ConfigureLimbSimulation(Rigidbody limbBody, bool enabled)
        {
            if (limbBody == null)
                return;
            if (enabled)
            {
                limbBody.isKinematic = false;
                limbBody.interpolation = RigidbodyInterpolation.Interpolate;
                limbBody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
            else
            {
                limbBody.collisionDetectionMode = CollisionDetectionMode.Discrete;
                limbBody.interpolation = RigidbodyInterpolation.None;
                limbBody.isKinematic = true;
            }
        }

        private static void ConfigurePlayerArmCollider(Transform arm)
        {
            if (arm == null)
                return;
            arm.gameObject.layer = GameplayLayers.Presentation;
            Collider armCollider = arm.GetComponent<Collider>();
            if (armCollider != null)
                armCollider.excludeLayers = PlayerArmExcludedCollisionMask;
        }
    }
}
