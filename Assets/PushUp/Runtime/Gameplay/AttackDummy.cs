using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using Random = UnityEngine.Random;

namespace PushUp.Gameplay
{
    public readonly struct AttackDummyPresentationSnapshot
    {
        public readonly Transform Transform;
        public readonly Vector3 WorldPosition;
        public readonly bool Aggressive;

        public AttackDummyPresentationSnapshot(Transform transform, Vector3 worldPosition, bool aggressive)
        {
            Transform = transform;
            WorldPosition = worldPosition;
            Aggressive = aggressive;
        }
    }

    /// <summary>Host-authoritative training opponent which retaliates briefly after being provoked.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(TrainingDummy))]
    public sealed class AttackDummy : MonoBehaviour
    {
        public const float AggroDuration = 7f;
        public const float PursuitSpeed = 4.6f;
        public const float PursuitAcceleration = 18f;
        public const float AttackRange = 4.0f;
        public const float PersonalSpaceRange = 1.35f;
        public const float MinimumAttackCooldown = 0.48f;
        public const float MaximumAttackCooldown = 1.05f;
        public const float RecoveryDuration = 0.30f;
        public const float RecoverySpeed = 2.8f;
        public const float TurnAcceleration = 24f;
        public const float DecisionInterval = 0.1f;
        public const float DefaultPunchImpulse = 520f;
        public const float DefaultGrabPullImpulse = 65f;
        public const float DefaultPushImpulse = 2000f;
        public const float AttackContactHeight = 0.65f;
        public const float GrabContactHeight = 0.48f;
        public const float GrabHoldDuration = 0.38f;
        public const float MaximumGrabDuration = 1f;
        public const float GrabPushChance = 0.42f;

        private enum MovementIntent : byte
        {
            Idle,
            Pursue,
            Retreat,
            Hold
        }

        private Rigidbody _body;
        private TrainingDummy _ragdoll;
        private Rigidbody _target;
        private ConfigurableJoint _leftArm;
        private ConfigurableJoint _rightArm;
        private AttackDummyNetworkRelay _networkRelay;
        [Header("Combat")]
        [SerializeField, Min(1f)] private float _punchImpulse = DefaultPunchImpulse;
        [SerializeField, Min(1f)] private float _grabPullImpulse = DefaultGrabPullImpulse;
        [SerializeField, Min(1f)] private float _pushImpulse = DefaultPushImpulse;
        private Renderer[] _renderers;
        private Color[] _baseColors;
        private MaterialPropertyBlock _angerProperties;
        private float _aggressiveUntil;
        private float _nextAttackAt;
        private float _poseUntil;
        private float _recoverUntil;
        private float _nextDecisionAt;
        private MovementIntent _movementIntent;
        private Vector3 _movementDirection;
        private bool _presentationAggressive;
        private bool _threatActive;
        private bool _angerApplied;
        private bool _simulatePhysics = true;
        private bool _grabComboActive;
        private bool _grabUsedThisAggro;
        private float _grabStartedAt;
        private float _grabReleaseAt;
        private bool _grabLeft;
        private uint _nextOfflineImpactSequence;
        private Quaternion _leftArmRestRotation;
        private Quaternion _rightArmRestRotation;
        private static readonly List<AttackDummy> Instances = new(4);
        private static readonly ProfilerMarker DecisionMarker = new("PushUp.Actor.AttackDummyDecision");

        public bool IsAggressive => _target != null && Time.time < _aggressiveUntil;
        public float AggressiveUntil => _aggressiveUntil;
        public Rigidbody Target => _target;
        public bool AngerVisible => _presentationAggressive;
        public float ConfiguredPunchImpulse => _punchImpulse;
        public float ConfiguredGrabPullImpulse => _grabPullImpulse;
        public float ConfiguredPushImpulse => _pushImpulse;
        public Vector3 AngerWorldPosition => transform.position + Vector3.up * 2.45f;
        public AttackDummyPresentationSnapshot PresentationSnapshot =>
            new(transform, AngerWorldPosition, _presentationAggressive);
        public static IReadOnlyList<AttackDummy> ActiveInstances => Instances;
        public event Action<AttackDummyPresentationSnapshot> PresentationChanged;
        public static event Action<AttackDummy, bool> InstanceAvailabilityChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetPresentationStatics()
        {
            Instances.Clear();
            InstanceAvailabilityChanged = null;
        }

        private void OnEnable()
        {
            if (!Instances.Contains(this))
                Instances.Add(this);
            InstanceAvailabilityChanged?.Invoke(this, true);
        }

        private void OnDisable()
        {
            InstanceAvailabilityChanged?.Invoke(this, false);
            Instances.Remove(this);
        }

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _ragdoll = GetComponent<TrainingDummy>();
            _networkRelay = GetComponent<AttackDummyNetworkRelay>();
            _renderers = GetComponentsInChildren<Renderer>(true);
            _baseColors = new Color[_renderers.Length];
            for (int index = 0; index < _renderers.Length; index++)
            {
                Material material = _renderers[index].sharedMaterial;
                _baseColors[index] = material != null && material.HasProperty("_BaseColor")
                    ? material.GetColor("_BaseColor")
                    : Color.white;
            }
            _angerProperties = new MaterialPropertyBlock();
            Transform torso = transform.Find("World Rig/Torso");
            _leftArm = torso != null ? torso.Find("Left Arm")?.GetComponent<ConfigurableJoint>() : null;
            _rightArm = torso != null ? torso.Find("Right Arm")?.GetComponent<ConfigurableJoint>() : null;
            if (_leftArm != null)
                _leftArmRestRotation = _leftArm.transform.localRotation;
            if (_rightArm != null)
                _rightArmRestRotation = _rightArm.transform.localRotation;
        }

        private void FixedUpdate()
        {
            UpdateArmPose();
            if (_networkRelay != null && _networkRelay.IsSpawned && !_networkRelay.IsServerStarted)
                return;

            UpdateGrabCombo();

            if (Time.time >= _nextDecisionAt)
            {
                _nextDecisionAt = Time.time + DecisionInterval;
                EvaluateDecision();
            }

            ApplyMovementIntent(Time.fixedDeltaTime);
        }

        private void EvaluateDecision()
        {
            using ProfilerMarker.AutoScope profilerScope = DecisionMarker.Auto();
            if (!IsAggressive)
            {
                SetThreat(false);
                SetAggroPresentation(false);
                _target = null;
                _grabComboActive = false;
                _grabUsedThisAggro = false;
                _movementIntent = MovementIntent.Idle;
                _movementDirection = Vector3.zero;
                return;
            }
            if (_ragdoll.IsKnockedDown)
            {
                SetThreat(false);
                _movementIntent = MovementIntent.Idle;
                _movementDirection = Vector3.zero;
                return;
            }

            if (_grabComboActive)
            {
                SetThreat(true);
                _movementIntent = MovementIntent.Hold;
                _movementDirection = Vector3.zero;
                return;
            }

            Vector3 toTarget = _target.worldCenterOfMass - _body.worldCenterOfMass;
            Vector3 planar = Vector3.ProjectOnPlane(toTarget, Vector3.up);
            if (planar.sqrMagnitude < 0.01f)
            {
                SetThreat(true);
                _movementIntent = MovementIntent.Retreat;
                _movementDirection = transform.forward;
                return;
            }

            Vector3 direction = planar.normalized;
            _movementDirection = direction;
            if (Time.time < _recoverUntil || planar.magnitude < PersonalSpaceRange)
            {
                SetThreat(planar.magnitude < AttackRange);
                _movementIntent = MovementIntent.Retreat;
                return;
            }
            if (planar.magnitude > AttackRange)
            {
                SetThreat(false);
                _movementIntent = MovementIntent.Pursue;
            }
            else
            {
                SetThreat(true);
                _movementIntent = MovementIntent.Hold;
                if (Time.time >= _nextAttackAt)
                    Attack(direction);
            }
        }

        private void ApplyMovementIntent(float deltaTime)
        {
            if (_movementDirection.sqrMagnitude > 0.01f && _movementIntent != MovementIntent.Idle)
                TurnToward(_movementDirection, deltaTime);
            switch (_movementIntent)
            {
                case MovementIntent.Pursue:
                    Pursue(_movementDirection, deltaTime);
                    break;
                case MovementIntent.Retreat:
                    Retreat(_movementDirection, deltaTime);
                    break;
                default:
                    BrakeHorizontalMotion(deltaTime);
                    break;
            }
        }

        public void Provoke(Rigidbody attacker)
        {
            if (attacker == null || attacker == _body ||
                (_networkRelay != null && _networkRelay.IsSpawned && !_networkRelay.IsServerStarted))
                return;
            bool wasAggressive = IsAggressive;
            if (_target != null && _target != attacker)
                SetThreat(false);
            _target = attacker;
            _aggressiveUntil = Time.time + AggroDuration;
            _nextDecisionAt = Time.time;
            if (!wasAggressive)
            {
                _grabUsedThisAggro = false;
                _nextAttackAt = Time.time + Mathf.Lerp(0.12f, 0.32f, Random.value);
            }
            SetAggroPresentation(true);
        }

        public static bool IsAggressiveAt(float now, float aggressiveUntil, bool hasTarget) =>
            hasTarget && now < aggressiveUntil;

        public static Vector3 CalculatePursuitVelocity(Vector3 currentVelocity, Vector3 direction, float deltaTime)
        {
            Vector3 vertical = Vector3.Project(currentVelocity, Vector3.up);
            Vector3 planar = Vector3.ProjectOnPlane(currentVelocity, Vector3.up);
            Vector3 desired = Vector3.ProjectOnPlane(direction, Vector3.up).normalized * PursuitSpeed;
            return Vector3.MoveTowards(planar, desired, PursuitAcceleration * Mathf.Max(0f, deltaTime)) + vertical;
        }

        public static Vector3 AttackImpulse(Vector3 direction, bool push, float punchImpulse = DefaultPunchImpulse,
            float pushImpulse = DefaultPushImpulse)
        {
            float strength = push ? pushImpulse : punchImpulse;
            Vector3 safe = Vector3.ClampMagnitude(Vector3.ProjectOnPlane(direction, Vector3.up).normalized +
                                                  Vector3.up * 0.08f, 1f);
            return safe * strength;
        }

        public static Vector3 GrabPullImpulse(Vector3 direction, float strength = DefaultGrabPullImpulse)
        {
            Vector3 safe = Vector3.ClampMagnitude(-Vector3.ProjectOnPlane(direction, Vector3.up).normalized +
                                                   Vector3.up * 0.03f, 1f);
            return safe * strength;
        }

        public static bool ShouldUseGrabPush(float randomValue) => Mathf.Clamp01(randomValue) < GrabPushChance;

        public static bool CanUseGrabPush(bool alreadyUsedThisAggro, float randomValue) =>
            !alreadyUsedThisAggro && ShouldUseGrabPush(randomValue);

        public static float NextAttackDelay(float randomValue) =>
            Mathf.Lerp(MinimumAttackCooldown, MaximumAttackCooldown, Mathf.Clamp01(randomValue));

        private void Pursue(Vector3 direction, float deltaTime)
        {
            Vector3 next = CalculatePursuitVelocity(_body.linearVelocity, direction, deltaTime);
            _body.AddForce(next - _body.linearVelocity, ForceMode.VelocityChange);
        }

        private void Retreat(Vector3 direction, float deltaTime)
        {
            Vector3 vertical = Vector3.Project(_body.linearVelocity, Vector3.up);
            Vector3 planar = Vector3.ProjectOnPlane(_body.linearVelocity, Vector3.up);
            Vector3 desired = -Vector3.ProjectOnPlane(direction, Vector3.up).normalized * RecoverySpeed;
            Vector3 next = Vector3.MoveTowards(planar, desired, PursuitAcceleration * deltaTime);
            _body.AddForce(next + vertical - _body.linearVelocity, ForceMode.VelocityChange);
        }

        private void BrakeHorizontalMotion(float deltaTime)
        {
            Vector3 planar = Vector3.ProjectOnPlane(_body.linearVelocity, Vector3.up);
            Vector3 next = Vector3.MoveTowards(planar, Vector3.zero, PursuitAcceleration * deltaTime);
            _body.AddForce(next - planar, ForceMode.VelocityChange);
        }

        private void TurnToward(Vector3 direction, float deltaTime)
        {
            Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f)
                return;
            float yawError = Vector3.SignedAngle(forward, direction, Vector3.up);
            float acceleration = Mathf.Clamp(yawError * 0.45f - _body.angularVelocity.y * 3f,
                -TurnAcceleration, TurnAcceleration);
            _body.AddTorque(Vector3.up * acceleration, ForceMode.Acceleration);
        }

        private void Attack(Vector3 direction)
        {
            if (CanUseGrabPush(_grabUsedThisAggro, Random.value))
            {
                BeginGrabCombo(direction);
                return;
            }

            bool left = Random.value < 0.5f;
            _nextAttackAt = Time.time + NextAttackDelay(Random.value);
            _recoverUntil = Time.time + RecoveryDuration;
            ExecuteAttack(direction, false, left);
        }

        private void BeginGrabCombo(Vector3 direction)
        {
            if (_target == null)
                return;

            _grabUsedThisAggro = true;
            _grabComboActive = true;
            _grabStartedAt = Time.time;
            _grabReleaseAt = Time.time + GrabHoldDuration;
            _grabLeft = Random.value < 0.5f;
            _nextAttackAt = _grabReleaseAt + NextAttackDelay(Random.value);
            _recoverUntil = _grabReleaseAt + RecoveryDuration;

            Vector3 impulse = GrabPullImpulse(direction, _grabPullImpulse);
            Vector3 hitPoint = SurfaceContactPoint(_target, direction, GrabContactHeight);
            uint simulationTick = _networkRelay != null ? _networkRelay.SimulationTick : 0u;
            if (_networkRelay != null && _networkRelay.IsSpawned && _networkRelay.IsServerStarted)
                _networkRelay.DispatchGrab(_target, _grabLeft, impulse, hitPoint, simulationTick);
            else
            {
                ApplyOfflineImpact(_target, impulse, hitPoint, PlayerImpactCommand.GrabPullAction,
                    simulationTick);
                PlayReplicatedGrab(_target, _grabLeft, impulse);
            }
        }

        private void UpdateGrabCombo()
        {
            if (!_grabComboActive)
                return;
            if (_target == null || !IsAggressive || _ragdoll.IsKnockedDown)
            {
                _grabComboActive = false;
                return;
            }
            float forcedReleaseAt = _grabStartedAt + MaximumGrabDuration;
            if (Time.time < Mathf.Min(_grabReleaseAt, forcedReleaseAt))
                return;

            Vector3 direction = Vector3.ProjectOnPlane(_target.worldCenterOfMass - _body.worldCenterOfMass,
                Vector3.up);
            _grabComboActive = false;
            if (direction.sqrMagnitude < 0.01f)
                return;
            ExecuteAttack(direction.normalized, true, _grabLeft);
        }

        private void ExecuteAttack(Vector3 direction, bool push, bool left)
        {
            if (_target == null)
                return;

            Vector3 impulse = AttackImpulse(direction, push, _punchImpulse, _pushImpulse);
            Vector3 hitPoint = SurfaceContactPoint(_target, direction, AttackContactHeight);
            uint simulationTick = _networkRelay != null ? _networkRelay.SimulationTick : 0u;
            _body.AddForce(-impulse * (push ? 0.22f : 0.15f), ForceMode.Impulse);

            if (_networkRelay != null && _networkRelay.IsSpawned && _networkRelay.IsServerStarted)
                _networkRelay.DispatchAttack(_target, push, left, impulse, hitPoint, simulationTick);
            else
            {
                ApplyOfflineImpact(_target, impulse, hitPoint,
                    push ? PlayerImpactCommand.PushAction : PlayerImpactCommand.PunchAction, simulationTick);
                PlayReplicatedAttack(_target, push, left, impulse);
            }
        }

        private void ApplyOfflineImpact(Rigidbody target, Vector3 impulse, Vector3 hitPoint, byte action,
            uint simulationTick)
        {
            if (target == null)
                return;
            PlayerImpactCommand command = new(gameObject.GetEntityId().GetHashCode(),
                PlayerInteraction.NextSequence(ref _nextOfflineImpactSequence),
                simulationTick, action, impulse, target.transform.InverseTransformPoint(hitPoint));
            PlayerImpactRouting.ApplyToLocalAuthority(target, command);
        }

        public static Vector3 SurfaceContactPoint(Rigidbody target, Vector3 attackDirection,
            float heightAboveCenter)
        {
            if (target == null)
                return default;

            Collider targetCollider = target.GetComponent<CapsuleCollider>();
            if (targetCollider == null)
                targetCollider = target.GetComponentInChildren<Collider>();
            if (targetCollider == null)
                return target.worldCenterOfMass + Vector3.up * Mathf.Max(0f, heightAboveCenter);

            Vector3 planarDirection = Vector3.ProjectOnPlane(attackDirection, Vector3.up).normalized;
            if (planarDirection.sqrMagnitude < 0.001f)
                planarDirection = Vector3.ProjectOnPlane(target.transform.forward, Vector3.up).normalized;
            if (planarDirection.sqrMagnitude < 0.001f)
                planarDirection = Vector3.forward;

            float upperOffset = Mathf.Clamp(heightAboveCenter, 0f, targetCollider.bounds.extents.y * 0.82f);
            Vector3 upperTorso = target.worldCenterOfMass + Vector3.up * upperOffset;
            Vector3 outsideNearSurface = upperTorso - planarDirection *
                (targetCollider.bounds.extents.magnitude + 0.5f);
            return targetCollider.ClosestPoint(outsideNearSurface);
        }

        internal void PlayReplicatedAttack(Rigidbody target, bool push, bool left, Vector3 impulse)
        {
            PlayAttackPose(push, left);
            PlayerInteraction interaction = target != null ? target.GetComponent<PlayerInteraction>() : null;
            if (target != null && target.GetComponent<IOwnerPlayerImpactReceiver>() == null)
                interaction?.ReactFromHit(impulse);
            interaction?.ShowFighterAttack(push ? "FIGHTER PUSH" : "FIGHTER PUNCH");
        }

        internal void PlayReplicatedGrab(Rigidbody target, bool left, Vector3 impulse)
        {
            PlayGrabPose(left);
            PlayerInteraction interaction = target != null ? target.GetComponent<PlayerInteraction>() : null;
            if (target != null && target.GetComponent<IOwnerPlayerImpactReceiver>() == null)
                interaction?.ReactFromHit(impulse);
            interaction?.ShowFighterAttack("FIGHTER GRABBED");
        }

        internal void SetNetworkSimulation(bool enabled)
        {
            _simulatePhysics = enabled;
            _ragdoll.SetSimulationEnabled(enabled);
        }

        internal void ClearTarget()
        {
            SetThreat(false);
            _target = null;
            _grabComboActive = false;
            _grabUsedThisAggro = false;
            SetReplicatedAggro(false);
        }

        internal void SetReplicatedAggro(bool active)
        {
            if (_presentationAggressive == active)
                return;
            _presentationAggressive = active;
            PresentationChanged?.Invoke(PresentationSnapshot);
            if (!active)
                UpdateArmPose(true);
        }

        private void PlayAttackPose(bool push, bool left)
        {
            if (push)
            {
                SetArmTargets(Quaternion.Euler(-62f, -8f, 0f), Quaternion.Euler(-62f, 8f, 0f));
                _poseUntil = Time.time + 0.28f;
                return;
            }

            SetArmTargets(left ? Quaternion.Euler(-72f, -12f, 0f) : Quaternion.identity,
                left ? Quaternion.identity : Quaternion.Euler(-72f, 12f, 0f));
            _poseUntil = Time.time + 0.2f;
        }

        private void PlayGrabPose(bool left)
        {
            float lean = left ? -9f : 9f;
            SetArmTargets(Quaternion.Euler(-58f, lean, -4f), Quaternion.Euler(-58f, -lean, 4f));
            _poseUntil = Time.time + GrabHoldDuration;
        }

        private void UpdateArmPose(bool force = false)
        {
            if (!force && Time.time < _poseUntil)
                return;
            if (_presentationAggressive)
                SetArmTargets(Quaternion.Euler(-38f, -10f, -8f), Quaternion.Euler(-38f, 10f, 8f));
            else
                SetArmTargets(Quaternion.identity, Quaternion.identity);
        }

        private void SetAggroPresentation(bool active)
        {
            if (_presentationAggressive == active)
                return;
            SetReplicatedAggro(active);
            if (_networkRelay != null && _networkRelay.IsSpawned && _networkRelay.IsServerStarted)
                _networkRelay.BroadcastAggro(active);
        }

        private void SetThreat(bool active)
        {
            if (_threatActive == active)
                return;
            _threatActive = active;
            if (_networkRelay != null && _networkRelay.IsSpawned && _networkRelay.IsServerStarted)
                _networkRelay.BroadcastThreat(_target, active);
            else
                _target?.GetComponent<PlayerInteraction>()?.SetFighterThreat(active);
        }

        private void Update()
        {
            if (!_presentationAggressive)
            {
                if (_angerApplied)
                {
                    foreach (Renderer renderer in _renderers)
                        renderer.SetPropertyBlock(null);
                    _angerApplied = false;
                }
                return;
            }

            float pulse = 0.55f + Mathf.PingPong(Time.time * 2.8f, 0.35f);
            for (int index = 0; index < _renderers.Length; index++)
            {
                _angerProperties.Clear();
                Color angry = Color.Lerp(_baseColors[index], new Color(1f, 0.04f, 0.01f, 1f), pulse);
                _angerProperties.SetColor("_BaseColor", angry);
                _angerProperties.SetColor("_Color", angry);
                _angerProperties.SetColor("_EmissionColor", new Color(0.4f * pulse, 0.01f, 0f));
                _renderers[index].SetPropertyBlock(_angerProperties);
            }
            _angerApplied = true;
        }

        private void SetArmTargets(Quaternion left, Quaternion right)
        {
            if (_leftArm != null)
            {
                _leftArm.targetRotation = left;
                if (!_simulatePhysics)
                    _leftArm.transform.localRotation = _leftArmRestRotation * left;
            }
            if (_rightArm != null)
            {
                _rightArm.targetRotation = right;
                if (!_simulatePhysics)
                    _rightArm.transform.localRotation = _rightArmRestRotation * right;
            }
        }
    }
}
