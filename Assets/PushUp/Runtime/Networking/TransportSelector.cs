using FishNet.Managing;
using FishNet.Managing.Transporting;
using FishNet.Transporting;
using FishNet.Transporting.Tugboat;
using UnityEngine;

namespace PushUp.Networking
{
    /// <summary>Chooses Steam for a player build and Tugboat for an Editor/development two-client loop.</summary>
    [DefaultExecutionOrder(int.MinValue)]
    [DisallowMultipleComponent]
    public sealed class TransportSelector : MonoBehaviour
    {
        [SerializeField] private bool _forceSteamTransport;

        public bool UsesSteamTransport => ShouldUseSteamTransport(Application.isEditor, Debug.isDebugBuild, _forceSteamTransport);

        public static bool ShouldUseSteamTransport(bool isEditor, bool isDebugBuild, bool forceSteam)
        {
#if PUSHUP_STEAM_PLAYTEST
            return true;
#else
            return forceSteam || (!isEditor && !isDebugBuild);
#endif
        }

        private void Awake()
        {
            NetworkManager manager = GetComponent<NetworkManager>();
            TransportManager transportManager = GetComponent<TransportManager>();
            if (manager == null || transportManager == null)
                return;

            Transport selected = UsesSteamTransport
                ? GetComponent<SteamSocketsTransport>()
                : GetComponent<Tugboat>();
            if (selected != null)
                transportManager.Transport = selected;
        }
    }
}
