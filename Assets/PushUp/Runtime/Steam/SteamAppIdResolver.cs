using System;
using System.Collections.Generic;
using System.IO;
using PushUp.Core;

namespace PushUp.Steam
{
    public readonly struct SteamAppIdResolution
    {
        public SteamAppIdResolution(uint appId, string source, bool isPlaytest)
        {
            AppId = appId;
            Source = source ?? string.Empty;
            IsPlaytest = isPlaytest;
        }

        public uint AppId { get; }
        public string Source { get; }
        public bool IsPlaytest { get; }
        public bool IsValid => AppId != 0;
    }

    public static class SteamAppIdResolver
    {
        public const string AppIdEnvironmentVariable = "PUSHUP_STEAM_APP_ID";
        public const string PlaytestEnvironmentVariable = "PUSHUP_STEAM_PLAYTEST";

        public static SteamAppIdResolution Resolve(IReadOnlyList<string> arguments,
            Func<string, string> getEnvironmentVariable, string appIdFileContents,
            bool allowDevelopmentFallback)
        {
            arguments ??= Array.Empty<string>();
            getEnvironmentVariable ??= _ => null;
            bool playtest = ContainsPlaytestFlag(arguments) ||
                            IsTruthy(getEnvironmentVariable(PlaytestEnvironmentVariable));
            uint appId;

            if (TryGetCommandLineAppIdValue(arguments, out string commandLineValue))
                return TryParseAppId(commandLineValue, out appId)
                    ? new SteamAppIdResolution(appId, "command line", playtest)
                    : new SteamAppIdResolution(0, "command line", playtest);
            string environmentAppId = getEnvironmentVariable(AppIdEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environmentAppId))
                return TryParseAppId(environmentAppId, out appId)
                    ? new SteamAppIdResolution(appId, AppIdEnvironmentVariable, playtest)
                    : new SteamAppIdResolution(0, AppIdEnvironmentVariable, playtest);
            string steamEnvironmentAppId = getEnvironmentVariable("SteamAppId");
            if (!string.IsNullOrWhiteSpace(steamEnvironmentAppId))
                return TryParseAppId(steamEnvironmentAppId, out appId)
                    ? new SteamAppIdResolution(appId, "SteamAppId", playtest)
                    : new SteamAppIdResolution(0, "SteamAppId", playtest);
            if (!string.IsNullOrWhiteSpace(appIdFileContents))
                return TryParseAppId(appIdFileContents, out appId)
                    ? new SteamAppIdResolution(appId, "steam_appid.txt", playtest)
                    : new SteamAppIdResolution(0, "steam_appid.txt", playtest);
            if (allowDevelopmentFallback || playtest)
                return new SteamAppIdResolution(PushUpConstants.DevelopmentSteamAppId,
                    allowDevelopmentFallback ? "development fallback" : "playtest fallback", playtest);
            return new SteamAppIdResolution(0, string.Empty, playtest);
        }

        public static SteamAppIdResolution ResolveRuntime(bool allowDevelopmentFallback)
        {
            string fileContents = null;
            string path = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
            try
            {
                if (File.Exists(path))
                    fileContents = File.ReadAllText(path);
            }
            catch (IOException)
            {
                // Bootstrap reports the missing/invalid ID through the normal resolution result.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return Resolve(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable,
                fileContents, allowDevelopmentFallback);
        }

        /// <summary>
        /// Resolves only inputs that explicitly configure the current build process. Runtime-only sources such as
        /// SteamAppId and steam_appid.txt must not authorize a non-development player build.
        /// </summary>
        public static SteamAppIdResolution ResolveBuildConfiguration() =>
            ResolveBuildConfiguration(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable);

        public static SteamAppIdResolution ResolveBuildConfiguration(IReadOnlyList<string> arguments,
            Func<string, string> getEnvironmentVariable)
        {
            getEnvironmentVariable ??= _ => null;
            return Resolve(arguments,
                key => string.Equals(key, AppIdEnvironmentVariable, StringComparison.Ordinal)
                    ? getEnvironmentVariable(key)
                    : null,
                null, false);
        }

        public static bool TryReadCommandLineAppId(IReadOnlyList<string> arguments, out uint appId)
        {
            appId = 0;
            return TryGetCommandLineAppIdValue(arguments, out string value) && TryParseAppId(value, out appId);
        }

        private static bool TryGetCommandLineAppIdValue(IReadOnlyList<string> arguments, out string value)
        {
            value = string.Empty;
            if (arguments == null)
                return false;
            for (int index = 0; index < arguments.Count; index++)
            {
                string argument = arguments[index] ?? string.Empty;
                int equals = argument.IndexOf('=');
                string key = equals >= 0 ? argument.Substring(0, equals) : argument;
                if (!IsAppIdOption(key))
                    continue;
                value = equals >= 0
                    ? argument.Substring(equals + 1)
                    : index + 1 < arguments.Count ? arguments[index + 1] : string.Empty;
                return true;
            }
            return false;
        }

        public static bool TryParseAppId(string value, out uint appId) =>
            uint.TryParse(value?.Trim(), out appId) && appId != 0;

        public static bool IsAllowedForBuild(SteamAppIdResolution resolution, bool allowDevelopmentAppId) =>
            resolution.IsValid && (resolution.AppId != PushUpConstants.DevelopmentSteamAppId ||
                                   allowDevelopmentAppId || resolution.IsPlaytest);

        /// <summary>
        /// Validates the explicit App ID used to authorize a non-development player build. Playtest intent does not
        /// relax this policy: App ID 480 belongs only to the dedicated development playtest build path.
        /// </summary>
        public static string GetProductionBuildConfigurationError(SteamAppIdResolution resolution)
        {
            if (!resolution.IsValid)
            {
                return string.IsNullOrWhiteSpace(resolution.Source)
                    ? $"A production Steam App ID is required for non-development builds. Supply it through " +
                      $"-steamAppId or {AppIdEnvironmentVariable}."
                    : $"The production Steam App ID from {resolution.Source} is invalid.";
            }

            if (resolution.AppId == PushUpConstants.DevelopmentSteamAppId)
            {
                return $"Non-development builds cannot use Valve's development App ID " +
                       $"{PushUpConstants.DevelopmentSteamAppId}. Use the dedicated Steam playtest build instead.";
            }

            return string.Empty;
        }

        private static bool IsAppIdOption(string value) =>
            string.Equals(value, "-steamAppId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "-steam-app-id", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "--steam-app-id", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "+steam_appid", StringComparison.OrdinalIgnoreCase);

        private static bool ContainsPlaytestFlag(IReadOnlyList<string> arguments)
        {
            for (int index = 0; index < arguments.Count; index++)
            {
                if (string.Equals(arguments[index], "-pushup-playtest", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arguments[index], "--pushup-playtest", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsTruthy(string value) =>
            string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
