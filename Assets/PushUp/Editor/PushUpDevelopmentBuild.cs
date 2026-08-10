using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PushUp.Core;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PushUp.Editor
{
    public static class PushUpDevelopmentBuild
    {
        private const string ScenePath = "Assets/PushUp/Scenes/Mountain.unity";
        private const string SteamPlaytestDefine = "PUSHUP_STEAM_PLAYTEST";

        public static void BuildWindowsX64() => BuildWindowsSteamPlaytest();

        public static void BuildSteamPlaytestAll()
        {
            BuildWindowsSteamPlaytest();
            BuildLinuxSteamPlaytest();
        }

        public static void BuildWindowsSteamPlaytest()
        {
            string folder = Path.Combine(GetBuildRoot(), "Windows");
            PrepareBuildFolder(folder);
            Build(folder, Path.Combine(folder, "PushUp.exe"), BuildTarget.StandaloneWindows64);
            WritePlaytestFiles(folder, false);
            WriteHashes(folder);
            WriteArchive(folder, "Windows");
        }

        public static void BuildLinuxSteamPlaytest()
        {
            string folder = Path.Combine(GetBuildRoot(), "Linux");
            PrepareBuildFolder(folder);
            Build(folder, Path.Combine(folder, "PushUp.x86_64"), BuildTarget.StandaloneLinux64);
            WritePlaytestFiles(folder, true);
            WriteHashes(folder);
            WriteArchive(folder, "Linux");
        }

        private static void PrepareBuildFolder(string folder)
        {
            string root = Path.GetFullPath(GetBuildRoot()).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string target = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string requiredPrefix = root + Path.DirectorySeparatorChar;
            if (!target.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException($"Refusing to clean build folder outside '{root}': '{target}'.");
            if (Directory.Exists(target))
                Directory.Delete(target, true);
            Directory.CreateDirectory(target);
        }

        private static void Build(string folder, string executable, BuildTarget target)
        {
            Directory.CreateDirectory(folder);
            NamedBuildTarget namedTarget = NamedBuildTarget.Standalone;
            ScriptingImplementation previousBackend = PlayerSettings.GetScriptingBackend(namedTarget);
            if (previousBackend != ScriptingImplementation.Mono2x)
                PlayerSettings.SetScriptingBackend(namedTarget, ScriptingImplementation.Mono2x);

            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = executable,
                    target = target,
                    options = BuildOptions.Development | BuildOptions.CompressWithLz4HC,
                    extraScriptingDefines = new[] { SteamPlaytestDefine }
                });
            }
            finally
            {
                if (PlayerSettings.GetScriptingBackend(namedTarget) != previousBackend)
                    PlayerSettings.SetScriptingBackend(namedTarget, previousBackend);
            }

            if (report.summary.result != BuildResult.Succeeded)
                throw new BuildFailedException($"Steam playtest {target} build failed: {report.summary.result}");

            Debug.Log($"Built {target} Steam playtest: {report.summary.outputPath} ({report.summary.totalSize} bytes)");
        }

        private static string GetBuildRoot()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length - 1; index++)
            {
                if (arguments[index].Equals("-pushUpBuildRoot", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(arguments[index + 1]);
            }
            return Path.GetFullPath("Builds/SteamPlaytest");
        }

        private static void WritePlaytestFiles(string folder, bool linux)
        {
            File.WriteAllText(Path.Combine(folder, "steam_appid.txt"), PushUpConstants.DevelopmentSteamAppId + Environment.NewLine, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "PLAYTEST_README.txt"), PlaytestInstructions(linux), new UTF8Encoding(false));

            if (linux)
            {
                const string launcher = "#!/usr/bin/env bash\nset -e\ncd -- \"$(dirname -- \"$0\")\"\nchmod +x ./PushUp.x86_64\nexec ./PushUp.x86_64 -pushup-playtest \"$@\"\n";
                File.WriteAllText(Path.Combine(folder, "run_pushup.sh"), launcher, new UTF8Encoding(false));
            }
            else
            {
                const string launcher = "@echo off\r\ncd /d \"%~dp0\"\r\nstart \"\" PushUp.exe -pushup-playtest\r\n";
                File.WriteAllText(Path.Combine(folder, "Run PushUp.bat"), launcher, new UTF8Encoding(false));
            }
        }

        private static void WriteHashes(string folder)
        {
            string hashPath = Path.Combine(folder, "SHA256SUMS.txt");
            using SHA256 sha256 = SHA256.Create();
            StringBuilder lines = new();
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                         .Where(file => !string.Equals(Path.GetFullPath(file), Path.GetFullPath(hashPath),
                             StringComparison.OrdinalIgnoreCase))
                         .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
            {
                using FileStream stream = File.OpenRead(file);
                string hash = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty)
                    .ToLowerInvariant();
                string relative = Path.GetFullPath(file).Substring(Path.GetFullPath(folder).Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                lines.Append(hash).Append("  ").Append(relative).AppendLine();
            }
            File.WriteAllText(hashPath, lines.ToString(), new UTF8Encoding(false));
        }

        private static void WriteArchive(string folder, string platform)
        {
            string archive = Path.Combine(Path.GetDirectoryName(folder) ?? GetBuildRoot(),
                $"PushUp-{Application.version}-{platform}.zip");
            if (File.Exists(archive))
                File.Delete(archive);
            ZipFile.CreateFromDirectory(folder, archive, System.IO.Compression.CompressionLevel.Optimal, false);
            using SHA256 sha256 = SHA256.Create();
            using FileStream stream = File.OpenRead(archive);
            File.WriteAllText(archive + ".sha256",
                BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant() + "  " +
                Path.GetFileName(archive) + Environment.NewLine, new UTF8Encoding(false));
        }

        private static string PlaytestInstructions(bool linux)
        {
            string launch = linux
                ? "Open a terminal in this folder, run: chmod +x run_pushup.sh PushUp.x86_64 && ./run_pushup.sh"
                : "Double-click Run PushUp.bat.";
            return $@"PUSHUP PRIVATE STEAM PLAYTEST - {Application.version}

This build uses Valve's Spacewar App ID 480 for private development testing only.
Each player needs Steam running, a different Steam account, and must already be Steam friends.
Close Spacewar or other App ID 480 tests before starting.

LAUNCH
{launch}

HOST
1. Launch the game first.
2. Click Host Steam Friends Game.
3. Wait for the Friends Lobby screen. No level objects are simulating yet.
4. Click Invite Friends, then click your friend's name in the in-game list. This works even if Steam Overlay is unavailable.
5. Wait for your friend to appear in the roster, then click Start Hill. The host socket, authoritative run, player, and boulder start in that order.
6. During the run, open Pause and click Invite Friends. The same direct friend list remains available even when Steam Overlay is disabled.
7. When someone leaves, their player and spawn slot are released. You may invite them or another friend into the still-running lobby immediately.

CLIENT
1. Launch the game. When the invite arrives in-game, click Join <friend>. You can also accept Steam's external Join action.
2. If the host has not started, remain on the Friends Lobby screen. This is expected and you may Leave Lobby at any time.
3. After the host clicks Start Hill, wait through Connecting, Authenticating, and Waiting for Player. The hill opens only after the run and your owned player are ready.
4. If no invite appears, click Join Friend Game, Refresh Friend Games, then Join <host>.
5. If the connection drops while the original host is still running, use Rejoin/Retry. A returning player reconnects to the same run and respawns at an available base slot.

TROUBLESHOOTING
- Steam must run as the same OS user as the game, not elevated as administrator.
- Both players must use build version {Application.version}.
- Steam may label this playtest as Spacewar because App ID 480 is Valve's test application.
- No router port forwarding is required; this build uses Steam Networking Sockets/SDR.
- Cancel is available during create/join/connect. A failed join offers Retry and Return; host departure has an explicit Host Ended screen. Detailed Steam end reasons are kept in Player.log.
- Steam Overlay is optional for invitations. The in-game friend list sends a direct lobby invite and is the preferred fallback for sideloaded App ID 480 builds.
- Windows Player.log: %USERPROFILE%\AppData\LocalLow\PushUp\PushUp\Player.log
- Linux Player.log: ~/.config/unity3d/PushUp/PushUp/Player.log
";
        }
    }
}
