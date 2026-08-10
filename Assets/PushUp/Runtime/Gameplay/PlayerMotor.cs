using System.Collections.Generic;
using FishNet.Component.Transforming;
using FishNet.Object;
using FishNet.Transporting;
using FishNet.Utility.Template;
using Unity.Profiling;
using UnityEngine;

namespace PushUp.Gameplay
{
    public interface ILocalPlayerController
    {
        Rigidbody Body { get; }
        bool IsLocallyControlled { get; }
        Vector3 MoveDirection { get; }
        bool IsGrounded { get; }
        Vector3 GroundNormal { get; }
        MovementSurfaceKind GroundSurfaceKind { get; }
        bool IsPushingBoulder { get; }
        bool IsBrakingBoulder { get; }
        bool IsInBoulderPushStance { get; }
        BoulderStanceClientState BoulderStanceState { get; }
        float HorizontalSpeed { get; }
        void ApplySpeedBoost(float multiplier);
        void ApplySpeedBoost(float multiplier, float durationSeconds);
        void ApplyExternalForce(Vector3 force, ForceMode mode);
        bool TryBeginBoulderPushStance(Rigidbody boulderBody);
        void EndBoulderPushStance();
    }

    public interface INetworkInteractionRelay
    {
        void RequestDynamicGrab(NetworkObject target, Vector3 localPoint);
        void RequestBeginBoulderPush(NetworkObject target);
        void RequestEndBoulderPush();
        void RequestStaticGrab(Vector3 worldPoint);
        void RequestReleaseGrab();
        void RequestPunch(NetworkObject target, Vector3 localHitPoint, Vector3 localHitNormal, Vector3 direction);
        void RequestToggleAnchor(NetworkObject boulder);
    }

    public interface IExternalImpulseReceiver
    {
        /// <summary>Applies an authoritative impulse once for a source/tick pair.</summary>
        bool TryApplyExternalImpulse(uint simulationTick, int sourceObjectId, Vector3 impulse, Vector3 worldPoint);
    }

    public enum InteractionResultAction : byte
    {
        Punch,
        Push,
        PunchComboFinisher
    }

    public struct InteractionResultPayload
    {
        public InteractionResultAction Action;
        public NetworkObject Target;
        public Vector3 Impulse;
        public Vector3 LocalHitPoint;
        public Vector3 LocalHitNormal;
        public uint SimulationTick;

        public InteractionResultPayload(InteractionResultAction action, NetworkObject target, Vector3 impulse,
            Vector3 localHitPoint, Vector3 localHitNormal, uint simulationTick)
        {
            Action = action;
            Target = target;
            Impulse = impulse;
            LocalHitPoint = localHitPoint;
            LocalHitNormal = localHitNormal;
            SimulationTick = simulationTick;
        }
    }

    /// <summary>
    /// A bounded, low-frequency description of an owner's shared-world intent. The host uses this only
    /// to validate and simulate interactions with host-authoritative objects; it never drives or corrects
    /// the owning player's Rigidbody.
    /// </summary>
    public struct PlayerMovementIntent
    {
        public Vector2 Move;
        public bool Sprint;
        public bool BoulderPushStance;
        public ushort Yaw;
        public uint Sequence;

        public PlayerMovementIntent(Vector2 move, bool sprint, bool boulderPushStance, float yaw, uint sequence)
        {
            Move = SanitizeMove(move);
            Sprint = sprint;
            BoulderPushStance = boulderPushStance;
            Yaw = PlayerPhysics.EncodeYaw(yaw);
            Sequence = sequence;
        }

        public float YawDegrees => PlayerPhysics.DecodeYaw(Yaw);

        public PlayerMovementIntent Sanitized() =>
            new(SanitizeMove(Move), Sprint, BoulderPushStance, YawDegrees, Sequence);

        private static Vector2 SanitizeMove(Vector2 move)
        {
            if (float.IsNaN(move.x) || float.IsInfinity(move.x) ||
                float.IsNaN(move.y) || float.IsInfinity(move.y))
                return Vector2.zero;
            return Vector2.ClampMagnitude(move, 1f);
        }
    }

    public enum BoulderIntentMode : byte
    {
        None,
        Contact,
        Stance
    }

    public enum BoulderStanceClientState : byte
    {
        None,
        Pending,
        Active
    }

    public enum BoulderStanceResultReason : byte
    {
        None,
        Accepted,
        Released,
        TimedOut,
        InvalidTarget,
        OutOfRange,
        NotGrounded,
        Obstructed
    }

    public struct BoulderStanceResult
    {
        public uint Sequence;
        public uint Generation;
        public bool Accepted;
        public BoulderStanceResultReason Reason;

        public BoulderStanceResult(uint sequence, uint generation, bool accepted,
            BoulderStanceResultReason reason)
        {
            Sequence = sequence;
            Generation = generation;
            Accepted = accepted;
            Reason = reason;
        }
    }

    public struct PlayerSharedWorldIntent
    {
        public BoulderIntentMode Mode;
        public NetworkObject BoulderTarget;
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector2 Move;
        public bool Sprint;
        public ushort Yaw;
        public uint Sequence;
        public uint StanceGeneration;

        public float YawDegrees => PlayerPhysics.DecodeYaw(Yaw);

        public PlayerSharedWorldIntent(BoulderIntentMode mode, NetworkObject target, Vector3 position,
            Vector3 velocity, Vector2 move, bool sprint, float yaw, uint sequence, uint stanceGeneration)
        {
            Mode = mode;
            BoulderTarget = target;
            Position = SanitizeVector(position);
            Velocity = Vector3.ClampMagnitude(SanitizeVector(velocity), 22f);
            Move = Vector2.ClampMagnitude(float.IsFinite(move.x) && float.IsFinite(move.y) ? move : Vector2.zero, 1f);
            Sprint = sprint;
            Yaw = PlayerPhysics.EncodeYaw(yaw);
            Sequence = sequence;
            StanceGeneration = stanceGeneration;
        }

        public PlayerSharedWorldIntent Sanitized() => new(Mode <= BoulderIntentMode.Stance ? Mode : BoulderIntentMode.None,
            BoulderTarget, Position, Velocity, Move, Sprint, YawDegrees, Sequence, StanceGeneration);

        private static Vector3 SanitizeVector(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) ? value : Vector3.zero;
    }

    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider), typeof(PlayerInputReader))]
    public sealed class PlayerMotor : TickNetworkBehaviour, ILocalPlayerController, INetworkInteractionRelay,
        IExternalImpulseReceiver, IOwnerPlayerImpactReceiver
    {
        private const uint MovementIntentIntervalTicks = 2u;
        private const uint MovementIntentTimeoutTicks = 15u;
        private const uint PoseSnapshotIntervalTicks = 3u;
        private const float NetworkPoseValidationSlack = 6f;
        private const float NetworkGroundTolerance = 0.55f;
        public struct MoveInput
        {
            public Vector2 Move;
            public bool Jump;
            public bool JumpHeld;
            public bool Sprint;
            public bool CrouchHeld;
            public bool CrouchPressed;
            public bool BoulderPushStance;
            public bool LookActive;
            public ushort Yaw;

            public MoveInput(Vector2 move, bool jump, bool jumpHeld, bool sprint, bool crouchHeld, bool crouchPressed,
                bool boulderPushStance, bool lookActive, float yaw)
            {
                Move = move;
                Jump = jump;
                JumpHeld = jumpHeld;
                Sprint = sprint;
                CrouchHeld = crouchHeld;
                CrouchPressed = crouchPressed;
                BoulderPushStance = boulderPushStance;
                LookActive = lookActive;
                Yaw = PlayerPhysics.EncodeYaw(yaw);
            }

            public float YawDegrees => PlayerPhysics.DecodeYaw(Yaw);
        }

        [SerializeField] private PhysicsMaterial _movementMaterial;
        [SerializeField] private Transform _cameraPivot;

        private Rigidbody _body;
        private CapsuleCollider _capsule;
        private PlayerInputReader _input;
        private ActiveRagdollPuppet _puppet;
        private PlayerActorPhysics _actorPhysics;
        private PlayerNameplate _nameplate;
        private PunchImpactFeedback _impactFeedback;
        private Camera _localCamera;
        private float _pitch;
        private float _presentationYaw;
        private float _motorYaw;
        private bool _lookActiveSinceLastTick;
        private Quaternion _cameraPresentationRotation;
        private bool _cameraRotationInitialized;
        private bool _cameraPivotDetached;
        private Vector3 _cameraOffset;
        private float _speedMultiplier = 1f;
        private int _speedBoostTicks;
        private int _coyoteTicks;
        private int _bufferTicks;
        private bool _grounded;
        private bool _crouched;
        private bool _sliding;
        private bool _crouchBoostAvailable;
        private int _crouchBoostTicks;
        private float _standingCapsuleHeight;
        private Vector3 _standingCapsuleCenter;
        private Vector3 _standingCameraPosition;
        private Vector3 _moveDirection;
        private Vector3 _groundNormal = Vector3.up;
        private GroundContact _currentGround;
        private bool _groundedOnBoulder;
        private bool _boulderLandingArmed;
        private bool _isPushingBoulder;
        private bool _isBrakingBoulder;
        private bool _boulderPushStanceActive;
        private Rigidbody _localBoulderPushBody;
        private PlayerSharedWorldIntent _serverMovementIntent;
        private bool _serverHasMovementIntent;
        private uint _serverLastMovementIntentTick;
        private uint _lastMovementIntentSentTick;
        private uint _localMovementIntentSequence;
        private uint _localStanceGeneration;
        private NetworkObject _localBoulderPushTarget;
        private BoulderIntentMode _lastSentIntentMode;
        private NetworkObject _lastSentIntentTarget;
        private uint _lastSentStanceGeneration;
        private BoulderStanceClientState _boulderStanceClientState;
        private uint _lastPoseSnapshotSentTick;
        private uint _poseSnapshotSequence;
        private PlayerActorState _lastActorStateSent = (PlayerActorState)byte.MaxValue;
        private RemotePlayerPresentation _remotePresentation;

        private NetworkObject _serverGrabTarget;
        private NetworkObject _serverBoulderPushTarget;
        private uint _serverStanceGeneration;
        private uint _serverReleaseBarrierGeneration;
        private int _serverInvalidStanceTicks;
        private Vector3 _serverRawOwnerPosition;
        private Quaternion _serverRawOwnerRotation;
        private bool _serverHasRawOwnerPose;
        private bool _serverRawOwnerPosePending;
        private NetworkTransform _rootNetworkTransform;
        private Vector3 _serverGrabLocalPoint;
        private Vector3 _serverStaticAnchor;
        private bool _serverHasStaticAnchor;
        private uint _serverGrabConstraintSequence;
        private bool _serverGrabIsPlayerConstraint;
        private bool _serverHasAnchorPowerup;
        private float _serverNextPunchTime;
        private int _serverPunchComboHits;
        private float _serverLastPunchHitTime = float.NegativeInfinity;
        private uint _nextPlayerImpactSequence;
        private bool _serverLocalPlayer;
        private int _presentationRefreshFrames;
        private float _nextGrabReactionTime;
        private Vector3 _pendingExternalForce;
        private Vector3 _pendingExternalImpulse;
        private readonly int[] _externalImpulseSources = new int[8];
        private readonly uint[] _externalImpulseTicks = new uint[8];
        private readonly bool[] _externalImpulseSlotsUsed = new bool[8];
        private int _nextExternalImpulseSlot;
        private readonly RaycastHit[] _lineOfSightHits = new RaycastHit[16];
        private readonly Collider[] _serverBoulderCandidates = new Collider[8];
        private static readonly List<Collider> SurfaceColliders = new(16);
        private static readonly ProfilerMarker MotorTickMarker = new("PushUp.Player.NetworkMotorTick");
        private static readonly ProfilerMarker ServerLineOfSightMarker = new("PushUp.Player.ServerLineOfSight");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private uint _developmentRejectedMovementIntents;
        private uint _developmentSuspiciousProxyMovements;
        private bool _developmentProxySampleInitialized;
        private Vector3 _developmentLastProxyPosition;
        private uint _developmentLastProxyChangeTick;
        private float _developmentNextProxyWarningTime;
#endif

        public Rigidbody Body => _body;
        public bool IsLocallyControlled => ShouldBeLocallyControlled(
            NetworkObject != null && IsSpawned && IsOwner, _serverLocalPlayer);
        public Vector3 MoveDirection => _moveDirection;
        public bool IsGrounded => _grounded;
        public Vector3 GroundNormal => _groundNormal;
        public MovementSurfaceKind GroundSurfaceKind => _grounded ? _currentGround.SurfaceKind : MovementSurfaceKind.None;
        public bool IsPushingBoulder => _isPushingBoulder;
        public bool IsBrakingBoulder => _isBrakingBoulder;
        public bool IsInBoulderPushStance => _boulderPushStanceActive || _localBoulderPushBody != null;
        public BoulderStanceClientState BoulderStanceState => IsServerStarted && IsLocallyControlled &&
                                                               _localBoulderPushBody != null
            ? BoulderStanceClientState.Active
            : _boulderStanceClientState;
        public float HorizontalSpeed => _body != null ? Vector3.ProjectOnPlane(_body.linearVelocity, Vector3.up).magnitude : 0f;
        public PhysicsMaterial MovementMaterial => _movementMaterial;
        public float LookSensitivity => PlayerLookSettings.MouseSensitivity;
        public float ControllerLookSpeed => PlayerLookSettings.ControllerSensitivity;
        public int DevelopmentReconcileCount
        {
            get => 0;
        }
        public float DevelopmentLastCorrectionDistance
        {
            get => 0f;
        }
        public float DevelopmentMaxCorrectionDistance
        {
            get => 0f;
        }

        public uint DevelopmentRejectedMovementIntents
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return _developmentRejectedMovementIntents;
#else
                return 0u;
#endif
            }
        }

        public uint DevelopmentSuspiciousProxyMovementCount
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return _developmentSuspiciousProxyMovements;
#else
                return 0u;
#endif
            }
        }

        public static bool ShouldBeLocallyControlled(bool isOwner, bool isServerLocalPlayer) =>
            isOwner || isServerLocalPlayer;

        public static bool ShouldSimulateNetworkPhysics(bool isServerStarted, bool isOwner) => isOwner;

        /// <summary>
        /// The owner camera is presentation-only and must never be pulled back toward an old
        /// authoritative snapshot. Remote/server views still use the authoritative yaw.
        /// </summary>
        public static float ReconciledPresentationYaw(float currentPresentationYaw, float authoritativeYaw,
            bool isOwner) => isOwner
            ? Mathf.Repeat(currentPresentationYaw, 360f)
            : Mathf.Repeat(authoritativeYaw, 360f);

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _capsule = GetComponent<CapsuleCollider>();
            _standingCapsuleHeight = _capsule.height;
            _standingCapsuleCenter = _capsule.center;
            _input = GetComponent<PlayerInputReader>();
            if (_input == null)
                _input = gameObject.AddComponent<PlayerInputReader>();
            _input.SetLocalControlEnabled(false);
            PlayerPhysics.ConfigureBody(_body, _capsule, _movementMaterial);
            _puppet = GetComponent<ActiveRagdollPuppet>();
            _actorPhysics = GetComponent<PlayerActorPhysics>();
            _nameplate = GetComponent<PlayerNameplate>();
            if (_nameplate == null)
                _nameplate = gameObject.AddComponent<PlayerNameplate>();
            if (_actorPhysics != null)
            {
                _actorPhysics.Configure(_puppet);
                _actorPhysics.SetExternalSimulation(true);
            }
            _impactFeedback = GetComponent<PunchImpactFeedback>();
            _remotePresentation = GetComponent<RemotePlayerPresentation>();
            _rootNetworkTransform = GetComponent<NetworkTransform>();
            if (_cameraPivot != null)
            {
                _standingCameraPosition = _cameraPivot.localPosition;
                _cameraOffset = _standingCameraPosition;
            }
            _presentationYaw = transform.eulerAngles.y;
            _motorYaw = _presentationYaw;
        }

        private void Start()
        {
            if (_serverLocalPlayer)
                RefreshPresentationForOwnership();
        }

        public override void OnStartNetwork()
        {
            ResetReconcileDiagnostics();
            ConfigureNetworkPhysicsAuthority(IsLocallyControlled);
            SetTickCallbacks(TickCallback.Tick);
            _remotePresentation?.ResetBuffer();
            if (_rootNetworkTransform != null)
                _rootNetworkTransform.OnDataReceived += RootNetworkTransform_OnDataReceived;
        }

        public override void OnStartClient()
        {
            ConfigureNetworkPhysicsAuthority(IsLocallyControlled);
            RefreshPresentationForOwnership();
            // FishNet may finish detaching the graphical child after OnStartClient. Reapply
            // the desired owner/remote presentation on the next two frames so remote peers
            // never remain as the fallback capsule after a spawn or ownership change.
            _presentationRefreshFrames = 2;
            _remotePresentation?.ResetBuffer();
        }

        public override void OnOwnershipClient(FishNet.Connection.NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);
            ConfigureNetworkPhysicsAuthority(IsLocallyControlled);
            RefreshPresentationForOwnership();
            _presentationRefreshFrames = 2;
            _remotePresentation?.ResetBuffer();
        }

        private void RefreshPresentationForOwnership()
        {
            bool local = IsLocallyControlled;
            _puppet?.ConfigureLocalView(local);
            _input?.SetLocalControlEnabled(local);
            if (local)
                AttachLocalCamera();
        }

        private void ConfigureNetworkPhysicsAuthority(bool simulate)
        {
            if (_body == null)
                return;
            _actorPhysics?.SetSimulationAuthority(simulate);
            _actorPhysics?.SetExternalSimulation(true);
            if (simulate)
            {
                gameObject.layer = GameplayLayers.Player;
                _body.isKinematic = false;
                _body.interpolation = RigidbodyInterpolation.Interpolate;
                _body.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }
            else
            {
                gameObject.layer = GameplayLayers.RemotePlayerProxy;
                _body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                _body.interpolation = RigidbodyInterpolation.None;
                _body.isKinematic = true;
            }
        }

        public override void OnStopNetwork()
        {
            if (_rootNetworkTransform != null)
                _rootNetworkTransform.OnDataReceived -= RootNetworkTransform_OnDataReceived;
            _input?.SetLocalControlEnabled(false);
            _serverHasMovementIntent = false;
            _serverHasRawOwnerPose = false;
            _serverRawOwnerPosePending = false;
            ClearServerGrab();
            ClearServerBoulderPush();
            _remotePresentation?.ResetBuffer();
            base.OnStopNetwork();
        }

        private void RootNetworkTransform_OnDataReceived(NetworkTransform.TransformData previous,
            NetworkTransform.TransformData next)
        {
            if (!IsServerStarted || IsLocallyControlled)
                return;
            _serverRawOwnerPosition = transform.parent != null
                ? transform.parent.TransformPoint(next.Position)
                : next.Position;
            _serverRawOwnerRotation = transform.parent != null
                ? transform.parent.rotation * next.Rotation
                : next.Rotation;
            _serverHasRawOwnerPose = NetworkQuaternion.IsFinite(_serverRawOwnerRotation) &&
                                     float.IsFinite(_serverRawOwnerPosition.x) &&
                                     float.IsFinite(_serverRawOwnerPosition.y) &&
                                     float.IsFinite(_serverRawOwnerPosition.z);
            _serverRawOwnerPosePending = _serverHasRawOwnerPose;
        }

        private void Update()
        {
            if (_presentationRefreshFrames > 0)
            {
                _presentationRefreshFrames--;
                RefreshPresentationForOwnership();
            }
            if (IsLocallyControlled)
            {
                UpdateLook();
                UpdateCrouchPresentation();
            }
        }

        private void LateUpdate()
        {
            if (!IsLocallyControlled || _cameraPivot == null)
                return;

            Vector3 reactionOffset = _puppet != null ? _puppet.CameraReactionOffset : Vector3.zero;
            Vector3 targetPosition = transform.TransformPoint(_cameraOffset + reactionOffset);
            _cameraPivot.position = PlayerPhysics.CalculateCameraPresentationPosition(
                _cameraPivot.position, targetPosition, Time.unscaledDeltaTime);
            Quaternion reactionRotation = _puppet != null
                ? _puppet.CameraReactionRotation
                : Quaternion.identity;
            Quaternion target = Quaternion.Euler(_pitch, _presentationYaw, 0f) * reactionRotation;
            if (!_cameraRotationInitialized)
            {
                _cameraPresentationRotation = target;
                _cameraRotationInitialized = true;
            }
            _cameraPresentationRotation = PlayerPhysics.CalculateCameraPresentationRotation(
                _cameraPresentationRotation, target, Time.unscaledDeltaTime);
            _cameraPivot.rotation = _cameraPresentationRotation;
        }

        protected override void TimeManager_OnTick()
        {
            float deltaTime = TimeManager != null ? (float)TimeManager.TickDelta : Time.fixedDeltaTime;
            if (IsLocallyControlled)
            {
                MoveInput input = BuildInput();
                ApplyLocalMotor(input, deltaTime, IsServerStarted);
                SendMovementIntentIfDue(input);
                SendActorStateIfChanged(_actorPhysics != null
                    ? _actorPhysics.ActorState
                    : PlayerActorState.Locomotion);
                SendPoseSnapshotIfDue();
            }
            else if (IsServerStarted)
            {
                ApplyPendingRemoteOwnerPose();
                ObserveServerProxyMovement();
                SimulateServerSharedWorldIntent();
            }
        }

        private void ApplyPendingRemoteOwnerPose()
        {
            if (!_serverRawOwnerPosePending || !_serverHasRawOwnerPose || _body == null || !_body.isKinematic)
                return;
            _serverRawOwnerPosePending = false;
            // Keep the hidden query/collision proxy current on the server physics clock. Its
            // RemotePlayerProxy layer cannot inject contact energy into the boulder or actors;
            // the separately buffered World Rig remains the only visible representation.
            _body.MovePosition(_serverRawOwnerPosition);
            _body.MoveRotation(_serverRawOwnerRotation);
        }

        private void ResetReconcileDiagnostics()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _developmentRejectedMovementIntents = 0;
            _developmentSuspiciousProxyMovements = 0;
            _developmentProxySampleInitialized = false;
            _developmentLastProxyPosition = default;
            _developmentLastProxyChangeTick = 0u;
            _developmentNextProxyWarningTime = 0f;
#endif
        }

        public void ApplySpeedBoost(float multiplier)
        {
            if (TryRouteSpeedBoostToRemoteOwner(multiplier, 0f))
                return;
            _speedMultiplier = Mathf.Max(1f, multiplier);
            if (_speedMultiplier <= 1f)
                _speedBoostTicks = 0;
        }

        public void ApplySpeedBoost(float multiplier, float durationSeconds)
        {
            if (TryRouteSpeedBoostToRemoteOwner(multiplier, durationSeconds))
                return;
            _speedMultiplier = Mathf.Max(1f, multiplier);
            float delta = TimeManager != null ? (float)TimeManager.TickDelta : Time.fixedDeltaTime;
            _speedBoostTicks = _speedMultiplier > 1f
                ? PlayerPhysics.DurationToTicks(durationSeconds, delta)
                : 0;
        }

        private bool TryRouteSpeedBoostToRemoteOwner(float multiplier, float durationSeconds)
        {
            if (!IsServerStarted || IsLocallyControlled || Owner == null || !Owner.IsActive)
                return false;
            ApplySpeedBoostTargetRpc(Owner, multiplier, durationSeconds);
            return true;
        }

        [TargetRpc]
        private void ApplySpeedBoostTargetRpc(FishNet.Connection.NetworkConnection connection, float multiplier,
            float durationSeconds)
        {
            _speedMultiplier = Mathf.Max(1f, multiplier);
            if (_speedMultiplier <= 1f)
            {
                _speedBoostTicks = 0;
                return;
            }
            float delta = TimeManager != null ? (float)TimeManager.TickDelta : Time.fixedDeltaTime;
            _speedBoostTicks = durationSeconds > 0f
                ? PlayerPhysics.DurationToTicks(durationSeconds, delta)
                : 0;
        }

        public void ApplyExternalForce(Vector3 force, ForceMode mode)
        {
            // The server copy of a remotely-owned player is an interpolated, kinematic proxy. Owner-targeted
            // impact/grab routing applies forces on the authoritative client instead.
            if (NetworkObject != null && IsSpawned && IsServerStarted && !IsLocallyControlled)
                return;
            if (mode == ForceMode.Impulse || mode == ForceMode.VelocityChange)
                _pendingExternalImpulse += mode == ForceMode.VelocityChange ? force * _body.mass : force;
            else
                _pendingExternalForce += mode == ForceMode.Acceleration ? force * _body.mass : force;
        }

        public bool TryApplyExternalImpulse(uint simulationTick, int sourceObjectId, Vector3 impulse,
            Vector3 worldPoint)
        {
            if (simulationTick != 0 && !RegisterExternalImpulse(sourceObjectId, simulationTick))
                return false;
            ApplyExternalForce(impulse, ForceMode.Impulse);
            return true;
        }

        public bool ApplyImpact(PlayerImpactCommand command)
        {
            if (!RegisterExternalImpulse(command.SourceObjectId, command.Sequence))
                return false;
            Vector3 worldPoint = transform.TransformPoint(command.LocalHitPoint);
            if (_actorPhysics != null)
                return _actorPhysics.TryApplyImpact(command.Impulse, worldPoint, _presentationYaw);
            ApplyExternalForce(command.Impulse, ForceMode.Impulse);
            return true;
        }

        private bool RegisterExternalImpulse(int sourceObjectId, uint simulationTick)
        {
            for (int index = 0; index < _externalImpulseSources.Length; index++)
            {
                if (!_externalImpulseSlotsUsed[index] || _externalImpulseSources[index] != sourceObjectId)
                    continue;
                uint previous = _externalImpulseTicks[index];
                if (simulationTick == previous || unchecked((int)(simulationTick - previous)) <= 0)
                    return false;
                _externalImpulseTicks[index] = simulationTick;
                return true;
            }

            int slot = _nextExternalImpulseSlot;
            _nextExternalImpulseSlot = (_nextExternalImpulseSlot + 1) % _externalImpulseSources.Length;
            _externalImpulseSlotsUsed[slot] = true;
            _externalImpulseSources[slot] = sourceObjectId;
            _externalImpulseTicks[slot] = simulationTick;
            return true;
        }

        private MoveInput BuildInput()
        {
            if (!IsLocallyControlled || _input == null)
                return default;

            // Keep continuous stick input active across catch-up ticks while retaining mouse deltas until
            // at least one simulation tick has consumed the render-frame look sample.
            bool lookActive = PlayerPhysics.IsLookActiveForSimulation(_lookActiveSinceLastTick, _input.Look);
            bool inBoulderStance = _localBoulderPushBody != null;
            // Auto-facing may turn the physical body while pushing, but only explicit look input may update
            // the camera. Outside the stance the body immediately follows the owner presentation yaw.
            float requestedMotorYaw = PlayerPhysics.SelectMotorYaw(
                _presentationYaw, _motorYaw, inBoulderStance, lookActive);
            MoveInput result = new(
                Vector2.ClampMagnitude(_input.Move, 1f),
                _input.ConsumeJump(),
                _input.JumpHeld,
                _input.SprintHeld,
                _input.CrouchHeld,
                _input.ConsumeCrouchPress(),
                inBoulderStance,
                lookActive,
                requestedMotorYaw);
            _lookActiveSinceLastTick = false;
            return result;
        }

        private void ApplyLocalMotor(MoveInput input, float deltaTime, bool applyAuthoritativeWorldForces)
        {
            using ProfilerMarker.AutoScope profilerScope = MotorTickMarker.Auto();
            if (!IsLocallyControlled || _body == null || _body.isKinematic)
                return;

            _actorPhysics?.SetDesiredYaw(_presentationYaw);
            bool knockedDown = _actorPhysics != null
                ? _actorPhysics.IsMovementLocked
                : _puppet != null && _puppet.IsMovementLocked;
            if (knockedDown && _actorPhysics != null)
            {
                _localBoulderPushBody = null;
                _boulderPushStanceActive = false;
                _isPushingBoulder = false;
                _isBrakingBoulder = false;
                _moveDirection = Vector3.zero;
                _actorPhysics.Simulate(deltaTime, Time.time);
                ConsumeExternalForces();
                return;
            }
            if (knockedDown)
            {
                input.Move = Vector2.zero;
                input.Jump = false;
                input.JumpHeld = false;
                input.Sprint = false;
                input.CrouchHeld = false;
                input.CrouchPressed = false;
                input.BoulderPushStance = false;
            }

            PlayerPhysics.AdvanceTimedMultiplier(ref _speedMultiplier, ref _speedBoostTicks);
            Rigidbody stanceBody = _localBoulderPushBody;
            if (input.BoulderPushStance && stanceBody != null && IsServerStarted &&
                (_serverBoulderPushTarget == null || _serverBoulderPushTarget.GetComponent<Rigidbody>() != stanceBody ||
                PlayerPhysics.TryGetBoulderStanceGeometry(_capsule, transform, stanceBody, _groundNormal,
                    out BoulderPushStanceGeometry validationGeometry) &&
                !HasLineOfSight(validationGeometry.SurfacePoint, stanceBody.transform)))
                stanceBody = null;

            PlayerSimulationState simulationState = CaptureSimulationState(input.YawDegrees);
            PlayerSimulationInput simulationInput = new(input.Move, input.Jump, input.JumpHeld, input.Sprint,
                input.CrouchHeld, input.CrouchPressed, input.LookActive, input.YawDegrees, _speedMultiplier,
                input.BoulderPushStance ? stanceBody : null);
            PlayerSimulationStep step = PlayerPhysics.SimulatePlayerStep(_capsule, transform, _body,
                simulationInput, _standingCapsuleHeight, _standingCapsuleCenter, deltaTime, ref simulationState);
            if (knockedDown)
            {
                Vector3 ragdollVelocity = PlayerPhysics.CalculateKnockdownVelocity(_body.linearVelocity,
                    simulationState.Grounded, simulationState.GroundNormal,
                    simulationState.Grounded ? simulationState.Ground.PointVelocity : Vector3.zero, deltaTime);
                step = new PlayerSimulationStep(ragdollVelocity, step.PositionCorrection, step.Rotation,
                    Vector3.zero, step.StanceGeometry, false, false);
            }
            ApplySimulationState(simulationState, step);

            if (applyAuthoritativeWorldForces && input.BoulderPushStance &&
                !step.HasBoulderStance && _serverBoulderPushTarget != null)
                ClearServerBoulderPush();

            _body.linearVelocity = step.Velocity;
            if (step.PositionCorrection.sqrMagnitude > 0.000001f)
                _body.MovePosition(_body.position + step.PositionCorrection);
            _body.MoveRotation(step.Rotation);
            if (applyAuthoritativeWorldForces)
                SimulateServerGrab();
            ConsumeExternalForces();

            _isBrakingBoulder = PlayerPhysics.IsBoulderBrakeActive(step.StanceGeometry, input.Move);
            if (step.HasBoulderStance && applyAuthoritativeWorldForces && step.StanceGeometry.IsValid &&
                !step.StanceGeometry.Body.isKinematic)
            {
                step.StanceGeometry.Body.AddForceAtPosition(
                    PlayerPhysics.CalculateBoulderHoldForce(step.StanceGeometry, input.Move, input.Sprint),
                    step.StanceGeometry.SurfacePoint, ForceMode.Force);
                step.StanceGeometry.Body.AddTorque(
                    PlayerPhysics.CalculateBoulderHoldTorque(step.StanceGeometry, input.Move), ForceMode.Force);
            }
        }

        private void SendMovementIntentIfDue(MoveInput input)
        {
            if (!IsClientStarted || IsServerStarted || !IsOwner || TimeManager == null)
                return;

            uint tick = TimeManager.LocalTick;
            PlayerSharedWorldIntent intent = BuildSharedWorldIntent(input);
            bool changed = intent.Mode != _lastSentIntentMode || intent.BoulderTarget != _lastSentIntentTarget ||
                           intent.StanceGeneration != _lastSentStanceGeneration;
            if (!changed && unchecked(tick - _lastMovementIntentSentTick) < MovementIntentIntervalTicks)
                return;
            _lastMovementIntentSentTick = tick;
            _lastSentIntentMode = intent.Mode;
            _lastSentIntentTarget = intent.BoulderTarget;
            _lastSentStanceGeneration = intent.StanceGeneration;
            SubmitMovementIntentServerRpc(intent, changed ? Channel.Reliable : Channel.Unreliable);
        }

        private PlayerSharedWorldIntent BuildSharedWorldIntent(MoveInput input)
        {
            BoulderIntentMode mode = BoulderIntentMode.None;
            NetworkObject target = null;
            if (_localBoulderPushBody != null)
            {
                mode = BoulderIntentMode.Stance;
                target = _localBoulderPushTarget ?? _localBoulderPushBody.GetComponent<NetworkObject>();
            }
            else if (PlayerPhysics.TryFindBoulderContact(_capsule, transform, input.Move, _motorYaw,
                         out Rigidbody contactBody))
            {
                mode = BoulderIntentMode.Contact;
                target = contactBody.GetComponent<NetworkObject>();
            }
            return new PlayerSharedWorldIntent(mode, target, _body.position, _body.linearVelocity,
                input.Move, input.Sprint, _motorYaw, ++_localMovementIntentSequence, _localStanceGeneration);
        }

        private void SendImmediateSharedWorldIntent()
        {
            if (!IsClientStarted || IsServerStarted || !IsOwner || TimeManager == null)
                return;
            MoveInput input = new(Vector2.ClampMagnitude(_input != null ? _input.Move : Vector2.zero, 1f),
                false, _input != null && _input.JumpHeld, _input != null && _input.SprintHeld,
                _input != null && _input.CrouchHeld, false, _localBoulderPushBody != null,
                _lookActiveSinceLastTick, _motorYaw);
            PlayerSharedWorldIntent intent = BuildSharedWorldIntent(input);
            _lastMovementIntentSentTick = TimeManager.LocalTick;
            _lastSentIntentMode = intent.Mode;
            _lastSentIntentTarget = intent.BoulderTarget;
            _lastSentStanceGeneration = intent.StanceGeneration;
            SubmitMovementIntentServerRpc(intent, Channel.Reliable);
        }

        private void SendPoseSnapshotIfDue()
        {
            if (_puppet == null || TimeManager == null)
                return;
            uint tick = TimeManager.LocalTick;
            if (unchecked(tick - _lastPoseSnapshotSentTick) < PoseSnapshotIntervalTicks)
                return;
            _lastPoseSnapshotSentTick = tick;

            PlayerPoseSnapshot snapshot = _puppet.CapturePoseSnapshot(tick, ++_poseSnapshotSequence);
            if (IsServerStarted)
                PublishPoseSnapshotObserversRpc(snapshot);
            else if (IsClientStarted && IsOwner)
                SubmitPoseSnapshotServerRpc(snapshot);
        }

        [ServerRpc]
        private void SubmitPoseSnapshotServerRpc(PlayerPoseSnapshot snapshot,
            Channel channel = Channel.Unreliable) => PublishPoseSnapshotObserversRpc(snapshot);

        [ObserversRpc(RunLocally = true, ExcludeOwner = true)]
        private void PublishPoseSnapshotObserversRpc(PlayerPoseSnapshot snapshot,
            Channel channel = Channel.Unreliable) => _puppet?.ApplyPoseSnapshot(snapshot);

        private void SendActorStateIfChanged(PlayerActorState state)
        {
            if (state == _lastActorStateSent)
                return;
            _lastActorStateSent = state;
            ushort yaw = PlayerPhysics.EncodeYaw(_motorYaw);
            if (IsServerStarted)
                PublishActorStateObserversRpc(state, yaw);
            else if (IsClientStarted && IsOwner)
                SubmitActorStateServerRpc(state, yaw);
        }

        [ServerRpc]
        private void SubmitActorStateServerRpc(PlayerActorState state, ushort yaw)
        {
            if ((byte)state > (byte)PlayerActorState.Recovering)
                state = PlayerActorState.Locomotion;
            PublishActorStateObserversRpc(state, yaw);
        }

        [ObserversRpc(BufferLast = true, RunLocally = true, ExcludeOwner = true)]
        private void PublishActorStateObserversRpc(PlayerActorState state, ushort yaw) =>
            _actorPhysics?.ApplyObservedState(state, PlayerPhysics.DecodeYaw(yaw));

        [ServerRpc]
        private void SubmitMovementIntentServerRpc(PlayerSharedWorldIntent intent,
            Channel channel = Channel.Unreliable)
        {
            bool invalidPose = !float.IsFinite(intent.Position.x) || !float.IsFinite(intent.Position.y) ||
                               !float.IsFinite(intent.Position.z) || !float.IsFinite(intent.Velocity.x) ||
                               !float.IsFinite(intent.Velocity.y) || !float.IsFinite(intent.Velocity.z);
            if (invalidPose || intent.Velocity.sqrMagnitude > 22f * 22f + 0.01f)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                _developmentRejectedMovementIntents++;
#endif
                return;
            }

            if (_serverHasMovementIntent &&
                unchecked((int)(intent.Sequence - _serverMovementIntent.Sequence)) <= 0)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                _developmentRejectedMovementIntents++;
#endif
                return;
            }

            PlayerSharedWorldIntent sanitized = intent.Sanitized();
            if (sanitized.StanceGeneration < _serverReleaseBarrierGeneration)
                return;
            _serverMovementIntent = sanitized;
            _serverHasMovementIntent = true;
            _serverLastMovementIntentTick = TimeManager != null ? TimeManager.LocalTick : intent.Sequence;
            if (sanitized.Mode == BoulderIntentMode.None)
            {
                _serverReleaseBarrierGeneration = System.Math.Max(_serverReleaseBarrierGeneration,
                    sanitized.StanceGeneration);
                ClearServerBoulderPush(BoulderStanceResultReason.Released, false);
            }
        }

        /// <summary>
        /// Simulates only effects on host-authoritative objects. The remote owner's kinematic proxy is
        /// never moved, rotated, or accelerated here.
        /// </summary>
        private void SimulateServerSharedWorldIntent()
        {
            SimulateServerGrab();
            _isPushingBoulder = false;
            _isBrakingBoulder = false;
            if (!_serverHasMovementIntent)
                return;

            uint serverTick = TimeManager != null ? TimeManager.LocalTick : _serverLastMovementIntentTick;
            bool intentIsCurrent = unchecked(serverTick - _serverLastMovementIntentTick) <=
                                   MovementIntentTimeoutTicks;
            if (!intentIsCurrent)
            {
                ClearServerBoulderPush(BoulderStanceResultReason.TimedOut, true);
                return;
            }
            if (_serverMovementIntent.Mode == BoulderIntentMode.None ||
                _serverMovementIntent.BoulderTarget == null)
            {
                ClearServerBoulderPush(BoulderStanceResultReason.Released, false);
                TryApplyServerProximityContact(_serverMovementIntent);
                return;
            }

            if (!TryValidateSharedWorldIntent(_serverMovementIntent, out Rigidbody boulderBody,
                    out BoulderPushStanceGeometry geometry, out BoulderStanceResultReason reason))
            {
                if (_serverMovementIntent.Mode == BoulderIntentMode.Contact &&
                    TryApplyServerProximityContact(_serverMovementIntent))
                    return;
                if (_serverMovementIntent.Mode == BoulderIntentMode.Stance && ++_serverInvalidStanceTicks < 12)
                    return;
                ClearServerBoulderPush(reason, _serverMovementIntent.Mode == BoulderIntentMode.Stance);
                return;
            }
            _serverInvalidStanceTicks = 0;
            Vector2 move = Vector2.ClampMagnitude(_serverMovementIntent.Move, 1f);
            if (boulderBody.isKinematic)
                return;
            if (_serverMovementIntent.Mode == BoulderIntentMode.Contact)
            {
                Vector3 desired = PlayerPhysics.DesiredDirection(_serverMovementIntent.YawDegrees, move);
                if (Vector3.Dot(desired, geometry.Inward) < 0.35f)
                    return;
                float force = _serverMovementIntent.Sprint ? 525f : 325f;
                boulderBody.AddForceAtPosition(geometry.Inward * force, geometry.SurfacePoint, ForceMode.Force);
                return;
            }

            bool newlyAccepted = _serverBoulderPushTarget != _serverMovementIntent.BoulderTarget ||
                                 _serverStanceGeneration != _serverMovementIntent.StanceGeneration;
            _serverBoulderPushTarget = _serverMovementIntent.BoulderTarget;
            _serverStanceGeneration = _serverMovementIntent.StanceGeneration;
            if (newlyAccepted)
            {
                SendBoulderStanceResult(true, BoulderStanceResultReason.Accepted);
                SetBoulderPushStanceObserversRpc(_serverBoulderPushTarget, true);
            }
            _isPushingBoulder = move.y > 0.01f;
            _isBrakingBoulder = PlayerPhysics.IsBoulderBrakeActive(geometry, move);
            boulderBody.AddForceAtPosition(PlayerPhysics.CalculateBoulderHoldForce(geometry, move,
                _serverMovementIntent.Sprint), geometry.SurfacePoint, ForceMode.Force);
            boulderBody.AddTorque(PlayerPhysics.CalculateBoulderHoldTorque(geometry, move), ForceMode.Force);
        }

        /// <summary>
        /// Remote player proxies deliberately do not collide with the authoritative boulder. If the
        /// owner's local contact sweep or target reference misses, infer ordinary walking contact from
        /// the same bounded movement pose/input already sent to the host. This never grants stance,
        /// braking, pulling, lateral steering, or client-selected force.
        /// </summary>
        private bool TryApplyServerProximityContact(PlayerSharedWorldIntent sourceIntent)
        {
            if (sourceIntent.Mode == BoulderIntentMode.Stance || sourceIntent.Move.sqrMagnitude < 0.01f)
                return false;
            Vector3 center = sourceIntent.Position + Vector3.up * _capsule.center.y;
            int count = Physics.OverlapSphereNonAlloc(center, 4.5f, _serverBoulderCandidates,
                1 << GameplayLayers.Boulder, QueryTriggerInteraction.Ignore);
            NetworkObject nearestTarget = null;
            float nearestDistance = float.PositiveInfinity;
            for (int index = 0; index < count; index++)
            {
                Collider candidate = _serverBoulderCandidates[index];
                Rigidbody body = candidate != null ? candidate.attachedRigidbody : null;
                BoulderController boulder = body != null ? body.GetComponentInParent<BoulderController>() : null;
                NetworkObject target = boulder != null ? boulder.GetComponent<NetworkObject>() : null;
                if (body == null || body.isKinematic || target == null)
                    continue;
                float distance = (candidate.ClosestPoint(center) - center).sqrMagnitude;
                if (distance >= nearestDistance)
                    continue;
                nearestDistance = distance;
                nearestTarget = target;
            }
            if (nearestTarget == null)
                return false;

            PlayerSharedWorldIntent inferred = sourceIntent;
            inferred.Mode = BoulderIntentMode.Contact;
            inferred.BoulderTarget = nearestTarget;
            if (!TryValidateSharedWorldIntent(inferred, out Rigidbody boulderBody,
                    out BoulderPushStanceGeometry geometry, out _))
                return false;
            Vector3 desired = PlayerPhysics.DesiredDirection(inferred.YawDegrees, inferred.Move);
            if (Vector3.Dot(desired, geometry.Inward) < 0.35f)
                return false;
            float force = inferred.Sprint ? 525f : 325f;
            boulderBody.AddForceAtPosition(geometry.Inward * force, geometry.SurfacePoint, ForceMode.Force);
            return true;
        }

        private bool TryValidateSharedWorldIntent(PlayerSharedWorldIntent intent, out Rigidbody boulderBody,
            out BoulderPushStanceGeometry geometry, out BoulderStanceResultReason reason)
        {
            boulderBody = intent.BoulderTarget != null ? intent.BoulderTarget.GetComponent<Rigidbody>() : null;
            geometry = default;
            reason = BoulderStanceResultReason.InvalidTarget;
            BoulderController boulder = boulderBody != null
                ? boulderBody.GetComponentInParent<BoulderController>()
                : null;
            Collider boulderCollider = boulder != null ? boulder.GetComponent<Collider>() : null;
            if (boulderBody == null || boulderCollider == null || !boulderCollider.enabled)
                return false;
            if (intent.Velocity.magnitude > 22.01f)
            {
                reason = BoulderStanceResultReason.OutOfRange;
                return false;
            }
            Vector3 rawPosition = _serverHasRawOwnerPose ? _serverRawOwnerPosition : transform.position;
            if ((intent.Position - rawPosition).sqrMagnitude >
                NetworkPoseValidationSlack * NetworkPoseValidationSlack)
            {
                reason = BoulderStanceResultReason.OutOfRange;
                return false;
            }
            if (!TryGetIntentGround(intent.Position, out Vector3 groundNormal))
            {
                reason = BoulderStanceResultReason.NotGrounded;
                return false;
            }

            Vector3 center = intent.Position + Vector3.up * _capsule.center.y;
            Vector3 surfacePoint = boulderCollider.ClosestPoint(center);
            float halfHeight = Mathf.Max(_capsule.radius, _capsule.height * 0.5f);
            float segmentHalf = halfHeight - _capsule.radius;
            Vector3 segmentBottom = center - Vector3.up * segmentHalf;
            Vector3 segmentTop = center + Vector3.up * segmentHalf;
            Vector3 closestAxis = ClosestPointOnSegment(segmentBottom, segmentTop, surfacePoint);
            float gap = Mathf.Max(0f, Vector3.Distance(closestAxis, surfacePoint) - _capsule.radius);
            float maximumGap = intent.Mode == BoulderIntentMode.Stance ? 2.4f : 1.6f;
            if (gap > maximumGap)
            {
                reason = BoulderStanceResultReason.OutOfRange;
                return false;
            }

            Vector3 outward = Vector3.ProjectOnPlane(center - boulderBody.worldCenterOfMass, groundNormal);
            if (outward.sqrMagnitude < 0.001f)
                outward = Vector3.ProjectOnPlane(-(Quaternion.Euler(0f, intent.YawDegrees, 0f) * Vector3.forward),
                    groundNormal);
            if (outward.sqrMagnitude < 0.001f)
                return false;
            outward.Normalize();
            if (!HasIntentLineOfSight(center + Vector3.up * 0.3f, surfacePoint, boulderCollider))
            {
                reason = BoulderStanceResultReason.Obstructed;
                return false;
            }
            geometry = new BoulderPushStanceGeometry(boulder, boulderBody, surfacePoint, groundNormal,
                outward, Vector3.Cross(outward, groundNormal).normalized, gap);
            reason = BoulderStanceResultReason.Accepted;
            return true;
        }

        private bool TryGetIntentGround(Vector3 position, out Vector3 normal)
        {
            normal = Vector3.up;
            float halfHeight = Mathf.Max(_capsule.radius, _capsule.height * 0.5f);
            Vector3 origin = position + Vector3.up * 0.15f;
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, _lineOfSightHits,
                halfHeight + 0.4f, (1 << GameplayLayers.Terrain) | (1 << GameplayLayers.LegacyDefault),
                QueryTriggerInteraction.Ignore);
            float closestBottomGap = float.PositiveInfinity;
            bool found = false;
            for (int index = 0; index < count; index++)
            {
                RaycastHit hit = _lineOfSightHits[index];
                if (hit.collider == null || hit.collider.attachedRigidbody != null ||
                    !PlayerPhysics.IsWalkableNormal(hit.normal))
                    continue;
                float bottomY = position.y + _capsule.center.y - halfHeight;
                float bottomGap = Mathf.Abs(hit.point.y - bottomY);
                if (bottomGap > NetworkGroundTolerance || bottomGap >= closestBottomGap)
                    continue;
                closestBottomGap = bottomGap;
                normal = hit.normal.normalized;
                found = true;
            }
            return found;
        }

        private bool HasIntentLineOfSight(Vector3 origin, Vector3 destination, Collider targetCollider)
        {
            if (targetCollider.bounds.Contains(origin))
                return true;
            Vector3 delta = destination - origin;
            if (delta.sqrMagnitude < 0.001f)
                return true;
            int count = Physics.RaycastNonAlloc(origin, delta.normalized, _lineOfSightHits,
                delta.magnitude + 0.05f, GameplayLayers.BlockingQueryMask, QueryTriggerInteraction.Ignore);
            for (int index = 0; index < count; index++)
            {
                Collider hit = _lineOfSightHits[index].collider;
                if (hit == null || hit.transform.IsChildOf(transform) || hit == targetCollider ||
                    hit.transform.IsChildOf(targetCollider.transform))
                    continue;
                if (_lineOfSightHits[index].distance < delta.magnitude - 0.03f)
                    return false;
            }
            return true;
        }

        private static Vector3 ClosestPointOnSegment(Vector3 start, Vector3 end, Vector3 point)
        {
            Vector3 segment = end - start;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared < 0.000001f)
                return start;
            return start + segment * Mathf.Clamp01(Vector3.Dot(point - start, segment) / lengthSquared);
        }

        private void SendBoulderStanceResult(bool accepted, BoulderStanceResultReason reason)
        {
            BoulderStanceResult result = new(_serverMovementIntent.Sequence,
                _serverMovementIntent.StanceGeneration, accepted, reason);
            if (Owner != null && Owner.IsActive && !Owner.IsLocalClient)
                ReceiveBoulderStanceResultTargetRpc(Owner, result);
            else if (IsLocallyControlled)
                ApplyBoulderStanceResult(result);
        }

        [TargetRpc]
        private void ReceiveBoulderStanceResultTargetRpc(FishNet.Connection.NetworkConnection connection,
            BoulderStanceResult result) => ApplyBoulderStanceResult(result);

        private void ApplyBoulderStanceResult(BoulderStanceResult result)
        {
            if (result.Generation != _localStanceGeneration || _localBoulderPushBody == null)
                return;
            _boulderStanceClientState = result.Accepted
                ? BoulderStanceClientState.Active
                : BoulderStanceClientState.Pending;
            if (!result.Accepted)
                GetComponent<PlayerInteraction>()?.ShowRejectedBoulderStance(result.Reason);
        }

        /// <summary>
        /// Development-only visibility into obviously invalid owner movement. This deliberately never
        /// corrects, rewinds, disconnects, or otherwise takes authority away from the owning player.
        /// NetworkTransform may hold the same proxy position for multiple 60 Hz ticks, so speed is measured
        /// only when a new position arrives and across the full interval since the prior changed sample.
        /// </summary>
        private void ObserveServerProxyMovement()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!IsServerStarted || IsLocallyControlled || _body == null)
                return;

            Vector3 position = _body.position;
            uint tick = TimeManager != null ? TimeManager.LocalTick : 0u;
            if (!_developmentProxySampleInitialized)
            {
                _developmentProxySampleInitialized = true;
                _developmentLastProxyPosition = position;
                _developmentLastProxyChangeTick = tick;
                return;
            }

            Vector3 delta = position - _developmentLastProxyPosition;
            if (delta.sqrMagnitude <= 0.000001f)
                return;

            uint elapsedTicks = unchecked(tick - _developmentLastProxyChangeTick);
            float tickDelta = TimeManager != null ? (float)TimeManager.TickDelta : Time.fixedDeltaTime;
            uint boundedElapsedTicks = elapsedTicks > 0u ? elapsedTicks : 1u;
            float elapsedSeconds = Mathf.Max(tickDelta, boundedElapsedTicks * tickDelta);
            float observedSpeed = delta.magnitude / elapsedSeconds;
            bool nonFinitePosition = float.IsNaN(position.x) || float.IsInfinity(position.x) ||
                                     float.IsNaN(position.y) || float.IsInfinity(position.y) ||
                                     float.IsNaN(position.z) || float.IsInfinity(position.z);
            bool teleported = delta.sqrMagnitude > 10f * 10f;
            bool impossibleSpeed = observedSpeed > 40f;
            bool outsideDiagnosticBounds = position.y < -100f || position.y > 2000f ||
                                           Mathf.Abs(position.x) > 10000f || Mathf.Abs(position.z) > 10000f;
            if (nonFinitePosition || teleported || impossibleSpeed || outsideDiagnosticBounds)
            {
                _developmentSuspiciousProxyMovements++;
                if (Time.realtimeSinceStartup >= _developmentNextProxyWarningTime)
                {
                    _developmentNextProxyWarningTime = Time.realtimeSinceStartup + 1f;
                    Debug.LogWarning(
                        $"[PlayerMotor] Suspicious owner movement observed for {name}: " +
                        $"speed={observedSpeed:F1}m/s delta={delta.magnitude:F1}m position={position}. " +
                        "Logged for diagnostics only; the host did not correct the owner.", this);
                }
            }

            _developmentLastProxyPosition = position;
            _developmentLastProxyChangeTick = tick;
#endif
        }

        private bool RefreshServerProxyGroundState()
        {
            if (_capsule == null || _body == null)
                return false;
            _grounded = PlayerPhysics.TryGetGround(_capsule, transform, false, false,
                _body.linearVelocity.y, out _currentGround);
            _groundNormal = _grounded ? _currentGround.Normal : Vector3.up;
            _groundedOnBoulder = _grounded && _currentGround.SurfaceKind == MovementSurfaceKind.Boulder;
            return _grounded;
        }

        private PlayerSimulationState CaptureSimulationState(float yaw) => new()
        {
            CoyoteTicks = _coyoteTicks,
            BufferTicks = _bufferTicks,
            CrouchBoostTicks = _crouchBoostTicks,
            CrouchBoostAvailable = _crouchBoostAvailable,
            Crouched = _crouched,
            Sliding = _sliding,
            Grounded = _grounded,
            GroundedOnBoulder = _groundedOnBoulder,
            BoulderLandingArmed = _boulderLandingArmed,
            GroundNormal = _groundNormal,
            Ground = _currentGround,
            Yaw = yaw
        };

        private void ApplySimulationState(PlayerSimulationState state, PlayerSimulationStep step)
        {
            _coyoteTicks = state.CoyoteTicks;
            _bufferTicks = state.BufferTicks;
            _crouchBoostTicks = state.CrouchBoostTicks;
            _crouchBoostAvailable = state.CrouchBoostAvailable;
            _crouched = state.Crouched;
            _sliding = state.Sliding;
            _grounded = state.Grounded;
            _groundedOnBoulder = state.GroundedOnBoulder;
            _boulderLandingArmed = state.BoulderLandingArmed;
            _groundNormal = state.GroundNormal;
            _currentGround = state.Ground;
            _motorYaw = state.Yaw;
            _moveDirection = step.MoveDirection;
            _isPushingBoulder = step.IsPushingBoulder;
            _boulderPushStanceActive = step.HasBoulderStance;
        }

        private void ConsumeExternalForces()
        {
            if (_pendingExternalForce.sqrMagnitude > 0.000001f)
                _body.AddForce(_pendingExternalForce, ForceMode.Force);
            if (_pendingExternalImpulse.sqrMagnitude > 0.000001f)
                _body.AddForce(_pendingExternalImpulse, ForceMode.Impulse);
            _pendingExternalForce = Vector3.zero;
            _pendingExternalImpulse = Vector3.zero;
        }

        private void UpdateCrouchPresentation()
        {
            if (_cameraPivot == null)
                return;
            Vector3 target = _standingCameraPosition + Vector3.down * (_crouched ? PlayerPhysics.CrouchCameraDrop : 0f);
            _cameraOffset = Vector3.Lerp(_cameraOffset, target, 1f - Mathf.Exp(-14f * Time.deltaTime));
        }

        private void UpdateLook()
        {
            Vector2 look = _input != null ? _input.Look : Vector2.zero;
            if (look.sqrMagnitude > 0.0001f)
            {
                _lookActiveSinceLastTick = true;
                Vector2 delta = PlayerPhysics.CalculateLookDelta(look, _input.LookUsesRate,
                    PlayerLookSettings.MouseSensitivity, PlayerLookSettings.ControllerSensitivity,
                    Time.unscaledDeltaTime);
                _presentationYaw = Mathf.Repeat(_presentationYaw + delta.x, 360f);
                _pitch = Mathf.Clamp(_pitch - delta.y, -75f, 75f);
            }
        }

        private void AttachLocalCamera()
        {
            _localCamera = Camera.main;
            if (_localCamera == null || _cameraPivot == null)
                return;
            if (!_cameraPivotDetached)
            {
                _cameraPivot.SetParent(null, true);
                _cameraPivotDetached = true;
                _cameraPivot.position = transform.TransformPoint(_cameraOffset);
            }
            _localCamera.transform.SetParent(_cameraPivot, false);
            _localCamera.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            _cameraPresentationRotation = Quaternion.Euler(_pitch, _presentationYaw, 0f);
            _cameraRotationInitialized = true;
            _cameraPivot.rotation = _cameraPresentationRotation;
        }

        public void ConfigureAsServerLocalPlayer()
        {
            _serverLocalPlayer = true;
            ConfigureNetworkPhysicsAuthority(true);
            _input?.SetLocalControlEnabled(true);
            _puppet?.ConfigureLocalView(true);
            AttachLocalCamera();
        }

        public void SetServerPlayerIdentity(ulong steamId, string displayName)
        {
            if (!IsServerStarted)
                return;
            SetPlayerIdentityObserversRpc(steamId,
                PlayerNameplate.SanitizeDisplayName(displayName,
                    steamId != 0UL ? steamId.ToString() : "Player"));
        }

        [ObserversRpc(BufferLast = true, RunLocally = true)]
        private void SetPlayerIdentityObserversRpc(ulong steamId, string displayName)
        {
            _nameplate ??= GetComponent<PlayerNameplate>();
            _nameplate?.SetIdentity(steamId, displayName);
        }

        private void OnDestroy()
        {
            if (!_cameraPivotDetached || _cameraPivot == null)
                return;
            if (_localCamera != null && _localCamera.transform.IsChildOf(_cameraPivot))
                _localCamera.transform.SetParent(null, true);
            if (Application.isPlaying)
                Destroy(_cameraPivot.gameObject);
            else
                DestroyImmediate(_cameraPivot.gameObject);
        }

        public bool TryBeginBoulderPushStance(Rigidbody boulderBody)
        {
            if (boulderBody == null || !_grounded || _groundedOnBoulder ||
                boulderBody.GetComponentInParent<BoulderController>() == null)
                return false;
            if (!PlayerPhysics.TryGetBoulderStanceGeometry(_capsule, transform, boulderBody, _groundNormal,
                    out BoulderPushStanceGeometry geometry) ||
                geometry.SurfaceGap > PlayerPhysics.BoulderStanceMaximumGap)
                return false;
            _localBoulderPushBody = boulderBody;
            _localBoulderPushTarget = boulderBody.GetComponent<NetworkObject>();
            _localStanceGeneration++;
            _boulderStanceClientState = IsServerStarted
                ? BoulderStanceClientState.Active
                : BoulderStanceClientState.Pending;
            _boulderPushStanceActive = true;
            return true;
        }

        public void EndBoulderPushStance()
        {
            _localBoulderPushBody = null;
            _localBoulderPushTarget = null;
            _localStanceGeneration++;
            _boulderStanceClientState = BoulderStanceClientState.None;
            _boulderPushStanceActive = false;
            _isPushingBoulder = false;
            _isBrakingBoulder = false;
        }

        public void RequestDynamicGrab(NetworkObject target, Vector3 localPoint)
        {
            if (IsServerStarted && !IsClientStarted) BeginDynamicGrabOnServer(target, localPoint);
            else BeginDynamicGrabServerRpc(target, localPoint);
        }

        public void RequestBeginBoulderPush(NetworkObject target)
        {
            _localBoulderPushTarget = target;
            if (IsServerStarted && IsLocallyControlled)
            {
                _serverBoulderPushTarget = target;
                _serverStanceGeneration = _localStanceGeneration;
                _boulderStanceClientState = BoulderStanceClientState.Active;
                SetBoulderPushStanceObserversRpc(target, true);
            }
            else
                SendImmediateSharedWorldIntent();
        }

        public void RequestEndBoulderPush()
        {
            if (IsServerStarted && IsLocallyControlled)
                ClearServerBoulderPush(BoulderStanceResultReason.Released, false);
            else
                SendImmediateSharedWorldIntent();
        }

        public void RequestStaticGrab(Vector3 worldPoint)
        {
            if (IsServerStarted && !IsClientStarted) BeginStaticGrabOnServer(worldPoint);
            else BeginStaticGrabServerRpc(worldPoint);
        }

        public void RequestReleaseGrab()
        {
            if (IsServerStarted && !IsClientStarted) ClearServerGrab();
            else ReleaseGrabServerRpc();
        }

        public void RequestPunch(NetworkObject target, Vector3 localHitPoint, Vector3 localHitNormal, Vector3 direction)
        {
            if (IsServerStarted && !IsClientStarted) PunchOnServer(target, localHitPoint, localHitNormal, direction);
            else PunchServerRpc(target, localHitPoint, localHitNormal, direction);
        }

        public void RequestToggleAnchor(NetworkObject boulder)
        {
            if (IsServerStarted && !IsClientStarted) ToggleAnchorOnServer(boulder);
            else ToggleAnchorServerRpc(boulder);
        }
        public void GrantAnchor() => _serverHasAnchorPowerup = true;

        [ServerRpc]
        private void BeginDynamicGrabServerRpc(NetworkObject target, Vector3 localPoint)
        {
            BeginDynamicGrabOnServer(target, localPoint);
        }

        private void BeginDynamicGrabOnServer(NetworkObject target, Vector3 localPoint)
        {
            if (!TryValidateTarget(target, 3.65f, out Rigidbody targetBody))
                return;
            if (targetBody.GetComponentInParent<BoulderController>() != null)
                return;
            Vector3 point = targetBody.transform.TransformPoint(localPoint);
            if ((point - transform.position).sqrMagnitude > 3.65f * 3.65f)
                return;
            ClearServerGrab();
            ClearServerBoulderPush();
            _serverGrabTarget = target;
            _serverGrabLocalPoint = localPoint;
            _serverHasStaticAnchor = false;
            if (target.GetComponent<PlayerMotor>() != null)
            {
                _serverGrabConstraintSequence = PlayerInteraction.NextSequence(ref _serverGrabConstraintSequence);
                _serverGrabIsPlayerConstraint = true;
                RouteBeginPlayerGrabToOwner(target, localPoint, _serverGrabConstraintSequence);
            }
            targetBody.GetComponentInParent<AttackDummy>()?.Provoke(_body);
            bool grabbingBoulder = targetBody.GetComponentInParent<BoulderController>() != null;
            SetGrabPoseObserversRpc(true, grabbingBoulder);
        }

        private void RouteBeginPlayerGrabToOwner(NetworkObject target, Vector3 targetLocalPoint, uint sequence)
        {
            FishNet.Connection.NetworkConnection owner = target != null ? target.Owner : null;
            if (owner != null && owner.IsActive && !owner.IsLocalClient)
            {
                BeginPlayerGrabTargetRpc(owner, target, targetLocalPoint, sequence);
                return;
            }
            target?.GetComponent<PlayerInteraction>()?.BeginOwnerGrabConstraint(
                NetworkObject, ObjectId, sequence, targetLocalPoint);
        }

        [TargetRpc]
        private void BeginPlayerGrabTargetRpc(FishNet.Connection.NetworkConnection connection,
            NetworkObject target, Vector3 targetLocalPoint, uint sequence)
        {
            if (target == null || !target.IsOwner)
                return;
            target.GetComponent<PlayerInteraction>()?.BeginOwnerGrabConstraint(
                NetworkObject, ObjectId, sequence, targetLocalPoint);
        }

        private void RouteEndPlayerGrabToOwner(NetworkObject target, uint sequence)
        {
            FishNet.Connection.NetworkConnection owner = target != null ? target.Owner : null;
            if (owner != null && owner.IsActive && !owner.IsLocalClient)
            {
                EndPlayerGrabTargetRpc(owner, target, sequence);
                return;
            }
            target?.GetComponent<PlayerInteraction>()?.EndOwnerGrabConstraint(ObjectId, sequence);
        }

        [TargetRpc]
        private void EndPlayerGrabTargetRpc(FishNet.Connection.NetworkConnection connection,
            NetworkObject target, uint sequence)
        {
            if (target == null || !target.IsOwner)
                return;
            target.GetComponent<PlayerInteraction>()?.EndOwnerGrabConstraint(ObjectId, sequence);
        }

        [ServerRpc]
        private void BeginStaticGrabServerRpc(Vector3 worldPoint)
        {
            BeginStaticGrabOnServer(worldPoint);
        }

        private void BeginStaticGrabOnServer(Vector3 worldPoint)
        {
            if ((worldPoint - transform.position).sqrMagnitude > 3.65f * 3.65f || !HasLineOfSight(worldPoint, null))
                return;
            ClearServerBoulderPush();
            _serverGrabTarget = null;
            _serverStaticAnchor = worldPoint;
            _serverHasStaticAnchor = true;
            SetGrabPoseObserversRpc(true, false);
        }

        [ServerRpc]
        private void ReleaseGrabServerRpc() => ClearServerGrab();

        [ServerRpc]
        private void PunchServerRpc(NetworkObject target, Vector3 localHitPoint, Vector3 localHitNormal, Vector3 direction)
        {
            PunchOnServer(target, localHitPoint, localHitNormal, direction);
        }

        private void PunchOnServer(NetworkObject target, Vector3 localHitPoint, Vector3 localHitNormal, Vector3 direction)
        {
            if (Time.time < _serverNextPunchTime)
            {
                RejectInteractionToOwner();
                return;
            }

            Rigidbody targetBody = target != null ? target.GetComponent<Rigidbody>() : null;
            bool boulderTarget = targetBody != null && targetBody.GetComponentInParent<BoulderController>() != null;
            Vector3 hitPoint;
            BoulderPushStanceGeometry stanceGeometry = default;
            bool requestedBoulderPush = boulderTarget &&
                                        (IsLocallyControlled
                                            ? _localBoulderPushTarget == target && _localBoulderPushBody != null
                                            : _serverMovementIntent.Mode == BoulderIntentMode.Stance &&
                                              _serverMovementIntent.BoulderTarget == target);
            if (boulderTarget)
            {
                PlayerSharedWorldIntent validationIntent = IsLocallyControlled
                    ? new PlayerSharedWorldIntent(_localBoulderPushBody != null
                            ? BoulderIntentMode.Stance
                            : BoulderIntentMode.Contact,
                        target, _body.position, _body.linearVelocity,
                        _input != null ? _input.Move : Vector2.zero,
                        _input != null && _input.SprintHeld, _motorYaw, 0u, _localStanceGeneration)
                    : _serverMovementIntent;
                validationIntent.BoulderTarget = target;
                validationIntent.Mode = requestedBoulderPush ? BoulderIntentMode.Stance : BoulderIntentMode.Contact;
                if (!TryValidateBoulderPunch(validationIntent, targetBody, out stanceGeometry))
                {
                    RejectInteractionToOwner();
                    return;
                }
                if (requestedBoulderPush && !IsLocallyControlled && (_serverBoulderPushTarget != target ||
                    _serverStanceGeneration != _serverMovementIntent.StanceGeneration))
                {
                    RejectInteractionToOwner();
                    return;
                }
                hitPoint = stanceGeometry.SurfacePoint;
            }
            else if (!TryValidateTargetPoint(target, localHitPoint, 3.1f, out targetBody, out hitPoint))
            {
                RejectInteractionToOwner();
                return;
            }

            Vector3 safeDirection = Vector3.ClampMagnitude(direction, 1f);
            // Derive the bonus from the server's validated grab state, never from a
            // client supplied multiplier. Static terrain braces do not qualify.
            bool boulderStancePush = requestedBoulderPush && _serverBoulderPushTarget == target;
            bool isPush = _serverGrabTarget == target || boulderStancePush;
            float configuredCooldown = GetComponent<PlayerInteraction>()?.ConfiguredPunchCooldown ?? 0.2f;
            PunchComboResult combo = default;
            if (isPush)
            {
                PlayerInteraction.ResetPunchCombo(ref _serverPunchComboHits, ref _serverLastPunchHitTime);
                _serverNextPunchTime = Time.time + configuredCooldown;
            }
            else
            {
                combo = PlayerInteraction.AdvancePunchCombo(ref _serverPunchComboHits,
                    ref _serverLastPunchHitTime, Time.time, configuredCooldown);
                _serverNextPunchTime = Time.time + combo.Cooldown;
            }
            Vector3 validatedWorldNormal = hitPoint - targetBody.worldCenterOfMass;
            if (boulderStancePush)
            {
                safeDirection = PlayerPhysics.BoulderPushDirection(stanceGeometry);
                hitPoint = stanceGeometry.SurfacePoint;
                validatedWorldNormal = stanceGeometry.Outward;
            }
            float configuredImpulse = GetComponent<PlayerInteraction>()?.ConfiguredPunchImpulse ?? PlayerInteraction.PunchImpulse;
            Vector3 impulse = isPush
                ? PlayerInteraction.CalculateGrabPunchImpulse(safeDirection, configuredImpulse)
                : PlayerInteraction.CalculateComboPunchImpulse(safeDirection, configuredImpulse,
                    combo.Multiplier);
            uint interactionTick = TimeManager != null ? TimeManager.Tick : 0u;
            bool ownerAuthoritativePlayer = targetBody.GetComponent<IOwnerPlayerImpactReceiver>() != null;
            if (ownerAuthoritativePlayer)
            {
                PlayerImpactCommand command = new(ObjectId,
                    PlayerInteraction.NextSequence(ref _nextPlayerImpactSequence), interactionTick,
                    isPush ? PlayerImpactCommand.PushAction : PlayerImpactCommand.PunchAction,
                    impulse, targetBody.transform.InverseTransformPoint(hitPoint));
                RoutePlayerImpactToOwner(target, targetBody, command);
            }
            else
            {
                PlayerInteraction.ApplyExternalImpulse(targetBody, impulse, hitPoint, ObjectId, interactionTick);
            }
            targetBody.GetComponentInParent<AttackDummy>()?.Provoke(_body);
            if (validatedWorldNormal.sqrMagnitude < 0.001f)
                validatedWorldNormal = -safeDirection;
            Vector3 safeLocalNormal = targetBody.transform.InverseTransformDirection(validatedWorldNormal.normalized);
            Vector3 validatedLocalHitPoint = targetBody.transform.InverseTransformPoint(hitPoint);
            PublishInteractionResultObserversRpc(new InteractionResultPayload(
                isPush ? InteractionResultAction.Push : combo.IsFinisher
                    ? InteractionResultAction.PunchComboFinisher
                    : InteractionResultAction.Punch,
                target, impulse, validatedLocalHitPoint, safeLocalNormal, interactionTick));
            if (boulderStancePush)
                ClearServerBoulderPush();
            else if (isPush)
                ClearServerGrab();
        }

        private bool TryValidateBoulderPunch(PlayerSharedWorldIntent intent, Rigidbody targetBody,
            out BoulderPushStanceGeometry geometry)
        {
            geometry = default;
            if (targetBody == null || intent.Velocity.magnitude > 22.01f)
                return false;
            Vector3 raw = _serverHasRawOwnerPose ? _serverRawOwnerPosition : transform.position;
            if ((intent.Position - raw).sqrMagnitude >
                    NetworkPoseValidationSlack * NetworkPoseValidationSlack || !TryGetIntentGround(intent.Position,
                    out Vector3 groundNormal))
                return false;
            Collider collider = targetBody.GetComponent<Collider>();
            BoulderController boulder = targetBody.GetComponentInParent<BoulderController>();
            if (collider == null || boulder == null)
                return false;
            Vector3 center = intent.Position + Vector3.up * _capsule.center.y;
            Vector3 surface = collider.ClosestPoint(center);
            if ((surface - center).sqrMagnitude > 4f * 4f ||
                !HasIntentLineOfSight(center + Vector3.up * 0.3f, surface, collider))
                return false;
            Vector3 outward = Vector3.ProjectOnPlane(center - targetBody.worldCenterOfMass, groundNormal);
            if (outward.sqrMagnitude < 0.001f)
                return false;
            outward.Normalize();
            geometry = new BoulderPushStanceGeometry(boulder, targetBody, surface, groundNormal, outward,
                Vector3.Cross(outward, groundNormal).normalized, Vector3.Distance(center, surface));
            return true;
        }

        private void RejectInteractionToOwner()
        {
            if (Owner != null && Owner.IsActive && !Owner.IsLocalClient)
                RejectInteractionTargetRpc(Owner);
            else if (IsLocallyControlled)
                GetComponent<PlayerInteraction>()?.ShowRejectedInteractionStatus();
        }

        [TargetRpc]
        private void RejectInteractionTargetRpc(FishNet.Connection.NetworkConnection connection) =>
            GetComponent<PlayerInteraction>()?.ShowRejectedInteractionStatus();

        private void RoutePlayerImpactToOwner(NetworkObject target, Rigidbody targetBody,
            PlayerImpactCommand command)
        {
            FishNet.Connection.NetworkConnection owner = target != null ? target.Owner : null;
            if (owner != null && owner.IsActive && !owner.IsLocalClient)
            {
                ApplyPlayerImpactTargetRpc(owner, target, command);
                return;
            }
            PlayerImpactRouting.ApplyToLocalAuthority(targetBody, command);
        }

        [TargetRpc]
        private void ApplyPlayerImpactTargetRpc(FishNet.Connection.NetworkConnection connection,
            NetworkObject target, PlayerImpactCommand command)
        {
            if (target == null || !target.IsOwner)
                return;
            PlayerImpactRouting.ApplyToLocalAuthority(target.GetComponent<Rigidbody>(), command);
        }

        [ObserversRpc(RunLocally = true)]
        private void PublishInteractionResultObserversRpc(InteractionResultPayload result)
        {
            bool isPush = result.Action == InteractionResultAction.Push;
            bool comboFinisher = result.Action == InteractionResultAction.PunchComboFinisher;
            if (IsLocallyControlled)
                GetComponent<PlayerInteraction>()?.ShowValidatedInteractionStatus(isPush, comboFinisher);
            if (!IsLocallyControlled)
                _puppet?.PlayInteraction(isPush);
            NetworkObject target = result.Target;
            if (target == null)
                return;
            if (target.GetComponent<IOwnerPlayerImpactReceiver>() == null)
                target.GetComponent<PlayerInteraction>()?.ReactFromHit(result.Impulse);
            Transform impactParent = target.GetComponent<BoulderController>()?.PresentationRoot ?? target.transform;
            Vector3 worldPoint = target.transform.TransformPoint(result.LocalHitPoint);
            Vector3 worldNormal = target.transform.TransformDirection(result.LocalHitNormal);
            _impactFeedback?.Show(impactParent, impactParent.InverseTransformPoint(worldPoint),
                impactParent.InverseTransformDirection(worldNormal));
        }

        [ObserversRpc(BufferLast = true, ExcludeOwner = true)]
        private void SetGrabPoseObserversRpc(bool active, bool grabbingBoulder) =>
            _puppet?.SetGrabPose(active, grabbingBoulder);

        [ObserversRpc(BufferLast = true)]
        private void SetBoulderPushStanceObserversRpc(NetworkObject target, bool active)
        {
            if (IsOwner)
            {
                if (active && target != null)
                {
                    _localBoulderPushBody = target.GetComponent<Rigidbody>();
                    _localBoulderPushTarget = target;
                    _boulderPushStanceActive = _localBoulderPushBody != null;
                    _boulderStanceClientState = _boulderPushStanceActive
                        ? BoulderStanceClientState.Active
                        : BoulderStanceClientState.Pending;
                }
                else if (_localBoulderPushBody != null)
                {
                    // A transient host rejection must not synthesize an RMB release. Keep aligning and
                    // allow repeated intents to reacquire while the physical button remains held.
                    _boulderStanceClientState = BoulderStanceClientState.Pending;
                    _boulderPushStanceActive = true;
                    return;
                }
            }
            _puppet?.SetGrabPose(active, active);
        }

        [ServerRpc]
        private void ToggleAnchorServerRpc(NetworkObject boulder)
        {
            ToggleAnchorOnServer(boulder);
        }

        private void ToggleAnchorOnServer(NetworkObject boulder)
        {
            if (!TryValidateTarget(boulder, 4.2f, out Rigidbody targetBody))
                return;
            BoulderController controller = targetBody.GetComponent<BoulderController>();
            if (controller == null)
                return;
            if (controller.IsAnchored)
            {
                controller.ToggleAnchor();
            }
            else if (_serverHasAnchorPowerup)
            {
                _serverHasAnchorPowerup = false;
                controller.ToggleAnchor();
            }
        }

        private void SimulateServerGrab()
        {
            Vector3 hand = _body.worldCenterOfMass + transform.forward * 0.85f + Vector3.up * 0.35f;
            if (_serverHasStaticAnchor)
            {
                Vector3 delta = _serverStaticAnchor - hand;
                if (delta.sqrMagnitude > PlayerInteraction.ReleaseDistanceSquared)
                {
                    ClearServerGrab();
                    return;
                }
                Vector3 force = PlayerInteraction.CalculateGrabForce(delta, _body.linearVelocity, 650f, 40f, 900f);
                if (!_body.isKinematic)
                    _body.AddForceAtPosition(force, hand, ForceMode.Force);
                return;
            }

            if (_serverGrabTarget == null)
                return;
            Rigidbody target = _serverGrabTarget.GetComponent<Rigidbody>();
            if (target == null)
            {
                ClearServerGrab();
                return;
            }

            Vector3 point = target.transform.TransformPoint(_serverGrabLocalPoint);
            Vector3 deltaToHand = hand - point;
            if (deltaToHand.sqrMagnitude > PlayerInteraction.ReleaseDistanceSquared)
            {
                ClearServerGrab();
                return;
            }
            Vector3 relativeVelocity = target.GetPointVelocity(point) - _body.GetPointVelocity(hand);
            float multiplier = target.GetComponent<PlayerInteraction>() != null ? 0.5f : 1f;
            Vector3 pull = PlayerInteraction.CalculateGrabForce(deltaToHand, relativeVelocity, 650f, 40f, 1100f) * multiplier;
            PlayerMotor targetMotor = target.GetComponent<PlayerMotor>();
            if (targetMotor != null)
            {
                // Every player target evaluates the validated persistent spring on its simulation owner.
                // Applying here would either push a kinematic proxy or double-apply against the host player.
            }
            else
                target.AddForceAtPosition(pull, point, ForceMode.Force);
            Vector3 reaction = -pull * 0.34f;
            if (_grounded && target.GetComponentInParent<BoulderController>() != null)
                reaction = Vector3.ProjectOnPlane(reaction, _groundNormal);
            if (!_body.isKinematic)
                _body.AddForceAtPosition(reaction, hand, ForceMode.Force);
        }

        // Grabs are a continuous force, so their visual hit reaction is intentionally
        // rate-limited. This gives a grabbed player a readable wobble/knockdown without
        // resetting the get-up timer every simulation tick.
        private void NotifyAuthoritativeGrabForce(Vector3 force)
        {
            if (!IsServerStarted || force.sqrMagnitude < 250f * 250f || Time.time < _nextGrabReactionTime)
                return;
            _nextGrabReactionTime = Time.time + 0.45f;
            PlayGrabReactionObserversRpc(force);
        }

        [ObserversRpc(RunLocally = true)]
        private void PlayGrabReactionObserversRpc(Vector3 force) => _puppet?.ReactToImpact(force);

        private bool TryValidateTarget(NetworkObject target, float range, out Rigidbody targetBody)
        {
            targetBody = target != null ? target.GetComponent<Rigidbody>() : null;
            if (targetBody == null || targetBody == _body)
                return false;
            Vector3 origin = _body.worldCenterOfMass + Vector3.up * 0.3f;
            if (!TryGetClosestSurfacePoint(targetBody, origin, out Vector3 destination))
                return false;
            return (destination - transform.position).sqrMagnitude <= range * range && HasLineOfSight(destination, target.transform);
        }

        private bool TryValidateTargetPoint(NetworkObject target, Vector3 localPoint, float range, out Rigidbody targetBody, out Vector3 surfacePoint)
        {
            targetBody = target != null ? target.GetComponent<Rigidbody>() : null;
            surfacePoint = default;
            if (targetBody == null || targetBody == _body)
                return false;

            Vector3 claimedPoint = targetBody.transform.TransformPoint(localPoint);
            if ((claimedPoint - transform.position).sqrMagnitude > range * range ||
                !TryGetClosestSurfacePoint(targetBody, claimedPoint, out surfacePoint) ||
                (surfacePoint - claimedPoint).sqrMagnitude > 0.35f * 0.35f)
                return false;
            return HasLineOfSight(surfacePoint, target.transform);
        }

        public static bool TryGetClosestSurfacePoint(Rigidbody targetBody, Vector3 point, out Vector3 surfacePoint)
        {
            surfacePoint = default;
            if (targetBody == null)
                return false;

            float bestDistance = float.PositiveInfinity;
            bool found = false;
            SurfaceColliders.Clear();
            targetBody.GetComponentsInChildren(false, SurfaceColliders);
            foreach (Collider collider in SurfaceColliders)
            {
                if (!collider.enabled || collider.isTrigger)
                    continue;
                Vector3 candidate = collider.ClosestPoint(point);
                float distance = (candidate - point).sqrMagnitude;
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                surfacePoint = candidate;
                found = true;
            }
            return found;
        }

        private bool HasLineOfSight(Vector3 destination, Transform expectedTarget)
        {
            using ProfilerMarker.AutoScope profilerScope = ServerLineOfSightMarker.Auto();
            Vector3 origin = _body.worldCenterOfMass + Vector3.up * 0.3f;
            Vector3 delta = destination - origin;
            if (delta.sqrMagnitude < 0.001f)
                return true;
            int count = Physics.RaycastNonAlloc(origin, delta.normalized, _lineOfSightHits,
                delta.magnitude + 0.1f, GameplayLayers.InteractionQueryMask, QueryTriggerInteraction.Ignore);
            float closest = float.PositiveInfinity;
            Collider closestCollider = null;
            Vector3 closestPoint = default;
            for (int index = 0; index < count; index++)
            {
                RaycastHit hit = _lineOfSightHits[index];
                if (hit.collider.transform.IsChildOf(transform))
                    continue;
                if (hit.distance >= closest)
                    continue;
                closest = hit.distance;
                closestCollider = hit.collider;
                closestPoint = hit.point;
            }
            if (closestCollider == null)
                return expectedTarget == null;
            return expectedTarget != null
                ? closestCollider.transform.IsChildOf(expectedTarget)
                : (closestPoint - destination).sqrMagnitude <= 0.16f;
        }

        private void ClearServerGrab()
        {
            bool hadGrab = _serverGrabTarget != null || _serverHasStaticAnchor;
            if (_serverGrabIsPlayerConstraint && _serverGrabTarget != null)
                RouteEndPlayerGrabToOwner(_serverGrabTarget, _serverGrabConstraintSequence);
            _serverGrabTarget = null;
            _serverHasStaticAnchor = false;
            _serverGrabIsPlayerConstraint = false;
            if (hadGrab && IsServerStarted)
                SetGrabPoseObserversRpc(false, false);
        }

        private void ClearServerBoulderPush() =>
            ClearServerBoulderPush(BoulderStanceResultReason.Released, false);

        private void ClearServerBoulderPush(BoulderStanceResultReason reason, bool notifyRejection)
        {
            bool hadStance = _serverBoulderPushTarget != null;
            if (notifyRejection && _serverMovementIntent.Mode == BoulderIntentMode.Stance)
                SendBoulderStanceResult(false, reason);
            _serverBoulderPushTarget = null;
            _serverInvalidStanceTicks = 0;
            _boulderPushStanceActive = false;
            _isPushingBoulder = false;
            _isBrakingBoulder = false;
            if (hadStance && IsServerStarted)
                SetBoulderPushStanceObserversRpc(null, false);
        }
    }

    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider), typeof(PlayerInputReader))]
    public sealed class StandalonePlayerController : MonoBehaviour, ILocalPlayerController, IExternalImpulseReceiver,
        IOwnerPlayerImpactReceiver
    {
        private float _lookSensitivity = 0.12f;
        private float _controllerLookSpeed = PlayerPhysics.ControllerLookSpeed;
        private Rigidbody _body;
        private CapsuleCollider _capsule;
        private PlayerInputReader _input;
        private Transform _cameraPivot;
        private float _pitch;
        private float _targetYaw;
        private Quaternion _cameraPresentationRotation;
        private bool _lookActive;
        private float _speedMultiplier = 1f;
        private int _speedBoostTicks;
        private int _coyoteTicks;
        private int _bufferTicks;
        private bool _jumpQueued;
        private bool _crouchQueued;
        private bool _grounded;
        private bool _crouched;
        private bool _sliding;
        private bool _crouchBoostAvailable;
        private int _crouchBoostTicks;
        private float _standingCapsuleHeight;
        private Vector3 _standingCapsuleCenter;
        private Vector3 _standingCameraPosition;
        private Vector3 _cameraOffset;
        private Vector3 _moveDirection;
        private Vector3 _groundNormal = Vector3.up;
        private GroundContact _currentGround;
        private bool _groundedOnBoulder;
        private bool _boulderLandingArmed;
        private bool _isPushingBoulder;
        private bool _isBrakingBoulder;
        private bool _boulderPushStanceActive;
        private Rigidbody _boulderPushBody;
        private PhysicsMaterial _movementMaterial;
        private ActiveRagdollPuppet _puppet;
        private PlayerActorPhysics _actorPhysics;
        private readonly Dictionary<int, uint> _impactSequences = new();

        public Rigidbody Body => _body;
        public bool IsLocallyControlled => true;
        public Vector3 MoveDirection => _moveDirection;
        public bool IsGrounded => _grounded;
        public Vector3 GroundNormal => _groundNormal;
        public MovementSurfaceKind GroundSurfaceKind => _grounded ? _currentGround.SurfaceKind : MovementSurfaceKind.None;
        public bool IsPushingBoulder => _isPushingBoulder;
        public bool IsBrakingBoulder => _isBrakingBoulder;
        public bool IsInBoulderPushStance => _boulderPushStanceActive || _boulderPushBody != null;
        public BoulderStanceClientState BoulderStanceState => IsInBoulderPushStance
            ? BoulderStanceClientState.Active
            : BoulderStanceClientState.None;
        public float HorizontalSpeed => _body != null ? Vector3.ProjectOnPlane(_body.linearVelocity, Vector3.up).magnitude : 0f;
        public Transform CameraPivot => _cameraPivot;

        private void Awake() => EnsureInitialized();

        public void EnsureInitialized()
        {
            if (_body != null)
                return;
            _body = GetComponent<Rigidbody>();
            _capsule = GetComponent<CapsuleCollider>();
            _standingCapsuleHeight = _capsule.height;
            _standingCapsuleCenter = _capsule.center;
            _input = GetComponent<PlayerInputReader>();
            if (_input == null)
                _input = gameObject.AddComponent<PlayerInputReader>();
            _input.SetLocalControlEnabled(true);
            PlayerPhysics.ConfigureBody(_body, _capsule, _movementMaterial);
            _cameraPivot = new GameObject("Offline Camera Pivot").transform;
            _standingCameraPosition = new Vector3(0f, 1.258f, 0f);
            _cameraOffset = _standingCameraPosition;
            _targetYaw = transform.eulerAngles.y;
            _cameraPresentationRotation = Quaternion.Euler(_pitch, _targetYaw, 0f);
            _cameraPivot.SetPositionAndRotation(transform.TransformPoint(_cameraOffset), _cameraPresentationRotation);
        }

        private void Start()
        {
            Camera camera = Camera.main;
            if (camera == null)
                return;
            camera.transform.SetParent(_cameraPivot, false);
            camera.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        private void Update()
        {
            _actorPhysics ??= GetComponent<PlayerActorPhysics>();
            _actorPhysics?.SetDesiredYaw(_targetYaw);
            Vector2 look = _input.Look;
            _lookActive = look.sqrMagnitude > 0.0001f;
            if (_lookActive)
            {
                Vector2 delta = PlayerPhysics.CalculateLookDelta(look, _input.LookUsesRate,
                    PlayerLookSettings.MouseSensitivity, PlayerLookSettings.ControllerSensitivity,
                    Time.unscaledDeltaTime);
                _targetYaw = Mathf.Repeat(_targetYaw + delta.x, 360f);
                _pitch = Mathf.Clamp(_pitch - delta.y, -75f, 75f);
            }
            Vector3 cameraTarget = _standingCameraPosition + Vector3.down * (_crouched ? PlayerPhysics.CrouchCameraDrop : 0f);
            _cameraOffset = Vector3.Lerp(_cameraOffset, cameraTarget, 1f - Mathf.Exp(-14f * Time.deltaTime));
            if (_input.ConsumeJump())
                _jumpQueued = true;
            if (_input.ConsumeCrouchPress())
                _crouchQueued = true;
        }

        private void LateUpdate()
        {
            if (_cameraPivot == null)
                return;
            Vector3 reactionOffset = _puppet != null ? _puppet.CameraReactionOffset : Vector3.zero;
            Vector3 targetPosition = transform.TransformPoint(_cameraOffset + reactionOffset);
            _cameraPivot.position = PlayerPhysics.CalculateCameraPresentationPosition(
                _cameraPivot.position, targetPosition, Time.deltaTime);
            Quaternion reactionRotation = _puppet != null
                ? _puppet.CameraReactionRotation
                : Quaternion.identity;
            Quaternion targetRotation = Quaternion.Euler(_pitch, _targetYaw, 0f) * reactionRotation;
            _cameraPresentationRotation = PlayerPhysics.CalculateCameraPresentationRotation(
                _cameraPresentationRotation, targetRotation, Time.unscaledDeltaTime);
            _cameraPivot.rotation = _cameraPresentationRotation;
        }

        private void OnDestroy()
        {
            if (_cameraPivot == null)
                return;
            Camera camera = Camera.main;
            if (camera != null && camera.transform.IsChildOf(_cameraPivot))
                camera.transform.SetParent(null, true);
            if (Application.isPlaying)
                Destroy(_cameraPivot.gameObject);
            else
                DestroyImmediate(_cameraPivot.gameObject);
        }

        private void FixedUpdate()
        {
            if (_puppet == null)
                _puppet = GetComponent<ActiveRagdollPuppet>();
            _actorPhysics ??= GetComponent<PlayerActorPhysics>();
            _actorPhysics?.SetDesiredYaw(_targetYaw);
            bool knockedDown = _actorPhysics != null
                ? _actorPhysics.IsMovementLocked
                : _puppet != null && _puppet.IsMovementLocked;
            if (knockedDown && _actorPhysics != null)
            {
                EndBoulderPushStance();
                _moveDirection = Vector3.zero;
                return;
            }
            PlayerPhysics.AdvanceTimedMultiplier(ref _speedMultiplier, ref _speedBoostTicks);
            Vector2 movement = knockedDown ? Vector2.zero : Vector2.ClampMagnitude(_input.Move, 1f);
            PlayerSimulationState state = new()
            {
                CoyoteTicks = _coyoteTicks,
                BufferTicks = _bufferTicks,
                CrouchBoostTicks = _crouchBoostTicks,
                CrouchBoostAvailable = _crouchBoostAvailable,
                Crouched = _crouched,
                Sliding = _sliding,
                Grounded = _grounded,
                GroundedOnBoulder = _groundedOnBoulder,
                BoulderLandingArmed = _boulderLandingArmed,
                GroundNormal = _groundNormal,
                Ground = _currentGround,
                Yaw = _targetYaw
            };
            PlayerSimulationInput input = new(movement, !knockedDown && _jumpQueued,
                !knockedDown && _input.JumpHeld, !knockedDown && _input.SprintHeld,
                !knockedDown && _input.CrouchHeld, !knockedDown && _crouchQueued, _lookActive,
                _targetYaw, _speedMultiplier, !knockedDown ? _boulderPushBody : null);
            PlayerSimulationStep step = PlayerPhysics.SimulatePlayerStep(_capsule, transform, _body, input,
                _standingCapsuleHeight, _standingCapsuleCenter, Time.fixedDeltaTime, ref state);
            if (knockedDown)
            {
                Vector3 ragdollVelocity = PlayerPhysics.CalculateKnockdownVelocity(_body.linearVelocity,
                    state.Grounded, state.GroundNormal,
                    state.Grounded ? state.Ground.PointVelocity : Vector3.zero, Time.fixedDeltaTime);
                step = new PlayerSimulationStep(ragdollVelocity, step.PositionCorrection, step.Rotation,
                    Vector3.zero, step.StanceGeometry, false, false);
            }
            _jumpQueued = false;
            _crouchQueued = false;

            _coyoteTicks = state.CoyoteTicks;
            _bufferTicks = state.BufferTicks;
            _crouchBoostTicks = state.CrouchBoostTicks;
            _crouchBoostAvailable = state.CrouchBoostAvailable;
            _crouched = state.Crouched;
            _sliding = state.Sliding;
            _grounded = state.Grounded;
            _groundedOnBoulder = state.GroundedOnBoulder;
            _boulderLandingArmed = state.BoulderLandingArmed;
            _groundNormal = state.GroundNormal;
            _currentGround = state.Ground;
            _targetYaw = state.Yaw;
            _moveDirection = step.MoveDirection;
            _isPushingBoulder = step.IsPushingBoulder;
            _boulderPushStanceActive = step.HasBoulderStance;

            _body.linearVelocity = step.Velocity;
            if (step.PositionCorrection.sqrMagnitude > 0.000001f)
                _body.MovePosition(_body.position + step.PositionCorrection);
            _body.MoveRotation(step.Rotation);

            _isBrakingBoulder = PlayerPhysics.IsBoulderBrakeActive(step.StanceGeometry, movement);
            if (step.HasBoulderStance && step.StanceGeometry.IsValid && !step.StanceGeometry.Body.isKinematic)
            {
                step.StanceGeometry.Body.AddForceAtPosition(
                    PlayerPhysics.CalculateBoulderHoldForce(step.StanceGeometry, movement,
                        !knockedDown && _input.SprintHeld),
                    step.StanceGeometry.SurfacePoint, ForceMode.Force);
                step.StanceGeometry.Body.AddTorque(
                    PlayerPhysics.CalculateBoulderHoldTorque(step.StanceGeometry, movement), ForceMode.Force);
            }
        }

        public void ApplySpeedBoost(float multiplier)
        {
            _speedMultiplier = Mathf.Max(1f, multiplier);
            if (_speedMultiplier <= 1f)
                _speedBoostTicks = 0;
        }

        public void ApplySpeedBoost(float multiplier, float durationSeconds)
        {
            _speedMultiplier = Mathf.Max(1f, multiplier);
            _speedBoostTicks = _speedMultiplier > 1f
                ? PlayerPhysics.DurationToTicks(durationSeconds, Time.fixedDeltaTime)
                : 0;
        }

        public void ApplyExternalForce(Vector3 force, ForceMode mode) => _body?.AddForce(force, mode);

        public bool TryApplyExternalImpulse(uint simulationTick, int sourceObjectId, Vector3 impulse,
            Vector3 worldPoint)
        {
            if (_body == null)
                return false;
            _actorPhysics ??= GetComponent<PlayerActorPhysics>();
            if (_actorPhysics != null)
                return _actorPhysics.TryApplyImpact(impulse, worldPoint, _targetYaw);
            _body.AddForceAtPosition(impulse, worldPoint, ForceMode.Impulse);
            return true;
        }

        public bool ApplyImpact(PlayerImpactCommand command)
        {
            if (_impactSequences.TryGetValue(command.SourceObjectId, out uint previous) &&
                unchecked((int)(command.Sequence - previous)) <= 0)
                return false;
            _impactSequences[command.SourceObjectId] = command.Sequence;
            _actorPhysics ??= GetComponent<PlayerActorPhysics>();
            Vector3 worldPoint = transform.TransformPoint(command.LocalHitPoint);
            if (_actorPhysics != null)
                return _actorPhysics.TryApplyImpact(command.Impulse, worldPoint, _targetYaw);
            _body.AddForceAtPosition(command.Impulse, worldPoint, ForceMode.Impulse);
            return true;
        }

        public bool TryBeginBoulderPushStance(Rigidbody boulderBody)
        {
            if (boulderBody == null || !_grounded || _groundedOnBoulder ||
                boulderBody.GetComponentInParent<BoulderController>() == null)
                return false;
            if (!PlayerPhysics.TryGetBoulderStanceGeometry(_capsule, transform, boulderBody, _groundNormal,
                    out BoulderPushStanceGeometry geometry) ||
                geometry.SurfaceGap > PlayerPhysics.BoulderStanceMaximumGap)
                return false;
            _boulderPushBody = boulderBody;
            _boulderPushStanceActive = true;
            return true;
        }

        public void EndBoulderPushStance()
        {
            _boulderPushBody = null;
            _boulderPushStanceActive = false;
            _isPushingBoulder = false;
            _isBrakingBoulder = false;
        }

        public void CopyConfigurationFrom(PlayerMotor source)
        {
            if (source == null)
                return;
            _movementMaterial = source.MovementMaterial;
            _lookSensitivity = source.LookSensitivity;
            _controllerLookSpeed = source.ControllerLookSpeed;
            if (_capsule != null && _movementMaterial != null)
                _capsule.material = _movementMaterial;
        }
    }
}
