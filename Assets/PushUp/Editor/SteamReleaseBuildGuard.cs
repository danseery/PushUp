using PushUp.Steam;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace PushUp.Editor
{
    /// <summary>Prevents an accidental public build with Valve's development App ID.</summary>
    public sealed class SteamReleaseBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if ((report.summary.options & BuildOptions.Development) != 0)
                return;

            SteamAppIdResolution resolution = SteamAppIdResolver.ResolveBuildConfiguration();
            string failure = SteamAppIdResolver.GetProductionBuildConfigurationError(resolution);
            if (!string.IsNullOrEmpty(failure))
                throw new BuildFailedException(failure);
        }
    }
}
