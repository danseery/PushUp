using PushUp.Core;
using Steamworks;
using UnityEngine;

namespace PushUp.Steam
{
    [DefaultExecutionOrder(-10000)]
    public sealed class SteamBootstrap : MonoBehaviour
    {
        public static bool IsAvailable { get; private set; }
        public static bool RestartRequested { get; private set; }
        public static string FailureReason { get; private set; }
        public static uint EffectiveAppId { get; private set; }
        public static string AppIdSource { get; private set; }

        private bool _initialized;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            TryInitialize();
        }

        private void Update()
        {
            if (_initialized)
                SteamAPI.RunCallbacks();
        }

        private void OnApplicationQuit()
        {
            Shutdown();
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private void TryInitialize()
        {
            try
            {
                IsAvailable = false;
                RestartRequested = false;
                FailureReason = string.Empty;
                EffectiveAppId = 0;
                AppIdSource = string.Empty;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                const bool allowDevelopmentFallback = true;
#else
                const bool allowDevelopmentFallback = false;
#endif
                SteamAppIdResolution resolution = SteamAppIdResolver.ResolveRuntime(allowDevelopmentFallback);
                if (!resolution.IsValid)
                {
                    FailureReason = string.IsNullOrWhiteSpace(resolution.Source)
                        ? "Steam App ID is missing. Configure the release App ID through Steam, " +
                          "-steamAppId, PUSHUP_STEAM_APP_ID, or steam_appid.txt."
                        : $"Steam App ID from {resolution.Source} is invalid.";
                    return;
                }
                if (!SteamAppIdResolver.IsAllowedForBuild(resolution, allowDevelopmentFallback))
                {
                    FailureReason = "A shipping build cannot use the Spacewar App ID 480 unless it is explicitly " +
                                    "launched as a PushUp playtest.";
                    return;
                }

                EffectiveAppId = resolution.AppId;
                AppIdSource = resolution.Source;
                AppId_t appId = new AppId_t(EffectiveAppId);
                if (SteamAPI.RestartAppIfNecessary(appId))
                {
                    RestartRequested = true;
                    FailureReason = "Restarting through Steam.";
                    Application.Quit();
                    return;
                }

                _initialized = SteamAPI.Init();
                IsAvailable = _initialized;
                FailureReason = _initialized ? string.Empty : "Steam is not running or the app ID is unavailable.";
                if (_initialized)
                {
                    SteamNetworkingUtils.InitRelayNetworkAccess();
                    Debug.Log($"Steam initialized as {SteamUser.GetSteamID().m_SteamID} for App ID " +
                              $"{EffectiveAppId} ({AppIdSource}).");
                }
                else
                    Debug.LogError(FailureReason);
            }
            catch (System.DllNotFoundException)
            {
                FailureReason = "Steamworks native binaries are unavailable.";
            }
            catch (System.Exception exception)
            {
                FailureReason = exception.Message;
                Debug.LogException(exception);
            }
        }

        private void Shutdown()
        {
            if (!_initialized)
                return;

            _initialized = false;
            IsAvailable = false;
            SteamAPI.Shutdown();
        }
    }
}
