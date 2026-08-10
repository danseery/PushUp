using System;
using UnityEngine;

namespace PushUp.Gameplay
{
    /// <summary>
    /// A server-validated, owner-directed impulse. SourceObjectId and Sequence form
    /// the stable identity used by the owning player to reject duplicate delivery.
    /// The contact point is target-local so it remains valid while the target moves
    /// between server validation and client delivery.
    /// </summary>
    [Serializable]
    public struct PlayerImpactCommand
    {
        public const byte PunchAction = 0;
        public const byte PushAction = 1;
        public const byte GrabPullAction = 2;

        public int SourceObjectId;
        public uint Sequence;
        public uint SimulationTick;
        public byte Action;
        public Vector3 Impulse;
        public Vector3 LocalHitPoint;

        public PlayerImpactCommand(int sourceObjectId, uint sequence, uint simulationTick, byte action,
            Vector3 impulse, Vector3 localHitPoint)
        {
            SourceObjectId = sourceObjectId;
            Sequence = sequence;
            SimulationTick = simulationTick;
            Action = action;
            Impulse = impulse;
            LocalHitPoint = localHitPoint;
        }

        public readonly bool IsPush => Action == PushAction;
        public readonly bool IsGrabPull => Action == GrabPullAction;
    }

    /// <summary>
    /// Implemented by the locally simulated player actor. Implementations must
    /// deduplicate by (SourceObjectId, Sequence) before applying physics.
    /// </summary>
    public interface IOwnerPlayerImpactReceiver
    {
        bool ApplyImpact(PlayerImpactCommand command);
    }

    public static class PlayerImpactRouting
    {
        /// <summary>
        /// Applies a validated command to the simulation authority currently
        /// holding <paramref name="body"/>. Networking code must call this only on
        /// the target owner (or for a server-local/unowned target).
        /// </summary>
        public static bool ApplyToLocalAuthority(Rigidbody body, PlayerImpactCommand command)
        {
            if (body == null)
                return false;

            if (body.GetComponent<IOwnerPlayerImpactReceiver>() is { } ownerReceiver)
                return ownerReceiver.ApplyImpact(command);

            // Compatibility for the existing predicted motor while the player
            // authority migration is being completed. Use the monotonic command
            // sequence as the legacy deduplication tick; the actual validation tick
            // remains available on PlayerImpactCommand.
            if (body.GetComponent<IExternalImpulseReceiver>() is { } legacyReceiver)
            {
                Vector3 worldPoint = body.transform.TransformPoint(command.LocalHitPoint);
                return legacyReceiver.TryApplyExternalImpulse(command.Sequence, command.SourceObjectId,
                    command.Impulse, worldPoint);
            }

            body.AddForceAtPosition(command.Impulse, body.transform.TransformPoint(command.LocalHitPoint),
                ForceMode.Impulse);
            return true;
        }
    }
}
