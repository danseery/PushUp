using System;
using PushUp.Core;

namespace PushUp.Steam
{
    public readonly struct LobbyMetadata
    {
        public readonly string Protocol;
        public readonly string Build;
        public readonly ulong HostSteamId;
        public readonly string RunState;

        public LobbyMetadata(string protocol, string build, ulong hostSteamId, string runState)
        {
            Protocol = protocol;
            Build = build;
            HostSteamId = hostSteamId;
            RunState = runState;
        }

        public bool IsCompatible(string localBuild) =>
            Protocol == PushUpConstants.ProtocolVersion && Build == localBuild && HostSteamId != 0 &&
            SteamSessionService.IsKnownRunState(RunState);

        public static bool TryParse(string protocol, string build, string host, string runState, out LobbyMetadata result)
        {
            result = default;
            if (!ulong.TryParse(host, out ulong hostId))
                return false;
            result = new LobbyMetadata(protocol, build, hostId, runState);
            return true;
        }
    }
}
