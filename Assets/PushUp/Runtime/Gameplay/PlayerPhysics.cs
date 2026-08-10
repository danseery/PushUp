using Unity.Profiling;
using UnityEngine;

namespace PushUp.Gameplay
{
    public enum MovementSurfaceKind : byte
    {
        None,
        StaticTerrain,
        Boulder,
        DynamicProp,
        Player,
        NonWalkable
    }

    public readonly struct GroundContact
    {
        public readonly Collider Collider;
        public readonly Vector3 Point;
        public readonly Vector3 Normal;
        public readonly Vector3 PointVelocity;
        public readonly MovementSurfaceKind SurfaceKind;
        public readonly float SnapDistance;
        public readonly RaycastHit Hit;

        public GroundContact(Collider collider, Vector3 point, Vector3 normal, Vector3 pointVelocity,
            MovementSurfaceKind surfaceKind, float snapDistance, RaycastHit hit)
        {
            Collider = collider;
            Point = point;
            Normal = normal;
            PointVelocity = pointVelocity;
            SurfaceKind = surfaceKind;
            SnapDistance = snapDistance;
            Hit = hit;
        }

        public bool IsValid => Collider != null;
    }

    public readonly struct BoulderPushStanceGeometry
    {
        public readonly BoulderController Boulder;
        public readonly Rigidbody Body;
        public readonly Vector3 SurfacePoint;
        public readonly Vector3 GroundNormal;
        public readonly Vector3 Outward;
        public readonly Vector3 Inward;
        public readonly Vector3 Tangent;
        public readonly float SurfaceGap;

        public BoulderPushStanceGeometry(BoulderController boulder, Rigidbody body, Vector3 surfacePoint,
            Vector3 groundNormal, Vector3 outward, Vector3 tangent, float surfaceGap)
        {
            Boulder = boulder;
            Body = body;
            SurfacePoint = surfacePoint;
            GroundNormal = groundNormal;
            Outward = outward;
            Inward = -outward;
            Tangent = tangent;
            SurfaceGap = surfaceGap;
        }

        public bool IsValid => Boulder != null && Body != null;
    }

    /// <summary>
    /// Mutable gameplay state shared by the predicted and standalone motors. Keeping the
    /// transition data in one structure prevents the offline controller from quietly
    /// developing different coyote, crouch, ground, or boulder-landing rules.
    /// </summary>
    public struct PlayerSimulationState
    {
        public int CoyoteTicks;
        public int BufferTicks;
        public int CrouchBoostTicks;
        public bool CrouchBoostAvailable;
        public bool Crouched;
        public bool Sliding;
        public bool Grounded;
        public bool GroundedOnBoulder;
        public bool BoulderLandingArmed;
        public Vector3 GroundNormal;
        public GroundContact Ground;
        public float Yaw;
    }

    public readonly struct PlayerSimulationInput
    {
        public readonly Vector2 Move;
        public readonly bool JumpPressed;
        public readonly bool JumpHeld;
        public readonly bool Sprint;
        public readonly bool CrouchHeld;
        public readonly bool CrouchPressed;
        public readonly bool LookActive;
        public readonly float Yaw;
        public readonly float SpeedMultiplier;
        public readonly Rigidbody BoulderStanceBody;

        public PlayerSimulationInput(Vector2 move, bool jumpPressed, bool jumpHeld, bool sprint,
            bool crouchHeld, bool crouchPressed, bool lookActive, float yaw, float speedMultiplier,
            Rigidbody boulderStanceBody)
        {
            Move = Vector2.ClampMagnitude(move, 1f);
            JumpPressed = jumpPressed;
            JumpHeld = jumpHeld;
            Sprint = sprint;
            CrouchHeld = crouchHeld;
            CrouchPressed = crouchPressed;
            LookActive = lookActive;
            Yaw = yaw;
            SpeedMultiplier = speedMultiplier;
            BoulderStanceBody = boulderStanceBody;
        }
    }

    public readonly struct PlayerSimulationStep
    {
        public readonly Vector3 Velocity;
        public readonly Vector3 PositionCorrection;
        public readonly Quaternion Rotation;
        public readonly Vector3 MoveDirection;
        public readonly BoulderPushStanceGeometry StanceGeometry;
        public readonly bool TookOff;
        public readonly bool IsPushingBoulder;

        public PlayerSimulationStep(Vector3 velocity, Vector3 positionCorrection, Quaternion rotation,
            Vector3 moveDirection, BoulderPushStanceGeometry stanceGeometry, bool tookOff,
            bool isPushingBoulder)
        {
            Velocity = velocity;
            PositionCorrection = positionCorrection;
            Rotation = rotation;
            MoveDirection = moveDirection;
            StanceGeometry = stanceGeometry;
            TookOff = tookOff;
            IsPushingBoulder = isPushingBoulder;
        }

        public bool HasBoulderStance => StanceGeometry.IsValid;
    }

    public static class PlayerPhysics
    {
        public const float Mass = 78f;
        public const float CapsuleHeight = 3.7f;
        public const float CapsuleRadius = 0.5f;
        public const float WalkSpeed = 10f;
        public const float SprintSpeed = 15f;
        public const float CrouchSpeed = 3.3f;
        public const float MoveAcceleration = 75f;
        public const float BrakeAcceleration = 90f;
        public const float ReverseAcceleration = 96f;
        public const float AirAcceleration = 20.0f;
        // A knocked-down player remains a deterministic capsule, but must keep enough
        // horizontal momentum for hits and grabs to feel like a physical ragdoll rather
        // than a motor immediately pinning them in place.
        public const float KnockdownBrakeAcceleration = 12f;
        public const float KnockdownMaximumSpeed = 16f;
        public const float MaxSpeed = WalkSpeed;
        public const float SprintMultiplier = SprintSpeed / WalkSpeed;
        public const float CrouchSpeedMultiplier = CrouchSpeed / WalkSpeed;
        public const float JumpHeight = 3.0f;
        public const float JumpRiseSeconds = 0.5f;
        public const float RisingGravity = 2f * JumpHeight / (JumpRiseSeconds * JumpRiseSeconds);
        public const float JumpVelocity = RisingGravity * JumpRiseSeconds;
        public const float FallingGravity = 38f;
        public const float ReleasedJumpGravity = 69.6f;
        public const float GroundStickSpeed = 1.8f;
        public const float GroundSnapDistance = 0.20f;
        public const float StepHeight = 0.28f;
        public const float PushWalkSpeed = 3.6f;
        public const float PushSprintSpeed = 5f;
        public const float PushWalkForce = 650f;
        public const float PushSprintForce = 1050f;
        public const float BoulderPushForceTaperSpeed = 0.75f;
        public const float BoulderBrakeForce = 900f;
        public const float BoulderBrakeTorque = 650f;
        public const float BoulderBackwardBrakeMultiplier = 2f;
        public const float BoulderBrakeFullForceSpeed = 1.25f;
        public const float BoulderBrakeFullTorqueSpeed = 1.5f;
        public const float BoulderBrakeMinimumSpeed = 0.08f;
        public const float BoulderBrakeInputDeadzone = 0.08f;
        public const float BoulderStanceGap = 0.10f;
        public const float BoulderStanceMaximumGap = 0.90f;
        public const float BoulderStanceAlignmentSpeed = 1.5f;
        public const float BoulderStanceAlignmentAcceleration = 18f;
        public const float BoulderStanceAlignmentGain = 8f;
        public const float BoulderStanceOrbitSpeed = 3f;
        public const float BoulderStanceYawSpeed = 90f;
        public const float CameraPositionSharpness = 60f;
        public const float CameraMaximumLag = 0.12f;
        public const float CameraTeleportDistance = 1.5f;
        public const float CameraRotationSharpness = 36f;
        public const float CameraMaximumAngularLag = 2.5f;
        public const float ControllerLookSpeed = 180f;
        public const float CrouchHeightMultiplier = 0.62f;
        public const float CrouchCameraDrop = 0.48f;
        public const float CrouchBoostVerticalVelocity = 1.2f;
        public const float CrouchBoostForwardVelocity = 0.9f;
        public const float CrouchBoostSeconds = 0.22f;
        public const float SlideEntryMinimumSpeed = SprintSpeed * 0.9f;
        public const float SlideExitSpeed = 6f;
        public const float SlideEntryBoost = 2.5f;
        public const float SlideDrag = 0.6f;
        public const float SlideSteerAcceleration = 5f;
        public const float SlideSlopeAcceleration = 32f;
        public const float SlideMaximumSpeed = 36f;
        public const float CoyoteSeconds = 0.12f;
        public const float JumpBufferSeconds = 0.12f;
        public const float MaxGroundAngle = 50f;
        public const float BoulderTopNormal = 0.72f;

        private const float ProbeSkin = 0.045f;
        private const float StepProbeDistance = 0.22f;
        private static readonly RaycastHit[] GroundHits = new RaycastHit[16];
        private static readonly RaycastHit[] StepHits = new RaycastHit[12];
        private static readonly RaycastHit[] BoulderContactHits = new RaycastHit[8];
        private static readonly Collider[] BoulderContactOverlaps = new Collider[8];
        private static readonly Collider[] StandHits = new Collider[12];
        private static readonly ProfilerMarker SimulationMarker = new("PushUp.Player.SimulationStep");
        private static readonly ProfilerMarker GroundQueryMarker = new("PushUp.Player.GroundQuery");
        private static readonly ProfilerMarker StepQueryMarker = new("PushUp.Player.StepQuery");

        public static void ConfigureBody(Rigidbody body, CapsuleCollider capsule = null, PhysicsMaterial movementMaterial = null)
        {
            body.mass = Mass;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.Continuous;
            body.constraints = RigidbodyConstraints.FreezeRotation;
            body.solverIterations = 12;
            body.solverVelocityIterations = 4;
            if (capsule != null && movementMaterial != null)
                capsule.material = movementMaterial;
        }

        public static bool IsGrounded(CapsuleCollider capsule, Transform playerRoot, out RaycastHit groundHit)
        {
            bool grounded = TryGetGround(capsule, playerRoot, false, false, 0f, out GroundContact contact);
            groundHit = grounded ? contact.Hit : default;
            return grounded;
        }

        public static bool TryGetGround(CapsuleCollider capsule, Transform playerRoot, bool allowBoulderTop,
            bool wasGroundedOnBoulder, float verticalVelocity, out GroundContact ground)
        {
            using ProfilerMarker.AutoScope profilerScope = GroundQueryMarker.Auto();
            // A freshly launched player must clear the support before ground snapping can
            // become eligible again. This also keeps coyote state from being refreshed
            // while the capsule is still within the probe distance on its way upward.
            if (allowBoulderTop && verticalVelocity > 0.05f)
            {
                ground = default;
                return false;
            }

            GetWorldCapsule(capsule, out Vector3 bottom, out _, out float radius);
            float castRadius = radius * 0.92f;
            Vector3 origin = bottom + Vector3.up * ProbeSkin;
            int count = Physics.SphereCastNonAlloc(origin, castRadius, Vector3.down, GroundHits,
                GroundSnapDistance + ProbeSkin, GameplayLayers.GroundQueryMask, QueryTriggerInteraction.Ignore);

            float closest = float.MaxValue;
            ground = default;
            for (int index = 0; index < count; index++)
            {
                RaycastHit hit = GroundHits[index];
                if (hit.collider == null || hit.collider.transform.IsChildOf(playerRoot))
                    continue;

                MovementSurfaceKind kind = ClassifySurface(hit.collider);
                bool walkable = IsWalkableNormal(hit.normal);
                if (kind == MovementSurfaceKind.Boulder)
                {
                    bool validTop = IsValidBoulderLanding(hit.normal, verticalVelocity,
                        allowBoulderTop || wasGroundedOnBoulder);
                    if (!validTop)
                        continue;
                }
                else if (kind != MovementSurfaceKind.StaticTerrain || !walkable)
                {
                    continue;
                }

                if (hit.distance >= closest)
                    continue;

                Rigidbody support = hit.rigidbody;
                Vector3 pointVelocity = support != null ? support.GetPointVelocity(hit.point) : Vector3.zero;
                closest = hit.distance;
                float actualGap = hit.distance - ProbeSkin - (radius - castRadius);
                ground = new GroundContact(hit.collider, hit.point, hit.normal.normalized, pointVelocity, kind,
                    Mathf.Clamp(actualGap, 0f, GroundSnapDistance), hit);
            }
            return ground.IsValid;
        }

        public static MovementSurfaceKind ClassifySurface(Collider collider)
        {
            if (collider == null)
                return MovementSurfaceKind.None;
            switch (collider.gameObject.layer)
            {
                case GameplayLayers.Terrain:
                    return MovementSurfaceKind.StaticTerrain;
                case GameplayLayers.Boulder:
                    return MovementSurfaceKind.Boulder;
                case GameplayLayers.Player:
                case GameplayLayers.Actor:
                    return MovementSurfaceKind.Player;
                case GameplayLayers.Interactable:
                    return collider.attachedRigidbody != null
                        ? MovementSurfaceKind.DynamicProp
                        : MovementSurfaceKind.NonWalkable;
                case GameplayLayers.Pickup:
                case GameplayLayers.GameplayTrigger:
                case GameplayLayers.Presentation:
                    return MovementSurfaceKind.NonWalkable;
            }

            // Legacy/untagged scene objects retain component-based classification until
            // the authored layer migration has touched them once.
            if (collider.GetComponentInParent<BoulderController>() != null)
                return MovementSurfaceKind.Boulder;
            if (collider.GetComponentInParent<PlayerInteraction>() != null ||
                collider.GetComponentInParent<TrainingDummy>() != null)
                return MovementSurfaceKind.Player;
            if (collider.attachedRigidbody != null)
                return MovementSurfaceKind.DynamicProp;
            return MovementSurfaceKind.StaticTerrain;
        }

        public static bool IsWalkableNormal(Vector3 surfaceNormal)
        {
            return Vector3.Dot(surfaceNormal.normalized, Vector3.up) >= Mathf.Cos(MaxGroundAngle * Mathf.Deg2Rad);
        }

        public static bool IsValidBoulderLanding(Vector3 normal, float verticalVelocity, bool landingArmed)
        {
            return landingArmed && verticalVelocity <= 0.05f && Vector3.Dot(normal.normalized, Vector3.up) >= BoulderTopNormal;
        }

        public static Vector3 DesiredDirection(Transform player, Vector2 input)
        {
            Vector3 direction = player.TransformDirection(new Vector3(input.x, 0f, input.y));
            return Vector3.ClampMagnitude(direction, 1f);
        }

        public static Vector3 DesiredDirection(float yawDegrees, Vector2 input)
        {
            Vector3 direction = Quaternion.Euler(0f, yawDegrees, 0f) * new Vector3(input.x, 0f, input.y);
            return Vector3.ClampMagnitude(direction, 1f);
        }

        public static ushort EncodeYaw(float yawDegrees) =>
            (ushort)Mathf.RoundToInt(Mathf.Repeat(yawDegrees, 360f) * (ushort.MaxValue / 360f));

        public static float DecodeYaw(ushort encodedYaw) => encodedYaw * (360f / ushort.MaxValue);

        /// <summary>
        /// Selects the owner-simulated body heading without allowing boulder auto-facing to write into
        /// presentation yaw. While a player is hands-on and not looking, the body may continue converging
        /// toward the boulder; every other case samples the owner's current camera heading.
        /// </summary>
        public static float SelectMotorYaw(float presentationYaw, float currentMotorYaw,
            bool inBoulderStance, bool lookActive) => Mathf.Repeat(
            inBoulderStance && !lookActive ? currentMotorYaw : presentationYaw, 360f);

        public static bool IsLookActiveForSimulation(bool latchedLook, Vector2 currentLook) =>
            latchedLook || currentLook.sqrMagnitude > 0.0001f;

        public static Vector3 DesiredVelocity(Transform player, Vector2 input, float maxSpeed)
        {
            return DesiredDirection(player, input) * maxSpeed;
        }

        public static float CurrentMovementSpeed(float speedMultiplier, bool sprinting, bool crouched,
            bool grounded)
        {
            // Crouching in the air is an action/presentation state, not an air brake. It may
            // prepare the crouch-jump boost and resize the capsule, but the reduced crouch
            // locomotion speed only applies once the player is supported by the ground.
            float speed = crouched && grounded ? CrouchSpeed : sprinting ? SprintSpeed : WalkSpeed;
            return speed * Mathf.Max(1f, speedMultiplier);
        }

        public static float MovementSpeed(float legacyBaseSpeed, bool sprinting, bool crouched)
        {
            if (crouched)
                return legacyBaseSpeed * CrouchSpeedMultiplier;
            return sprinting ? legacyBaseSpeed * SprintMultiplier : legacyBaseSpeed;
        }

        /// <summary>
        /// Advances one complete player simulation step without mutating the Rigidbody.
        /// The caller applies the returned velocity, correction, and rotation through its
        /// authoritative Rigidbody (the owning peer online, or the standalone body offline).
        /// </summary>
        public static PlayerSimulationStep SimulatePlayerStep(CapsuleCollider capsule, Transform playerRoot,
            Rigidbody body, PlayerSimulationInput input, float standingHeight, Vector3 standingCenter,
            float deltaTime, ref PlayerSimulationState state)
        {
            using ProfilerMarker.AutoScope profilerScope = SimulationMarker.Auto();
            float safeDelta = Mathf.Max(0.0001f, deltaTime);
            state.Yaw = Mathf.Repeat(input.Yaw, 360f);
            state.Grounded = TryGetGround(capsule, playerRoot, state.BoulderLandingArmed,
                state.GroundedOnBoulder, body.linearVelocity.y, out state.Ground);
            state.GroundNormal = state.Grounded ? state.Ground.Normal : Vector3.up;
            state.GroundedOnBoulder = state.Grounded && state.Ground.SurfaceKind == MovementSurfaceKind.Boulder;
            if (state.Grounded && !state.GroundedOnBoulder)
                state.BoulderLandingArmed = false;

            bool tookOff = AdvanceJumpWindows(state.Grounded, input.JumpPressed, safeDelta,
                ref state.CoyoteTicks, ref state.BufferTicks);
            if (tookOff)
                state.Grounded = false;

            if (input.CrouchHeld)
                state.Crouched = true;
            else if (state.Crouched && CanStand(capsule, playerRoot, standingHeight, standingCenter))
                state.Crouched = false;
            SetCrouched(capsule, state.Crouched, standingHeight, standingCenter);

            BoulderPushStanceGeometry stance = default;
            bool hasStance = input.BoulderStanceBody != null && !tookOff && state.Grounded &&
                             !state.GroundedOnBoulder &&
                             TryGetBoulderStanceGeometry(capsule, playerRoot, input.BoulderStanceBody,
                                 state.GroundNormal, out stance) &&
                             stance.SurfaceGap <= BoulderStanceMaximumGap;

            Vector3 desiredDirection = hasStance
                ? Vector3.ClampMagnitude(stance.Inward * Mathf.Clamp01(input.Move.y) +
                                         stance.Tangent * input.Move.x, 1f)
                : DesiredDirection(state.Yaw, input.Move);
            Vector3 moveDirection = desiredDirection.sqrMagnitude > 0.01f
                ? desiredDirection.normalized
                : Vector3.zero;
            bool pushing = hasStance && input.Move.y > 0.01f;
            float movementSpeed = CurrentMovementSpeed(input.SpeedMultiplier, input.Sprint, state.Crouched,
                state.Grounded);
            Vector3 supportVelocity = state.Grounded ? state.Ground.PointVelocity : Vector3.zero;
            float groundedPlanarSpeed = Vector3.ProjectOnPlane(body.linearVelocity - supportVelocity,
                state.GroundNormal).magnitude;
            state.Sliding = UpdateSlideState(state.Sliding, state.Grounded, state.GroundedOnBoulder,
                hasStance, input.CrouchHeld, input.CrouchPressed, input.Sprint, groundedPlanarSpeed);
            bool slideActive = state.Sliding && state.Grounded;
            Vector3 velocity = hasStance
                ? CalculateBoulderStanceVelocity(body.linearVelocity, stance, input.Move, input.Sprint,
                    supportVelocity, safeDelta)
                : slideActive
                    ? CalculateSlideVelocity(body.linearVelocity, desiredDirection, state.GroundNormal,
                        supportVelocity, input.CrouchPressed, input.SpeedMultiplier, safeDelta)
                    : CalculateLocomotionVelocity(body.linearVelocity, desiredDirection, movementSpeed,
                        state.Grounded, state.GroundNormal, supportVelocity, safeDelta,
                        state.Crouched && !state.Grounded);

            Vector3 positionCorrection = Vector3.zero;
            if (state.Grounded && !state.GroundedOnBoulder && !hasStance &&
                TryFindStep(capsule, playerRoot, desiredDirection, out Vector3 stepCorrection))
                positionCorrection += stepCorrection;

            if (tookOff)
            {
                velocity.y = JumpVelocity;
                state.BoulderLandingArmed = true;
                state.GroundedOnBoulder = false;
            }
            if (AdvanceCrouchBoost(tookOff, state.Grounded, input.CrouchPressed, safeDelta,
                    ref state.CrouchBoostTicks, ref state.CrouchBoostAvailable))
                velocity += CrouchBoost(moveDirection);
            if (!tookOff)
                velocity = ApplyJumpGravity(velocity, state.Grounded, input.JumpHeld, safeDelta);

            Vector3 expectedGroundVelocity = Vector3.ProjectOnPlane(desiredDirection, state.GroundNormal) *
                                             movementSpeed + supportVelocity;
            velocity = SuppressBoulderClimbVelocity(velocity, expectedGroundVelocity, hasStance, tookOff);
            if (state.Grounded && !tookOff && state.Ground.SnapDistance > 0.01f &&
                body.linearVelocity.y <= supportVelocity.y + 0.2f)
                positionCorrection -= Vector3.up * state.Ground.SnapDistance;

            if (hasStance && !input.LookActive)
            {
                Quaternion facing = CalculateBoulderFacingRotation(body.rotation, stance.Inward, safeDelta);
                state.Yaw = facing.eulerAngles.y;
            }
            Quaternion rotation = Quaternion.Euler(0f, state.Yaw, 0f);
            return new PlayerSimulationStep(velocity, positionCorrection, rotation, moveDirection, stance,
                tookOff, pushing);
        }

        public static void AdvanceTimedMultiplier(ref float multiplier, ref int remainingTicks)
        {
            if (remainingTicks <= 0)
            {
                remainingTicks = 0;
                multiplier = 1f;
                return;
            }

            remainingTicks--;
            if (remainingTicks == 0)
                multiplier = 1f;
        }

        public static int DurationToTicks(float seconds, float simulationDelta) =>
            Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(0f, seconds) / Mathf.Max(0.0001f, simulationDelta)));

        public static Vector3 CalculateLocomotionVelocity(Vector3 currentVelocity, Vector3 desiredDirection,
            float desiredSpeed, bool grounded, Vector3 groundNormal, Vector3 supportVelocity, float deltaTime,
            bool preserveAirMomentum = false)
        {
            Vector3 normal = grounded ? groundNormal.normalized : Vector3.up;
            Vector3 relative = currentVelocity - supportVelocity;
            Vector3 planar = Vector3.ProjectOnPlane(relative, normal);
            Vector3 tangentDirection = desiredDirection.sqrMagnitude > 0.0001f
                ? Vector3.ProjectOnPlane(desiredDirection, normal).normalized
                : Vector3.zero;
            Vector3 target = tangentDirection * desiredSpeed;

            float acceleration;
            if (!grounded)
                acceleration = AirAcceleration;
            else if (target.sqrMagnitude < 0.0001f)
                acceleration = BrakeAcceleration;
            else if (planar.sqrMagnitude > 0.01f && Vector3.Dot(planar.normalized, target.normalized) < 0f)
                acceleration = ReverseAcceleration;
            else
                acceleration = MoveAcceleration;

            Vector3 nextPlanar;
            if (!grounded && preserveAirMomentum)
            {
                float preservedSpeed = planar.magnitude;
                if (tangentDirection.sqrMagnitude < 0.0001f)
                {
                    nextPlanar = planar;
                }
                else
                {
                    float targetSpeed = Mathf.Max(desiredSpeed, preservedSpeed);
                    nextPlanar = Vector3.MoveTowards(planar, tangentDirection * targetSpeed,
                        acceleration * deltaTime);
                    if (preservedSpeed > desiredSpeed && nextPlanar.sqrMagnitude > 0.0001f &&
                        nextPlanar.magnitude < preservedSpeed)
                        nextPlanar = nextPlanar.normalized * preservedSpeed;
                }
            }
            else
            {
                nextPlanar = Vector3.MoveTowards(planar, target, acceleration * deltaTime);
            }
            if (!grounded)
                return new Vector3(nextPlanar.x + supportVelocity.x, currentVelocity.y, nextPlanar.z + supportVelocity.z);
            return nextPlanar + supportVelocity - normal * GroundStickSpeed;
        }

        public static bool UpdateSlideState(bool sliding, bool grounded, bool groundedOnBoulder,
            bool inBoulderStance, bool crouchHeld, bool crouchPressed, bool sprinting, float planarSpeed)
        {
            if (!crouchHeld || groundedOnBoulder || inBoulderStance)
                return false;
            if (sliding)
                return !grounded || planarSpeed > SlideExitSpeed;
            return grounded && crouchPressed && sprinting && planarSpeed >= SlideEntryMinimumSpeed;
        }

        public static Vector3 CalculateSlideVelocity(Vector3 currentVelocity, Vector3 desiredDirection,
            Vector3 groundNormal, Vector3 supportVelocity, bool enteringSlide, float speedMultiplier,
            float deltaTime)
        {
            float safeDelta = Mathf.Max(0.0001f, deltaTime);
            Vector3 normal = groundNormal.sqrMagnitude > 0.0001f ? groundNormal.normalized : Vector3.up;
            Vector3 planar = Vector3.ProjectOnPlane(currentVelocity - supportVelocity, normal);
            float initialSpeed = planar.magnitude;

            Vector3 inputDirection = desiredDirection.sqrMagnitude > 0.0001f
                ? Vector3.ProjectOnPlane(desiredDirection, normal).normalized
                : Vector3.zero;
            if (inputDirection.sqrMagnitude > 0.0001f && planar.sqrMagnitude > 0.0001f)
            {
                Vector3 steered = Vector3.MoveTowards(planar, inputDirection * initialSpeed,
                    SlideSteerAcceleration * safeDelta);
                planar = steered.sqrMagnitude > 0.0001f ? steered.normalized * initialSpeed : planar;
            }

            if (enteringSlide && planar.sqrMagnitude > 0.0001f)
                planar += planar.normalized * (SlideEntryBoost * Mathf.Max(1f, speedMultiplier));

            // Projected downhill acceleration is zero on level ground and grows naturally with slope.
            // It replaces ordinary motor braking while skiing, giving steep descents the Tribes-like payoff.
            planar += Vector3.ProjectOnPlane(Vector3.down * SlideSlopeAcceleration, normal) * safeDelta;
            planar = Vector3.MoveTowards(planar, Vector3.zero, SlideDrag * safeDelta);
            planar = Vector3.ClampMagnitude(planar, SlideMaximumSpeed * Mathf.Max(1f, speedMultiplier));
            return planar + supportVelocity - normal * GroundStickSpeed;
        }

        /// <summary>
        /// Legacy locomotion-disabled velocity damping used only when a player has no
        /// PlayerActorPhysics component. Migrated players unlock and simulate their real
        /// Rigidbody root instead of forcing an upright capsule through this fallback.
        /// </summary>
        public static Vector3 CalculateKnockdownVelocity(Vector3 currentVelocity, bool grounded,
            Vector3 groundNormal, Vector3 supportVelocity, float deltaTime)
        {
            Vector3 normal = grounded && groundNormal.sqrMagnitude > 0.0001f
                ? groundNormal.normalized
                : Vector3.up;
            Vector3 relative = currentVelocity - supportVelocity;
            Vector3 planar = Vector3.ProjectOnPlane(relative, normal);
            planar = Vector3.MoveTowards(planar, Vector3.zero,
                KnockdownBrakeAcceleration * Mathf.Max(0.0001f, deltaTime));
            planar = Vector3.ClampMagnitude(planar, KnockdownMaximumSpeed);

            if (grounded)
                return planar + supportVelocity - normal * GroundStickSpeed;

            Vector3 velocity = planar + supportVelocity;
            velocity.y = currentVelocity.y;
            return ApplyJumpGravity(velocity, false, false, deltaTime);
        }

        public static Vector3 ApplyJumpGravity(Vector3 velocity, bool grounded, bool jumpHeld, float deltaTime)
        {
            if (grounded)
                return velocity;
            float gravity = velocity.y > 0f ? (jumpHeld ? RisingGravity : ReleasedJumpGravity) : FallingGravity;
            velocity.y -= gravity * deltaTime;
            return velocity;
        }

        public static Vector3 CrouchBoost(Vector3 movementDirection)
        {
            Vector3 horizontal = Vector3.ProjectOnPlane(movementDirection, Vector3.up).normalized;
            return Vector3.up * CrouchBoostVerticalVelocity + horizontal * CrouchBoostForwardVelocity;
        }

        public static bool AdvanceCrouchBoost(bool tookOff, bool grounded, bool crouchPressed, float deltaTime,
            ref int boostTicks, ref bool boostAvailable)
        {
            if (tookOff)
            {
                boostTicks = Mathf.Max(1, Mathf.CeilToInt(CrouchBoostSeconds / deltaTime));
                boostAvailable = true;
                return false;
            }
            if (grounded)
            {
                boostTicks = 0;
                boostAvailable = false;
                return false;
            }
            boostTicks = Mathf.Max(0, boostTicks - 1);
            if (!boostAvailable || boostTicks <= 0)
            {
                boostAvailable = false;
                return false;
            }
            if (!crouchPressed)
                return false;
            boostAvailable = false;
            boostTicks = 0;
            return true;
        }

        public static void SetCrouched(CapsuleCollider capsule, bool crouched, float standingHeight, Vector3 standingCenter)
        {
            float height = crouched ? standingHeight * CrouchHeightMultiplier : standingHeight;
            capsule.height = height;
            capsule.center = standingCenter - Vector3.up * ((standingHeight - height) * 0.5f);
        }

        public static bool CanStand(CapsuleCollider capsule, Transform playerRoot, float standingHeight, Vector3 standingCenter)
        {
            Vector3 scale = capsule.transform.lossyScale;
            float radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z)) * 0.94f;
            float halfHeight = Mathf.Max(radius, standingHeight * Mathf.Abs(scale.y) * 0.5f);
            Vector3 center = capsule.transform.TransformPoint(standingCenter);
            Vector3 offset = capsule.transform.up * (halfHeight - radius);
            int count = Physics.OverlapCapsuleNonAlloc(center - offset, center + offset, radius, StandHits,
                GameplayLayers.BlockingQueryMask, QueryTriggerInteraction.Ignore);
            for (int index = 0; index < count; index++)
            {
                Collider hit = StandHits[index];
                if (hit != null && !hit.transform.IsChildOf(playerRoot))
                    return false;
            }
            return true;
        }

        public static float TakeoffVelocityChange(float currentVerticalVelocity)
        {
            return Mathf.Max(0f, JumpVelocity - currentVerticalVelocity);
        }

        public static bool AdvanceJumpWindows(bool grounded, bool jumpPressed, float deltaTime,
            ref int coyoteTicks, ref int bufferTicks)
        {
            int coyoteWindow = Mathf.Max(1, Mathf.CeilToInt(CoyoteSeconds / deltaTime));
            int bufferWindow = Mathf.Max(1, Mathf.CeilToInt(JumpBufferSeconds / deltaTime));
            coyoteTicks = grounded ? coyoteWindow : Mathf.Max(0, coyoteTicks - 1);
            bufferTicks = jumpPressed ? bufferWindow : Mathf.Max(0, bufferTicks - 1);
            if (coyoteTicks <= 0 || bufferTicks <= 0)
                return false;
            coyoteTicks = 0;
            bufferTicks = 0;
            return true;
        }

        public static bool TryFindStep(CapsuleCollider capsule, Transform playerRoot, Vector3 desiredDirection,
            out Vector3 correction)
        {
            using ProfilerMarker.AutoScope profilerScope = StepQueryMarker.Auto();
            correction = Vector3.zero;
            Vector3 direction = Vector3.ProjectOnPlane(desiredDirection, Vector3.up).normalized;
            if (direction.sqrMagnitude < 0.01f)
                return false;

            GetWorldCapsule(capsule, out Vector3 bottom, out Vector3 top, out float radius);
            float castRadius = radius * 0.94f;
            if (!TryGetNearestStaticHit(bottom, top, castRadius, direction, StepProbeDistance, out _))
                return false;

            Vector3 raised = Vector3.up * (StepHeight + ProbeSkin);
            if (TryGetNearestBlockingHit(bottom + raised, top + raised, castRadius, direction, StepProbeDistance, playerRoot))
                return false;

            Vector3 forward = direction * StepProbeDistance;
            Vector3 downStartBottom = bottom + raised + forward;
            Vector3 downStartTop = top + raised + forward;
            int count = Physics.CapsuleCastNonAlloc(downStartBottom, downStartTop, castRadius, Vector3.down,
                StepHits, StepHeight + GroundSnapDistance, GameplayLayers.GroundQueryMask,
                QueryTriggerInteraction.Ignore);
            float currentBottom = Mathf.Min(bottom.y, top.y) - radius;
            float bestRise = float.MaxValue;
            for (int index = 0; index < count; index++)
            {
                RaycastHit hit = StepHits[index];
                if (hit.collider == null || hit.collider.transform.IsChildOf(playerRoot) || hit.rigidbody != null ||
                    !IsWalkableNormal(hit.normal))
                    continue;
                float rise = hit.point.y - currentBottom;
                if (rise > 0.01f && rise <= StepHeight + 0.01f && rise < bestRise)
                    bestRise = rise;
            }
            if (bestRise == float.MaxValue)
                return false;
            correction = Vector3.up * bestRise + direction * 0.04f;
            return true;
        }

        public static float PushSpeed(bool sprinting) => sprinting ? PushSprintSpeed : PushWalkSpeed;
        public static float PushForce(bool sprinting) => sprinting ? PushSprintForce : PushWalkForce;

        public static bool TryFindBoulderContact(CapsuleCollider capsule, Transform playerRoot, Vector2 input,
            float yaw, out Rigidbody boulderBody)
        {
            boulderBody = null;
            Vector3 direction = DesiredDirection(yaw, input);
            if (direction.sqrMagnitude < 0.01f)
                return false;
            GetWorldCapsule(capsule, out Vector3 bottom, out Vector3 top, out float radius);

            // CapsuleCast does not reliably return a collider which overlaps the cast at its start.
            // That is precisely the common client case: the kinematic boulder proxy has already
            // blocked the owner capsule. Detect the touching boulder before looking ahead.
            int overlapCount = Physics.OverlapCapsuleNonAlloc(bottom, top, radius + 0.12f,
                BoulderContactOverlaps, 1 << GameplayLayers.Boulder, QueryTriggerInteraction.Ignore);
            float nearestOverlap = float.PositiveInfinity;
            Vector3 playerCenter = capsule.bounds.center;
            for (int index = 0; index < overlapCount; index++)
            {
                Collider candidate = BoulderContactOverlaps[index];
                Rigidbody body = candidate != null ? candidate.attachedRigidbody : null;
                if (body == null)
                    body = candidate != null ? candidate.GetComponentInParent<Rigidbody>() : null;
                if (body == null || body == capsule.attachedRigidbody ||
                    body.GetComponentInParent<BoulderController>() == null)
                    continue;
                Vector3 radial = playerCenter - body.worldCenterOfMass;
                if (radial.sqrMagnitude > 0.001f &&
                    Vector3.Dot(radial.normalized, Vector3.up) >= BoulderTopNormal)
                    continue;
                float distance = (candidate.ClosestPoint(playerCenter) - playerCenter).sqrMagnitude;
                if (distance >= nearestOverlap)
                    continue;
                nearestOverlap = distance;
                boulderBody = body;
            }
            if (boulderBody != null)
                return true;

            int count = Physics.CapsuleCastNonAlloc(bottom, top, radius * 0.94f, direction.normalized,
                BoulderContactHits, 0.32f, 1 << GameplayLayers.Boulder, QueryTriggerInteraction.Ignore);
            float nearest = float.PositiveInfinity;
            for (int index = 0; index < count; index++)
            {
                RaycastHit hit = BoulderContactHits[index];
                if (hit.rigidbody == null || hit.rigidbody == capsule.attachedRigidbody ||
                    hit.rigidbody.GetComponentInParent<BoulderController>() == null || hit.distance >= nearest)
                    continue;
                if (Vector3.Dot(hit.normal, Vector3.up) >= BoulderTopNormal)
                    continue;
                nearest = hit.distance;
                boulderBody = hit.rigidbody;
            }
            return boulderBody != null;
        }

        public static bool TryGetBoulderStanceGeometry(CapsuleCollider capsule, Transform playerRoot,
            Rigidbody targetBody, Vector3 groundNormal, out BoulderPushStanceGeometry geometry)
        {
            geometry = default;
            if (capsule == null || playerRoot == null || targetBody == null)
                return false;
            BoulderController boulder = targetBody.GetComponentInParent<BoulderController>();
            Collider boulderCollider = boulder != null ? boulder.GetComponent<Collider>() : null;
            if (boulderCollider == null || !boulderCollider.enabled || boulderCollider.isTrigger)
                return false;

            Vector3 safeGroundNormal = groundNormal.sqrMagnitude > 0.01f ? groundNormal.normalized : Vector3.up;
            Vector3 playerCenter = capsule.bounds.center;
            Vector3 surfacePoint = boulderCollider.ClosestPoint(playerCenter);
            Vector3 outward = Vector3.ProjectOnPlane(playerCenter - targetBody.worldCenterOfMass, safeGroundNormal);
            if (outward.sqrMagnitude < 0.001f)
                outward = Vector3.ProjectOnPlane(-playerRoot.forward, safeGroundNormal);
            if (outward.sqrMagnitude < 0.001f)
                return false;
            outward.Normalize();
            Vector3 tangent = Vector3.Cross(outward, safeGroundNormal).normalized;
            Vector3 playerSurface = capsule.ClosestPoint(surfacePoint);
            float surfaceGap = Vector3.Distance(playerSurface, surfacePoint);
            geometry = new BoulderPushStanceGeometry(boulder, targetBody, surfacePoint, safeGroundNormal,
                outward, tangent, surfaceGap);
            return true;
        }

        public static Vector3 CalculateBoulderStanceVelocity(Vector3 currentVelocity,
            BoulderPushStanceGeometry geometry, Vector2 input, bool sprinting, Vector3 supportVelocity,
            float deltaTime)
        {
            Vector3 relative = currentVelocity - supportVelocity;
            // Follow translation of the live boulder surface without following its spin. The previous
            // implementation drove the capsule into the collider at PushSpeed every step, leaving
            // PhysX to separate the pair and producing a visible push/separate oscillation.
            Vector3 boulderRelativeVelocity =
                Vector3.ProjectOnPlane(geometry.Body.linearVelocity - supportVelocity, geometry.GroundNormal);
            float forwardInput = Mathf.Clamp01(input.y);
            float alignmentSpeed = Mathf.Clamp((geometry.SurfaceGap - BoulderStanceGap) *
                BoulderStanceAlignmentGain, -BoulderStanceAlignmentSpeed, BoulderStanceAlignmentSpeed);
            float targetInwardSpeed = Vector3.Dot(boulderRelativeVelocity, geometry.Inward) + alignmentSpeed;
            if (forwardInput > 0.01f)
                targetInwardSpeed = Mathf.Min(targetInwardSpeed, PushSpeed(sprinting) * forwardInput);
            float inwardSpeed = Mathf.MoveTowards(Vector3.Dot(relative, geometry.Inward), targetInwardSpeed,
                BoulderStanceAlignmentAcceleration * deltaTime);

            float targetTangentSpeed = Vector3.Dot(boulderRelativeVelocity, geometry.Tangent) +
                                       Mathf.Clamp(input.x, -1f, 1f) * BoulderStanceOrbitSpeed;
            float tangentAcceleration = Mathf.Abs(targetTangentSpeed) > 0.01f ? MoveAcceleration : BrakeAcceleration;
            float tangentSpeed = Mathf.MoveTowards(Vector3.Dot(relative, geometry.Tangent), targetTangentSpeed,
                tangentAcceleration * deltaTime);
            return geometry.Inward * inwardSpeed + geometry.Tangent * tangentSpeed + supportVelocity -
                   geometry.GroundNormal * GroundStickSpeed;
        }

        public static Vector3 BoulderPushDirection(BoulderPushStanceGeometry geometry) => geometry.Inward;

        public static Vector3 CalculateBoulderPushForce(BoulderPushStanceGeometry geometry, float forwardInput,
            bool sprinting)
        {
            float inputAmount = Mathf.Clamp01(forwardInput);
            if (inputAmount <= 0f || !geometry.IsValid)
                return Vector3.zero;
            float targetSpeed = PushSpeed(sprinting) * inputAmount;
            float currentSpeed = Vector3.Dot(
                Vector3.ProjectOnPlane(geometry.Body.linearVelocity, geometry.GroundNormal), geometry.Inward);
            float speedFactor = Mathf.Clamp01((targetSpeed - currentSpeed) / BoulderPushForceTaperSpeed);
            return geometry.Inward * PushForce(sprinting) * inputAmount * speedFactor;
        }

        public static bool ShouldBrakeBoulder(Vector2 input) =>
            input.sqrMagnitude <= BoulderBrakeInputDeadzone * BoulderBrakeInputDeadzone;

        public static float BoulderBrakeMultiplier(Vector2 input)
        {
            if (input.y < -BoulderBrakeInputDeadzone)
                return Mathf.Lerp(1f, BoulderBackwardBrakeMultiplier, Mathf.Clamp01(-input.y));
            return ShouldBrakeBoulder(input) ? 1f : 0f;
        }

        public static bool IsBoulderBrakeActive(BoulderPushStanceGeometry geometry, Vector2 input)
        {
            if (!geometry.IsValid || BoulderBrakeMultiplier(input) <= 0f)
                return false;
            Vector3 planarVelocity = Vector3.ProjectOnPlane(
                geometry.Body.linearVelocity, geometry.GroundNormal);
            return planarVelocity.magnitude > BoulderBrakeMinimumSpeed ||
                   Vector3.ProjectOnPlane(geometry.Body.angularVelocity, geometry.GroundNormal).magnitude >
                   BoulderBrakeMinimumSpeed;
        }

        public static Vector3 CalculateBoulderHoldForce(BoulderPushStanceGeometry geometry, Vector2 input,
            bool sprinting)
        {
            if (!geometry.IsValid)
                return Vector3.zero;
            if (input.y > 0.01f)
                return CalculateBoulderPushForce(geometry, input.y, sprinting);
            float brakeMultiplier = BoulderBrakeMultiplier(input);
            if (brakeMultiplier <= 0f)
                return Vector3.zero;

            Vector3 planarVelocity = Vector3.ProjectOnPlane(
                geometry.Body.linearVelocity, geometry.GroundNormal);
            float speed = planarVelocity.magnitude;
            if (speed <= BoulderBrakeMinimumSpeed)
                return Vector3.zero;
            float strength = BoulderBrakeForce * brakeMultiplier *
                             Mathf.Clamp01(speed / BoulderBrakeFullForceSpeed);
            return -planarVelocity.normalized * strength;
        }

        public static Vector3 CalculateBoulderHoldTorque(BoulderPushStanceGeometry geometry, Vector2 input)
        {
            float brakeMultiplier = BoulderBrakeMultiplier(input);
            if (!geometry.IsValid || brakeMultiplier <= 0f)
                return Vector3.zero;
            Vector3 rollingVelocity = Vector3.ProjectOnPlane(
                geometry.Body.angularVelocity, geometry.GroundNormal);
            float speed = rollingVelocity.magnitude;
            if (speed <= BoulderBrakeMinimumSpeed)
                return Vector3.zero;
            float strength = BoulderBrakeTorque * brakeMultiplier *
                             Mathf.Clamp01(speed / BoulderBrakeFullTorqueSpeed);
            return -rollingVelocity.normalized * strength;
        }

        public static bool ShouldExitBoulderStance(Vector2 input, bool jumpHeld) =>
            jumpHeld;

        public static Quaternion CalculateBoulderFacingRotation(Quaternion currentRotation, Vector3 inward,
            float deltaTime)
        {
            Vector3 horizontalInward = Vector3.ProjectOnPlane(inward, Vector3.up);
            if (horizontalInward.sqrMagnitude < 0.001f)
                return currentRotation;
            Quaternion target = Quaternion.LookRotation(horizontalInward.normalized, Vector3.up);
            return Quaternion.RotateTowards(currentRotation, target, BoulderStanceYawSpeed * deltaTime);
        }

        public static Vector3 CalculateCameraPresentationPosition(Vector3 current, Vector3 target, float deltaTime)
        {
            Vector3 toTarget = target - current;
            if (toTarget.sqrMagnitude >= CameraTeleportDistance * CameraTeleportDistance)
                return target;
            float amount = 1f - Mathf.Exp(-CameraPositionSharpness * Mathf.Max(0f, deltaTime));
            Vector3 next = Vector3.Lerp(current, target, amount);
            Vector3 remaining = next - target;
            if (remaining.sqrMagnitude > CameraMaximumLag * CameraMaximumLag)
                next = target + remaining.normalized * CameraMaximumLag;
            return next;
        }

        public static Vector2 CalculateLookDelta(Vector2 input, bool usesRate, float mouseSensitivity,
            float controllerDegreesPerSecond, float unscaledDeltaTime)
        {
            if (usesRate)
                return input * Mathf.Max(0f, controllerDegreesPerSecond) * Mathf.Max(0f, unscaledDeltaTime);
            return input * Mathf.Max(0f, mouseSensitivity);
        }

        public static Quaternion CalculateCameraPresentationRotation(Quaternion current, Quaternion target,
            float unscaledDeltaTime)
        {
            float amount = 1f - Mathf.Exp(-CameraRotationSharpness * Mathf.Max(0f, unscaledDeltaTime));
            Quaternion smoothed = Quaternion.Slerp(current, target, amount);
            float remaining = Quaternion.Angle(smoothed, target);
            return remaining > CameraMaximumAngularLag
                ? Quaternion.RotateTowards(target, smoothed, CameraMaximumAngularLag)
                : smoothed;
        }

        public static Vector3 SuppressBoulderClimbVelocity(Vector3 velocity, Vector3 expectedGroundVelocity,
            bool pushing, bool tookOff)
        {
            if (pushing && !tookOff && velocity.y > expectedGroundVelocity.y + 0.1f)
                velocity.y = expectedGroundVelocity.y + 0.1f;
            return velocity;
        }

        private static bool TryGetNearestStaticHit(Vector3 bottom, Vector3 top, float radius, Vector3 direction,
            float distance, out RaycastHit nearest)
        {
            int count = Physics.CapsuleCastNonAlloc(bottom, top, radius, direction, StepHits, distance,
                GameplayLayers.GroundQueryMask, QueryTriggerInteraction.Ignore);
            float closest = float.MaxValue;
            nearest = default;
            for (int index = 0; index < count; index++)
            {
                RaycastHit hit = StepHits[index];
                if (hit.collider == null || hit.rigidbody != null || hit.distance >= closest)
                    continue;
                closest = hit.distance;
                nearest = hit;
            }
            return nearest.collider != null;
        }

        private static bool TryGetNearestBlockingHit(Vector3 bottom, Vector3 top, float radius, Vector3 direction,
            float distance, Transform playerRoot)
        {
            int count = Physics.CapsuleCastNonAlloc(bottom, top, radius, direction, StepHits, distance,
                GameplayLayers.GroundQueryMask, QueryTriggerInteraction.Ignore);
            for (int index = 0; index < count; index++)
            {
                Collider hit = StepHits[index].collider;
                if (hit != null && !hit.transform.IsChildOf(playerRoot))
                    return true;
            }
            return false;
        }

        private static void GetWorldCapsule(CapsuleCollider capsule, out Vector3 bottom, out Vector3 top, out float radius)
        {
            Vector3 scale = capsule.transform.lossyScale;
            radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            float halfHeight = Mathf.Max(radius, capsule.height * Mathf.Abs(scale.y) * 0.5f);
            Vector3 center = capsule.transform.TransformPoint(capsule.center);
            Vector3 offset = capsule.transform.up * (halfHeight - radius);
            bottom = center - offset;
            top = center + offset;
        }
    }
}
