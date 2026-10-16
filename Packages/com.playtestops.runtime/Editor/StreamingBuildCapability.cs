using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PlaytestOps.RuntimeStreaming.Editor
{
    internal sealed class StreamingBuildCapability : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.StandaloneWindows64) return;
            var marker = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report.summary.outputPath)), "playtestops-streaming.json");
            if (File.Exists(marker)) File.Delete(marker);
#if !ENABLE_INPUT_SYSTEM
            throw new BuildFailedException("PlaytestOps Runtime Streaming requires Active Input Handling to be Input System Package (New) or Both. Change Player Settings > Other Settings > Configuration, restart the Editor when prompted, and rebuild.");
#endif
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.StandaloneWindows64
                || report.summary.result == BuildResult.Failed || report.summary.result == BuildResult.Cancelled) return;
            var directory = Path.GetDirectoryName(Path.GetFullPath(report.summary.outputPath));
            var marker = Path.Combine(directory, "playtestops-streaming.json");
#if ENABLE_INPUT_SYSTEM
            var capability = new Capability { buildGuid = report.summary.guid.ToString() };
            File.WriteAllText(marker, JsonUtility.ToJson(capability), new UTF8Encoding(false));
#else
            // A rebuild with Input System disabled must not retain a stale capability marker.
            if (File.Exists(marker)) File.Delete(marker);
#endif
        }

        [Serializable]
        private sealed class Capability
        {
            public int protocolVersion = 1;
            public string packageVersion = "0.1.0";
            public string buildGuid;
            public string capture = "framebuffer";
            public string input = "input-system";
            public string transport = "webrtc";
        }
    }
}
