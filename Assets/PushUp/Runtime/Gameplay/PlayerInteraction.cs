using System;
using FishNet.Object;
using Unity.Profiling;
using UnityEngine;

namespace PushUp.Gameplay
{
    public enum PlayerInteractionMode : byte
    {
        None,
        DynamicGrab,
        TerrainBrace,
        BoulderPushStance
    }

    public readonly struct PlayerInteractionHudSnapshot
    {
        public readonly bool Visible;
        public readonly bool ReticleVisible;
        public readonly string InteractionStatus;
        public readonly bool FighterThreatActive;
        public readonly string FighterStatus;

        public PlayerInteractionHudSnapshot(bool visible, bool reticleVisible, string interactionStatus,
            bool fighterThreatActive, string fighterStatus)
        {
            Visible = visible;
            ReticleVisible = reticleVisible;
            InteractionStatus = interactionStatus ?? string.Empty;
            FighterThreatActive = fighterThreatActive;
            FighterStatus = fighterStatus ?? string.Empty;
        }
    }

    public readonly struct PunchComboResult
    {
        public readonly int Step;
        public readonly bool IsFinisher;
        public readonly float Multiplier;
        public readonly float Cooldown;

        public PunchComboResult(int step, bool isFinisher, float multiplier, float cooldown)
        {
            Step = step;
            IsFinisher = isFinisher;
            Multiplier = multiplier;
            Cooldown = cooldown;
        }
    }

    [RequireComponent(typeof(PlayerInputReader))]
    public sealed class PlayerInteraction : MonoBehaviour
    {
        public const float PunchImpulse = 200f;
        public const float GrabPunchMultiplier = 2f;
        public const float GrabPunchImpulse = PunchImpulse * GrabPunchMultiplier;
        public const int PunchComboLength = 3;
        public const float PunchComboFinisherMultiplier = 1.4f;
        public const float PunchComboWindow = 1.5f;
        public const float PunchComboFinisherCooldown = 1.5f;
        public const float ReleaseDistance = 4.5f;
        public const float ReleaseDistanceSquared = ReleaseDistance * ReleaseDistance;

        [SerializeField] private float _grabRange = 3.4f;
        [SerializeField] private float _grabForce = 650f;
        [SerializeField] private float _grabDamping = 40f;
        [SerializeField] private float _maxGrabForce = 1100f;
        [SerializeField] private float _punchRange = 2.8f;
        [SerializeField] private float _punchImpulse = PunchImpulse;
        [SerializeField] private float _punchCooldown = 0.2f;

        private ILocalPlayerController _motor;
        private INetworkInteractionRelay _networkRelay;
        private PlayerInputReader _input;
        private ActiveRagdollPuppet _puppet;
        private PunchImpactFeedback _impactFeedback;
        private Rigidbody _grabbedBody;
        private Rigidbody _boulderPushBody;
        private Vector3 _grabbedLocalPoint;
        private Vector3 _staticAnchor;
        private bool _hasStaticAnchor;
        private bool _hasAnchor;
        private bool _grabWasHeld;
        private bool _grabRequiresRelease;
        private bool _grabIsLocalOnly;
        private float _nextPunchTime;
        private int _localPunchComboHits;
        private float _localLastPunchHitTime = float.NegativeInfinity;
        private float _statusUntil;
        private string _status = string.Empty;
        private bool _fighterThreatActive;
        private string _fighterAttackStatus = string.Empty;
        private float _fighterAttackStatusUntil;
        private PlayerInteractionMode _mode;
        private readonly Collider[] _aimOverlaps = new Collider[16];
        private readonly RaycastHit[] _aimHits = new RaycastHit[24];
        private readonly RaycastHit[] _lineOfSightHits = new RaycastHit[16];
        private readonly OwnerGrabConstraint[] _ownerGrabConstraints = new OwnerGrabConstraint[4];
        private static readonly ProfilerMarker PhysicsInteractionMarker = new("PushUp.Player.InteractionPhysics");
        private static readonly ProfilerMarker AimQueryMarker = new("PushUp.Player.InteractionAimQuery");
        private static readonly ProfilerMarker LineOfSightMarker = new("PushUp.Player.InteractionLineOfSight");
        private Transform _cachedView;
        private string _cachedAimPrompt = string.Empty;
        private float _nextAimPromptSample;
        private bool _fighterAttackPresentationActive;
        private uint _nextLocalImpactSequence;

        private struct OwnerGrabConstraint
        {
            public NetworkObject Source;
            public int SourceObjectId;
            public uint Sequence;
            public Vector3 TargetLocalPoint;

            public readonly bool Active => Source != null;
        }

        public string InteractionStatus => _status;
        public bool ReticleVisible => _motor != null && _motor.IsLocallyControlled &&
                                      PlayerInputReader.GameplayEnabled;
        public bool FighterThreatActive => _fighterThreatActive;
        public string FighterStatus => Time.time < _fighterAttackStatusUntil
            ? _fighterAttackStatus
            : _fighterThreatActive ? "FIGHTER ON YOU" : string.Empty;
        public PlayerInteractionMode InteractionMode => _mode;
        public float ConfiguredPunchImpulse => Mathf.Clamp(_punchImpulse, 0f, PunchImpulse);
        public float ConfiguredPunchCooldown => Mathf.Max(0.05f, _punchCooldown);
        public static PlayerInteraction LocalHudSource { get; private set; }
        public PlayerInteractionHudSnapshot HudSnapshot => new(ReticleVisible, ReticleVisible, _status,
            _fighterThreatActive, FighterStatus);
        public event Action<PlayerInteraction> PresentationChanged;
        public event Action<PlayerInteractionHudSnapshot> HudChanged;
        public static event Action<PlayerInteraction> LocalHudSourceChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetPresentationStatics()
        {
            LocalHudSource = null;
            LocalHudSourceChanged = null;
        }

        private void Awake()
        {
            foreach (MonoBehaviour behaviour in GetComponents<MonoBehaviour>())
            {
                if (_motor == null && behaviour is ILocalPlayerController controller)
                    _motor = controller;
                if (_networkRelay == null && behaviour is INetworkInteractionRelay relay)
                    _networkRelay = relay;
            }
            _input = GetComponent<PlayerInputReader>();
            if (_input == null)
                _input = gameObject.AddComponent<PlayerInputReader>();
            _puppet = GetComponent<ActiveRagdollPuppet>();
            _impactFeedback = GetComponent<PunchImpactFeedback>();
        }

        private void Update()
        {
            bool locallyControlled = _motor != null && _motor.IsLocallyControlled;
            if (locallyControlled && LocalHudSource != this)
            {
                LocalHudSource = this;
                LocalHudSourceChanged?.Invoke(this);
                NotifyHudChanged();
            }
            else if (!locallyControlled && LocalHudSource == this)
            {
                LocalHudSource = null;
                LocalHudSourceChanged?.Invoke(null);
            }
            if (_fighterAttackPresentationActive && Time.time >= _fighterAttackStatusUntil)
            {
                _fighterAttackPresentationActive = false;
                NotifyHudChanged();
            }
            if (_motor == null || !_motor.IsLocallyControlled)
                return;

            if (ResolvePuppet() is { IsMovementLocked: true })
            {
                if (_mode != PlayerInteractionMode.None)
                    ReleaseGrab();
                return;
            }

            if (!PlayerInputReader.GameplayEnabled)
            {
                ReleaseGrab();
                _grabWasHeld = false;
                SetPresentationStatus(string.Empty);
                return;
            }

            bool grabHeld = _input.GrabHeld;
            if (_mode == PlayerInteractionMode.BoulderPushStance &&
                PlayerPhysics.ShouldExitBoulderStance(_input.Move, _input.JumpHeld))
                EndGrabAfterPush();

            if (_grabRequiresRelease)
            {
                // A PUSH forcibly drops the target. Holding the same grab press
                // must never reacquire it; only a physical release clears this latch.
                if (!grabHeld)
                    _grabRequiresRelease = false;
            }
            else if (CanStartGrab(grabHeld, _grabWasHeld, _grabRequiresRelease))
                TryBeginGrab();
            else if (!grabHeld && _grabWasHeld)
                ReleaseGrab();
            _grabWasHeld = grabHeld;

            if (_input.ConsumePunch())
                TryPunch();
            if (_input.ConsumeAnchor())
                TryUseOrReleaseAnchor();

            if (_mode != PlayerInteractionMode.None && !IsGrabValid())
                ReleaseGrab();

            if (Time.time >= _statusUntil)
            {
                if (_mode == PlayerInteractionMode.BoulderPushStance)
                    SetPresentationStatus(_motor.BoulderStanceState == BoulderStanceClientState.Pending
                        ? "ALIGNING"
                        : _motor.IsPushingBoulder
                        ? "PUSHING"
                        : _motor.IsBrakingBoulder ? "BRAKING" : "PUSH");
                else if (_mode == PlayerInteractionMode.DynamicGrab || _mode == PlayerInteractionMode.TerrainBrace)
                    SetPresentationStatus("HOLDING");
                else if (Time.unscaledTime >= _nextAimPromptSample)
                {
                    _cachedAimPrompt = AimPrompt();
                    _nextAimPromptSample = Time.unscaledTime + 1f / 24f;
                    SetPresentationStatus(_cachedAimPrompt);
                }
            }
        }

        private void FixedUpdate()
        {
            if (_motor == null || !_motor.IsLocallyControlled)
                return;
            using ProfilerMarker.AutoScope profilerScope = PhysicsInteractionMarker.Auto();
            SimulateOwnerGrabConstraints();
            if (!PlayerInputReader.GameplayEnabled)
                return;
            bool networkAuthoritativeGrab = _networkRelay != null && !_grabIsLocalOnly;
            if (networkAuthoritativeGrab && _motor is PlayerMotor networkMotor && networkMotor.IsServerStarted)
                return;
            if (_mode == PlayerInteractionMode.BoulderPushStance)
                return;

            Transform view = ViewTransform;
            Vector3 hand = view.position + view.forward * 0.85f;
            if (_hasStaticAnchor)
            {
                Vector3 delta = _staticAnchor - hand;
                Vector3 force = CalculateGrabForce(delta, _motor.Body.linearVelocity, _grabForce, _grabDamping, 900f);
                _motor.ApplyExternalForce(force, ForceMode.Force);
                return;
            }

            if (_grabbedBody == null)
                return;
            Vector3 point = _grabbedBody.transform.TransformPoint(_grabbedLocalPoint);
            Vector3 deltaToHand = hand - point;
            Vector3 relativeVelocity = _grabbedBody.GetPointVelocity(point) - _motor.Body.GetPointVelocity(hand);
            float playerMultiplier = _grabbedBody.GetComponent<PlayerInteraction>() != null ? 0.5f : 1f;
            Vector3 pull = CalculateGrabForce(deltaToHand, relativeVelocity, _grabForce, _grabDamping, _maxGrabForce) * playerMultiplier;
            if (!networkAuthoritativeGrab)
                _grabbedBody.AddForceAtPosition(pull, point, ForceMode.Force);
            Vector3 reaction = -pull * 0.34f;
            if (_motor.IsGrounded && _grabbedBody.GetComponentInParent<BoulderController>() != null)
                reaction = Vector3.ProjectOnPlane(reaction, _motor.GroundNormal);
            _motor.ApplyExternalForce(reaction, ForceMode.Force);
        }

        private void TryBeginGrab()
        {
            if (!TryGetAimHit(_grabRange, 0.18f, out InteractionHit hit))
                return;

            if (hit.Body != null && hit.Body != _motor.Body)
            {
                BoulderController boulder = hit.Body.GetComponentInParent<BoulderController>();
                if (boulder != null)
                {
                    if (!_motor.TryBeginBoulderPushStance(hit.Body))
                        return;
                    _boulderPushBody = hit.Body;
                    _grabbedBody = null;
                    _grabIsLocalOnly = false;
                    _hasStaticAnchor = false;
                    _mode = PlayerInteractionMode.BoulderPushStance;
                    if (_networkRelay != null)
                    {
                        NetworkObject target = hit.Body.GetComponent<NetworkObject>();
                        if (target == null)
                        {
                            ReleaseGrab();
                            return;
                        }
                        _networkRelay.RequestBeginBoulderPush(target);
                    }
                    ResolvePuppet()?.SetGrabPose(true, true);
                    SetStatus(_networkRelay != null ? "ALIGNING" : "PUSH", 0.2f);
                    return;
                }

                _grabbedBody = hit.Body;
                _grabbedLocalPoint = _grabbedBody.transform.InverseTransformPoint(hit.point);
                _grabIsLocalOnly = IsLocalOnlyBody(_grabbedBody);
                _hasStaticAnchor = false;
                _mode = PlayerInteractionMode.DynamicGrab;
                if (_networkRelay != null && !_grabIsLocalOnly)
                {
                    Vector3 grabbedWorldPoint = _grabbedBody.transform.TransformPoint(_grabbedLocalPoint);
                    if (!TryResolveNetworkTarget(_grabbedBody, grabbedWorldPoint, out NetworkObject target,
                            out Rigidbody targetBody, out Vector3 targetLocalPoint))
                    {
                        ReleaseGrab();
                        return;
                    }
                    _grabbedBody = targetBody;
                    _grabbedLocalPoint = targetLocalPoint;
                    _networkRelay.RequestDynamicGrab(target, targetLocalPoint);
                }
                _grabbedBody.GetComponentInParent<TrainingDummy>()?.NotifyImpact();
                _grabbedBody.GetComponentInParent<AttackDummy>()?.Provoke(_motor.Body);
            }
            else
            {
                _grabbedBody = null;
                _boulderPushBody = null;
                _grabIsLocalOnly = false;
                _staticAnchor = hit.point;
                _hasStaticAnchor = true;
                _mode = PlayerInteractionMode.TerrainBrace;
                _networkRelay?.RequestStaticGrab(_staticAnchor);
            }
            ResolvePuppet()?.SetGrabPose(true, false);
            SetStatus("HOLDING", 0.2f);
        }

        private void ReleaseGrab()
        {
            PlayerInteractionMode releasedMode = _mode;
            bool notifyNetwork = releasedMode != PlayerInteractionMode.None && !_grabIsLocalOnly;
            if (releasedMode == PlayerInteractionMode.BoulderPushStance)
                _motor?.EndBoulderPushStance();
            _grabbedBody = null;
            _boulderPushBody = null;
            _hasStaticAnchor = false;
            _grabIsLocalOnly = false;
            _mode = PlayerInteractionMode.None;
            ResolvePuppet()?.SetGrabPose(false);
            if (notifyNetwork)
            {
                if (releasedMode == PlayerInteractionMode.BoulderPushStance)
                    _networkRelay?.RequestEndBoulderPush();
                else
                    _networkRelay?.RequestReleaseGrab();
            }
        }

        private bool IsGrabValid()
        {
            if (_mode == PlayerInteractionMode.BoulderPushStance)
            {
                CapsuleCollider capsule = GetComponent<CapsuleCollider>();
                return _boulderPushBody != null && _motor.IsInBoulderPushStance && _motor.IsGrounded &&
                       _motor.GroundSurfaceKind != MovementSurfaceKind.Boulder &&
                       PlayerPhysics.TryGetBoulderStanceGeometry(capsule, transform, _boulderPushBody,
                           _motor.GroundNormal, out BoulderPushStanceGeometry geometry) &&
                       geometry.SurfaceGap <= PlayerPhysics.BoulderStanceMaximumGap &&
                           HasLineOfSight(geometry.SurfacePoint, _boulderPushBody.transform);
            }

            if (_mode == PlayerInteractionMode.DynamicGrab && _grabbedBody == null)
                return false;

            Transform view = ViewTransform;
            Vector3 hand = view.position + view.forward * 0.85f;
            Vector3 target = _hasStaticAnchor ? _staticAnchor : _grabbedBody.transform.TransformPoint(_grabbedLocalPoint);
            return (target - hand).sqrMagnitude <= ReleaseDistanceSquared;
        }

        private void TryPunch()
        {
            if (Time.time < _nextPunchTime)
                return;
            _nextPunchTime = Time.time + _punchCooldown;
            // A held dynamic body is the explicit combo target. This lets a player
            // punch the object in-hand even when the reticle has moved away from its
            // collider, while terrain bracing intentionally remains a normal punch.
            bool boulderStancePush = _mode == PlayerInteractionMode.BoulderPushStance && _boulderPushBody != null;
            bool isGrabPunch = (_mode == PlayerInteractionMode.DynamicGrab && _grabbedBody != null) ||
                               boulderStancePush;
            Rigidbody targetBody;
            Vector3 hitPoint;
            Vector3 hitNormal;
            BoulderPushStanceGeometry stanceGeometry = default;
            if (boulderStancePush)
            {
                targetBody = _boulderPushBody;
                if (!PlayerPhysics.TryGetBoulderStanceGeometry(GetComponent<CapsuleCollider>(), transform,
                        targetBody, _motor.GroundNormal, out stanceGeometry))
                {
                    EndGrabAfterPush();
                    return;
                }
                hitPoint = stanceGeometry.SurfacePoint;
                hitNormal = stanceGeometry.Outward;
            }
            else if (isGrabPunch)
            {
                targetBody = _grabbedBody;
                hitPoint = targetBody.transform.TransformPoint(_grabbedLocalPoint);
                hitNormal = (hitPoint - targetBody.worldCenterOfMass).normalized;
            }
            else if (!TryGetAimHit(_punchRange, 0.28f, out InteractionHit hit) || hit.Body == null || hit.Body == _motor.Body)
            {
                SetStatus("PUNCH", 0.25f);
                return;
            }
            else
            {
                targetBody = hit.Body;
                hitPoint = hit.point;
                hitNormal = hit.normal;
            }

            Vector3 direction = boulderStancePush
                ? PlayerPhysics.BoulderPushDirection(stanceGeometry)
                : Vector3.ClampMagnitude(ViewTransform.forward + Vector3.up * 0.08f, 1f);
            if (hitNormal.sqrMagnitude < 0.001f)
                hitNormal = -direction;
            ResolvePuppet()?.PlayInteraction(isGrabPunch);
            bool localOnlyTarget = IsLocalOnlyBody(targetBody);
            bool comboFinisher = false;
            if (_networkRelay != null && !localOnlyTarget)
            {
                if (!TryResolveNetworkTarget(targetBody, hitPoint, out NetworkObject target,
                        out Rigidbody targetRootBody, out Vector3 targetLocalHitPoint))
                    return;
                targetBody = targetRootBody;
                _networkRelay.RequestPunch(
                    target,
                    targetLocalHitPoint,
                    targetBody.transform.InverseTransformDirection(hitNormal).normalized,
                    direction);
            }
            else
            {
                targetBody.GetComponentInParent<TrainingDummy>()?.NotifyImpact();
                Vector3 impulse;
                if (isGrabPunch)
                {
                    ResetPunchCombo(ref _localPunchComboHits, ref _localLastPunchHitTime);
                    impulse = CalculateGrabPunchImpulse(direction, _punchImpulse);
                }
                else
                {
                    PunchComboResult combo = AdvancePunchCombo(ref _localPunchComboHits,
                        ref _localLastPunchHitTime, Time.time, ConfiguredPunchCooldown);
                    comboFinisher = combo.IsFinisher;
                    _nextPunchTime = Time.time + combo.Cooldown;
                    impulse = CalculateComboPunchImpulse(direction, _punchImpulse, combo.Multiplier);
                }
                PlayerImpactCommand command = new(gameObject.GetEntityId().GetHashCode(),
                    NextSequence(ref _nextLocalImpactSequence), 0u,
                    isGrabPunch ? PlayerImpactCommand.PushAction : PlayerImpactCommand.PunchAction,
                    impulse, targetBody.transform.InverseTransformPoint(hitPoint));
                bool ownerHandlesReaction = targetBody.GetComponent<IOwnerPlayerImpactReceiver>() != null;
                PlayerImpactRouting.ApplyToLocalAuthority(targetBody, command);
                if (!ownerHandlesReaction)
                    targetBody.GetComponent<PlayerInteraction>()?.ReactFromHit(impulse);
                targetBody.GetComponentInParent<AttackDummy>()?.Provoke(_motor.Body);
                Transform impactParent = targetBody.GetComponentInParent<BoulderController>()?.PresentationRoot ??
                                         targetBody.transform;
                _impactFeedback?.Show(
                    impactParent,
                    impactParent.InverseTransformPoint(hitPoint),
                    impactParent.InverseTransformDirection(hitNormal).normalized);
            }
            if (isGrabPunch)
                EndGrabAfterPush();
            if (_networkRelay == null || localOnlyTarget)
                SetStatus(isGrabPunch ? "PUSH" : comboFinisher ? "3 HIT COMBO" : "PUNCH HIT", 0.45f);
        }

        public void ShowValidatedInteractionStatus(bool push, bool comboFinisher = false)
        {
            if (comboFinisher)
                _nextPunchTime = Mathf.Max(_nextPunchTime, Time.time + PunchComboFinisherCooldown);
            SetStatus(push ? "PUSH" : comboFinisher ? "3 HIT COMBO" : "PUNCH HIT", 0.45f);
        }

        public void ShowRejectedInteractionStatus() => SetStatus("BLOCKED", 0.3f);

        public void ShowRejectedBoulderStance(BoulderStanceResultReason reason) =>
            SetStatus(reason switch
            {
                BoulderStanceResultReason.NotGrounded => "PUSH BLOCKED: GROUND",
                BoulderStanceResultReason.Obstructed => "PUSH BLOCKED: OBSTRUCTED",
                BoulderStanceResultReason.OutOfRange => "PUSH BLOCKED: RANGE",
                BoulderStanceResultReason.TimedOut => "PUSH CONNECTION LOST",
                _ => "PUSH BLOCKED"
            }, 0.7f);

        private void EndGrabAfterPush()
        {
            _grabRequiresRelease = true;
            _grabWasHeld = true;
            ReleaseGrab();
        }

        public void ReactFromHit(Vector3 impulse) => ResolvePuppet()?.ReactToImpact(impulse);
        public void LoosenFromHit() => ResolvePuppet()?.Loosen(0.65f);

        private ActiveRagdollPuppet ResolvePuppet()
        {
            if (_puppet == null)
                _puppet = GetComponent<ActiveRagdollPuppet>();
            return _puppet;
        }
        public void GiveAnchor() => _hasAnchor = true;

        public void SetFighterThreat(bool active)
        {
            if (_fighterThreatActive == active)
                return;
            _fighterThreatActive = active;
            NotifyHudChanged();
        }

        public void ShowFighterAttack(string status)
        {
            _fighterAttackStatus = string.IsNullOrWhiteSpace(status) ? "FIGHTER ATTACK" : status;
            _fighterAttackStatusUntil = Time.time + 0.55f;
            _fighterAttackPresentationActive = true;
            NotifyHudChanged();
        }

        public void CopyConfigurationFrom(PlayerInteraction source)
        {
            if (source == null)
                return;
            _grabRange = source._grabRange;
            _grabForce = source._grabForce;
            _grabDamping = source._grabDamping;
            _maxGrabForce = source._maxGrabForce;
            _punchRange = source._punchRange;
            _punchImpulse = source.ConfiguredPunchImpulse;
            _punchCooldown = source._punchCooldown;
        }

        public static Vector3 CalculateGrabForce(Vector3 displacement, Vector3 relativeVelocity, float spring, float damping, float maximumForce)
        {
            return Vector3.ClampMagnitude(displacement * Mathf.Max(0f, spring) - relativeVelocity * Mathf.Max(0f, damping), Mathf.Max(0f, maximumForce));
        }

        public static Vector3 CalculatePunchImpulse(Vector3 direction, float requestedImpulse = PunchImpulse)
        {
            return Vector3.ClampMagnitude(direction, 1f) * Mathf.Clamp(requestedImpulse, 0f, PunchImpulse);
        }

        public static Vector3 CalculateGrabPunchImpulse(Vector3 direction, float requestedBaseImpulse = PunchImpulse)
        {
            float baseImpulse = Mathf.Clamp(requestedBaseImpulse, 0f, PunchImpulse);
            return Vector3.ClampMagnitude(direction, 1f) * baseImpulse * GrabPunchMultiplier;
        }

        public static Vector3 CalculateComboPunchImpulse(Vector3 direction,
            float requestedBaseImpulse, float multiplier)
        {
            float baseImpulse = Mathf.Clamp(requestedBaseImpulse, 0f, PunchImpulse);
            return Vector3.ClampMagnitude(direction, 1f) * baseImpulse *
                   Mathf.Clamp(multiplier, 1f, PunchComboFinisherMultiplier);
        }

        public static PunchComboResult AdvancePunchCombo(ref int acceptedHits, ref float lastAcceptedHitTime,
            float now, float baseCooldown)
        {
            if (acceptedHits < 0 || acceptedHits >= PunchComboLength ||
                !float.IsFinite(lastAcceptedHitTime) || now - lastAcceptedHitTime > PunchComboWindow)
                acceptedHits = 0;
            acceptedHits++;
            bool finisher = acceptedHits == PunchComboLength;
            int step = acceptedHits;
            lastAcceptedHitTime = now;
            if (finisher)
                acceptedHits = 0;
            return new PunchComboResult(step, finisher,
                finisher ? PunchComboFinisherMultiplier : 1f,
                finisher ? PunchComboFinisherCooldown : Mathf.Max(0.05f, baseCooldown));
        }

        public static void ResetPunchCombo(ref int acceptedHits, ref float lastAcceptedHitTime)
        {
            acceptedHits = 0;
            lastAcceptedHitTime = float.NegativeInfinity;
        }

        public static bool CanStartGrab(bool grabHeld, bool grabWasHeld, bool requiresRelease) =>
            grabHeld && !grabWasHeld && !requiresRelease;

        public static bool IsLocalOnlyBody(Rigidbody body) => body != null &&
            body.GetComponentInParent<TrainingDummy>() != null &&
            body.GetComponentInParent<NetworkObject>() == null;

        public static bool ApplyExternalImpulse(Rigidbody body, Vector3 impulse, Vector3 worldPoint,
            int sourceObjectId = 0, uint simulationTick = 0)
        {
            if (body == null)
                return false;
            IExternalImpulseReceiver receiver = body.GetComponent<IExternalImpulseReceiver>();
            if (receiver != null)
                return receiver.TryApplyExternalImpulse(simulationTick, sourceObjectId, impulse, worldPoint);
            body.AddForceAtPosition(impulse, worldPoint, ForceMode.Impulse);
            return true;
        }

        internal static uint NextSequence(ref uint sequence)
        {
            unchecked
            {
                sequence++;
                if (sequence == 0u)
                    sequence++;
            }
            return sequence;
        }

        /// <summary>
        /// Starts a server-validated player grab on the machine which owns this player.
        /// The source transform is already replicated; this owner evaluates the capped
        /// spring locally rather than accepting a stream of client-supplied forces.
        /// </summary>
        internal bool BeginOwnerGrabConstraint(NetworkObject source, int sourceObjectId, uint sequence,
            Vector3 targetLocalPoint)
        {
            if (_motor == null || !_motor.IsLocallyControlled || source == null ||
                source.ObjectId != sourceObjectId || sequence == 0u)
                return false;

            int freeSlot = -1;
            for (int index = 0; index < _ownerGrabConstraints.Length; index++)
            {
                ref OwnerGrabConstraint constraint = ref _ownerGrabConstraints[index];
                if (!constraint.Active)
                {
                    if (freeSlot < 0)
                        freeSlot = index;
                    continue;
                }
                if (constraint.SourceObjectId != sourceObjectId)
                    continue;
                if (unchecked((int)(sequence - constraint.Sequence)) <= 0)
                    return false;
                freeSlot = index;
                break;
            }

            if (freeSlot < 0)
                return false;
            _ownerGrabConstraints[freeSlot] = new OwnerGrabConstraint
            {
                Source = source,
                SourceObjectId = sourceObjectId,
                Sequence = sequence,
                TargetLocalPoint = targetLocalPoint
            };
            return true;
        }

        internal bool EndOwnerGrabConstraint(int sourceObjectId, uint sequence)
        {
            for (int index = 0; index < _ownerGrabConstraints.Length; index++)
            {
                ref OwnerGrabConstraint constraint = ref _ownerGrabConstraints[index];
                if (!constraint.Active || constraint.SourceObjectId != sourceObjectId ||
                    constraint.Sequence != sequence)
                    continue;
                constraint = default;
                return true;
            }
            return false;
        }

        private void SimulateOwnerGrabConstraints()
        {
            if (_motor?.Body == null)
                return;
            for (int index = 0; index < _ownerGrabConstraints.Length; index++)
            {
                ref OwnerGrabConstraint constraint = ref _ownerGrabConstraints[index];
                if (!constraint.Active || !constraint.Source.IsSpawned)
                {
                    constraint = default;
                    continue;
                }

                Rigidbody sourceBody = constraint.Source.GetComponent<Rigidbody>();
                if (sourceBody == null)
                {
                    constraint = default;
                    continue;
                }

                Vector3 targetPoint = _motor.Body.transform.TransformPoint(constraint.TargetLocalPoint);
                Vector3 sourceHand = sourceBody.worldCenterOfMass + constraint.Source.transform.forward * 0.85f +
                                     Vector3.up * 0.35f;
                Vector3 displacement = sourceHand - targetPoint;
                if (displacement.sqrMagnitude > ReleaseDistanceSquared)
                {
                    constraint = default;
                    continue;
                }

                Vector3 relativeVelocity = _motor.Body.GetPointVelocity(targetPoint) -
                                           sourceBody.GetPointVelocity(sourceHand);
                Vector3 pull = CalculateGrabForce(displacement, relativeVelocity, _grabForce, _grabDamping,
                    _maxGrabForce) * 0.5f;
                _motor.ApplyExternalForce(pull, ForceMode.Force);
            }
        }

        private static bool TryResolveNetworkTarget(Rigidbody hitBody, Vector3 worldPoint,
            out NetworkObject target, out Rigidbody targetBody, out Vector3 targetLocalPoint)
        {
            target = hitBody != null ? hitBody.GetComponentInParent<NetworkObject>() : null;
            targetBody = target != null ? target.GetComponent<Rigidbody>() : null;
            targetLocalPoint = targetBody != null
                ? targetBody.transform.InverseTransformPoint(worldPoint)
                : default;
            return target != null && targetBody != null;
        }

        private void TryUseOrReleaseAnchor()
        {
            BoulderController boulder = FindAnyObjectByType<BoulderController>();
            if (boulder == null || Vector3.Distance(transform.position, boulder.transform.position) > _grabRange + 1f)
                return;

            NetworkObject networkBoulder = boulder.GetComponent<NetworkObject>();
            if (_networkRelay != null && networkBoulder != null)
            {
                _networkRelay.RequestToggleAnchor(networkBoulder);
            }
            else if (_hasAnchor && !boulder.IsAnchored)
            {
                _hasAnchor = false;
                boulder.ToggleAnchor();
            }
            else if (boulder.IsAnchored)
            {
                boulder.ToggleAnchor();
            }
        }

        private string AimPrompt()
        {
            if (!TryGetAimHit(_grabRange, 0.12f, out InteractionHit hit))
                return string.Empty;
            return hit.Body != null && hit.Body.GetComponentInParent<BoulderController>() != null ? "PUSH" : "GRAB";
        }

        private bool HasLineOfSight(Vector3 destination, Transform expectedTarget)
        {
            using ProfilerMarker.AutoScope profilerScope = LineOfSightMarker.Auto();
            Vector3 origin = _motor.Body.worldCenterOfMass + Vector3.up * 0.3f;
            Vector3 delta = destination - origin;
            if (delta.sqrMagnitude < 0.001f)
                return true;
            int count = Physics.RaycastNonAlloc(origin, delta.normalized, _lineOfSightHits,
                delta.magnitude + 0.1f, GameplayLayers.InteractionQueryMask, QueryTriggerInteraction.Ignore);
            float closest = float.PositiveInfinity;
            Collider closestCollider = null;
            for (int index = 0; index < count; index++)
            {
                RaycastHit hit = _lineOfSightHits[index];
                if (hit.collider.transform.IsChildOf(transform))
                    continue;
                if (hit.distance >= closest)
                    continue;
                closest = hit.distance;
                closestCollider = hit.collider;
            }
            return closestCollider != null && closestCollider.transform.IsChildOf(expectedTarget);
        }

        private readonly struct InteractionHit
        {
            public readonly Collider Collider;
            public readonly Rigidbody Body;
            public readonly Vector3 point;
            public readonly Vector3 normal;
            public readonly float Distance;

            public InteractionHit(Collider collider, Vector3 hitPoint, Vector3 hitNormal, float distance)
            {
                Collider = collider;
                Body = collider != null ? collider.attachedRigidbody : null;
                point = hitPoint;
                normal = hitNormal;
                Distance = distance;
            }
        }

        private bool TryGetAimHit(float range, float radius, out InteractionHit result)
        {
            using ProfilerMarker.AutoScope profilerScope = AimQueryMarker.Auto();
            Transform view = ViewTransform;
            result = default;
            float bestDistance = float.PositiveInfinity;

            // SphereCast does not report colliders overlapping the cast shape at its
            // origin consistently. That is common when the camera is point-blank
            // against a large boulder, so explicitly consider those overlaps first.
            int overlapCount = Physics.OverlapSphereNonAlloc(view.position, radius + 0.03f, _aimOverlaps,
                GameplayLayers.InteractionQueryMask, QueryTriggerInteraction.Ignore);
            for (int index = 0; index < overlapCount; index++)
            {
                Collider collider = _aimOverlaps[index];
                if (!IsValidAimCollider(collider))
                    continue;
                Vector3 point = collider.ClosestPoint(view.position);
                Vector3 delta = point - view.position;
                if (Vector3.Dot(delta, view.forward) < -0.05f)
                    continue;
                Vector3 normal = SurfaceNormal(collider, point, -view.forward);
                result = new InteractionHit(collider, point, normal, 0f);
                bestDistance = 0f;
                break;
            }

            int hitCount = Physics.SphereCastNonAlloc(view.position, radius, view.forward, _aimHits, range,
                GameplayLayers.InteractionQueryMask, QueryTriggerInteraction.Ignore);
            for (int index = 0; index < hitCount; index++)
            {
                RaycastHit hit = _aimHits[index];
                if (!IsValidAimCollider(hit.collider) || hit.distance >= bestDistance)
                    continue;
                result = new InteractionHit(hit.collider, hit.point, hit.normal, hit.distance);
                bestDistance = hit.distance;
            }
            return result.Collider != null;
        }

        private bool IsValidAimCollider(Collider collider) => collider != null && !collider.transform.IsChildOf(transform);

        private static Vector3 SurfaceNormal(Collider collider, Vector3 point, Vector3 fallback)
        {
            Vector3 normal = point - collider.bounds.center;
            return normal.sqrMagnitude > 0.0001f ? normal.normalized : fallback.normalized;
        }

        private Transform ViewTransform
        {
            get
            {
                if (_cachedView == null || _cachedView == transform)
                {
                    Camera camera = Camera.main;
                    _cachedView = camera != null ? camera.transform : transform;
                }
                return _cachedView;
            }
        }

        private void SetStatus(string value, float duration)
        {
            SetPresentationStatus(value);
            _statusUntil = Time.time + duration;
        }

        private void SetPresentationStatus(string value)
        {
            value ??= string.Empty;
            if (string.Equals(_status, value, StringComparison.Ordinal))
                return;
            _status = value;
            NotifyHudChanged();
        }

        private void NotifyHudChanged()
        {
            PresentationChanged?.Invoke(this);
            HudChanged?.Invoke(HudSnapshot);
        }

        private void OnDestroy()
        {
            if (LocalHudSource != this)
                return;
            LocalHudSource = null;
            LocalHudSourceChanged?.Invoke(null);
        }
    }
}
