using System;
using FishNet.Object;
using UnityEngine;

namespace PushUp.Gameplay
{
    public enum NetworkRunPhase : byte
    {
        Waiting,
        Playing,
        Complete,
        Ending
    }

    /// <summary>
    /// Lightweight late-join state carried by the primary boulder's NetworkObject.
    /// Steam lobby metadata is discovery state; this component is gameplay truth.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetworkRunState : NetworkBehaviour
    {
        private bool _lastAnchored;

        public static NetworkRunState Active { get; private set; }

        public event Action<NetworkRunState> Changed;

        public NetworkRunPhase Phase { get; private set; } = NetworkRunPhase.Waiting;
        public uint StartedTick { get; private set; }
        public float CompletionSeconds { get; private set; }
        public bool IsBoulderAnchored { get; private set; }
        public bool IsReady => Phase is NetworkRunPhase.Playing or NetworkRunPhase.Complete;
        public BoulderController Boulder { get; private set; }

        private void Awake() => Boulder = GetComponent<BoulderController>();

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();
            Active = this;
            _lastAnchored = Boulder != null && Boulder.IsAnchored;
        }

        public override void OnStopNetwork()
        {
            if (Active == this)
                Active = null;
            base.OnStopNetwork();
        }

        private void Update()
        {
            if (!IsServerStarted || Boulder == null)
                return;
            bool anchored = Boulder.IsAnchored;
            if (anchored == _lastAnchored)
                return;
            _lastAnchored = anchored;
            SetAnchorObserversRpc(anchored);
        }

        public void BeginRun(uint startedTick)
        {
            if (!IsServerStarted)
                return;
            SetPhaseObserversRpc(NetworkRunPhase.Playing, startedTick, 0f);
        }

        public void CompleteRun(float completionSeconds)
        {
            if (!IsServerStarted)
                return;
            SetPhaseObserversRpc(NetworkRunPhase.Complete, StartedTick, Mathf.Max(0f, completionSeconds));
        }

        public void EndRun()
        {
            if (!IsServerStarted)
                return;
            SetPhaseObserversRpc(NetworkRunPhase.Ending, StartedTick, CompletionSeconds);
        }

        [ObserversRpc(BufferLast = true, RunLocally = true)]
        private void SetPhaseObserversRpc(NetworkRunPhase phase, uint startedTick, float completionSeconds)
        {
            Phase = phase;
            StartedTick = startedTick;
            CompletionSeconds = completionSeconds;
            Changed?.Invoke(this);
        }

        [ObserversRpc(BufferLast = true, RunLocally = true)]
        private void SetAnchorObserversRpc(bool anchored)
        {
            _lastAnchored = anchored;
            IsBoulderAnchored = anchored;
            Changed?.Invoke(this);
        }
    }
}
