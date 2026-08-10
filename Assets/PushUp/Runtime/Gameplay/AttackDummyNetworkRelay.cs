using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace PushUp.Gameplay
{
    /// <summary>Keeps FishNet replication separate from the attack dummy's offline-capable AI.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject), typeof(AttackDummy))]
    public sealed class AttackDummyNetworkRelay : NetworkBehaviour
    {
        private AttackDummy _dummy;
        private uint _nextImpactSequence;

        internal uint SimulationTick => TimeManager != null ? TimeManager.Tick : 0u;

        private void Awake() => _dummy = GetComponent<AttackDummy>();

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();
            _dummy.SetNetworkSimulation(IsServerStarted);
        }

        public override void OnStopNetwork()
        {
            _dummy.ClearTarget();
            base.OnStopNetwork();
        }

        internal void DispatchAttack(Rigidbody target, bool push, bool left, Vector3 impulse, Vector3 hitPoint,
            uint simulationTick)
        {
            if (!IsServerStarted || target == null)
                return;
            NetworkObject targetObject = target.GetComponentInParent<NetworkObject>();
            if (targetObject != null)
            {
                PlayerImpactCommand command = CreateImpactCommand(target, push
                        ? PlayerImpactCommand.PushAction
                        : PlayerImpactCommand.PunchAction,
                    impulse, hitPoint, simulationTick);
                RouteImpactToOwner(targetObject, target, command);
                PlayAttackObserversRpc(targetObject, push, left, impulse);
            }
            else
            {
                PlayerInteraction.ApplyExternalImpulse(target, impulse, hitPoint, ObjectId, simulationTick);
                _dummy.PlayReplicatedAttack(target, push, left, impulse);
            }
        }

        internal void DispatchGrab(Rigidbody target, bool left, Vector3 impulse, Vector3 hitPoint,
            uint simulationTick)
        {
            if (!IsServerStarted || target == null)
                return;
            NetworkObject targetObject = target.GetComponentInParent<NetworkObject>();
            if (targetObject != null)
            {
                PlayerImpactCommand command = CreateImpactCommand(target, PlayerImpactCommand.GrabPullAction,
                    impulse, hitPoint, simulationTick);
                RouteImpactToOwner(targetObject, target, command);
                PlayGrabObserversRpc(targetObject, left, impulse);
            }
            else
            {
                PlayerInteraction.ApplyExternalImpulse(target, impulse, hitPoint, ObjectId, simulationTick);
                _dummy.PlayReplicatedGrab(target, left, impulse);
            }
        }

        [ObserversRpc(RunLocally = true)]
        private void PlayAttackObserversRpc(NetworkObject target, bool push, bool left, Vector3 impulse)
        {
            Rigidbody targetBody = target != null ? target.GetComponent<Rigidbody>() : null;
            _dummy.PlayReplicatedAttack(targetBody, push, left, impulse);
        }

        [ObserversRpc(RunLocally = true)]
        private void PlayGrabObserversRpc(NetworkObject target, bool left, Vector3 impulse)
        {
            Rigidbody targetBody = target != null ? target.GetComponent<Rigidbody>() : null;
            _dummy.PlayReplicatedGrab(targetBody, left, impulse);
        }

        private PlayerImpactCommand CreateImpactCommand(Rigidbody target, byte action, Vector3 impulse,
            Vector3 hitPoint, uint simulationTick)
        {
            unchecked
            {
                _nextImpactSequence++;
                if (_nextImpactSequence == 0u)
                    _nextImpactSequence++;
            }

            return new PlayerImpactCommand(ObjectId, _nextImpactSequence, simulationTick, action, impulse,
                target.transform.InverseTransformPoint(hitPoint));
        }

        [Server]
        private void RouteImpactToOwner(NetworkObject targetObject, Rigidbody targetBody,
            PlayerImpactCommand command)
        {
            NetworkConnection owner = targetObject != null ? targetObject.Owner : null;
            if (owner != null && owner.IsActive && !owner.IsLocalClient)
            {
                ApplyImpactTargetRpc(owner, targetObject, command);
                return;
            }

            // The host player (and any intentionally unowned target) is simulated
            // in the server process, so no network round-trip is needed.
            PlayerImpactRouting.ApplyToLocalAuthority(targetBody, command);
        }

        [TargetRpc]
        private void ApplyImpactTargetRpc(NetworkConnection connection, NetworkObject target,
            PlayerImpactCommand command)
        {
            if (target == null || !target.IsOwner)
                return;
            Rigidbody targetBody = target.GetComponent<Rigidbody>();
            PlayerImpactRouting.ApplyToLocalAuthority(targetBody, command);
        }

        internal void BroadcastAggro(bool active) => SetAggroObserversRpc(active);

        [ObserversRpc(BufferLast = true, RunLocally = true)]
        private void SetAggroObserversRpc(bool active) => _dummy.SetReplicatedAggro(active);

        internal void BroadcastThreat(Rigidbody target, bool active)
        {
            NetworkObject targetObject = target != null ? target.GetComponentInParent<NetworkObject>() : null;
            if (targetObject != null)
                SetThreatObserversRpc(targetObject, active);
        }

        [ObserversRpc(RunLocally = true)]
        private void SetThreatObserversRpc(NetworkObject target, bool active) =>
            target?.GetComponent<PlayerInteraction>()?.SetFighterThreat(active);
    }
}
