using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace PlaytestOps.Editor
{
    internal static class SourceSettings
    {
        private const string SettingsPath = "ProjectSettings/PlaytestOpsSource.json";
        [Serializable] private sealed class Configuration { public string[] roots; }

        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        public static string[] Load(string projectRoot)
        {
            var path = Path.Combine(projectRoot, SettingsPath);
            SourceReader.EnsureNoReparsePoints(path, true);
            if (!File.Exists(path)) return (string[])SourceReader.DefaultRoots.Clone();
            string json;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new MemoryStream())
            {
                if (stream.Length > 65536) throw new InvalidOperationException("Source-folder settings exceed the supported size.");
                var buffer = new byte[8192];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + count > 65536) throw new InvalidOperationException("Source-folder settings exceed the supported size.");
                    output.Write(buffer, 0, count);
                }
                SourceReader.EnsureNoReparsePoints(path, false);
                json = new UTF8Encoding(false, true).GetString(output.ToArray());
            }
            if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
            var configuration = JsonUtility.FromJson<Configuration>(json);
            if (configuration?.roots == null) throw new InvalidOperationException("Source-folder settings are invalid. Save an explicit author-folder list in the PlaytestOps window.");
            // Validate even if callers do not immediately make a source request.
            new SourceReader(projectRoot, configuration.roots);
            return configuration.roots;
        }

        // Only called by the explicit Save source folders UI action.
        public static void Save(string projectRoot, string[] roots)
        {
            new SourceReader(projectRoot, roots);
            var path = Path.Combine(projectRoot, SettingsPath);
            SourceReader.EnsureNoReparsePoints(path, true);
            File.WriteAllText(path, JsonUtility.ToJson(new Configuration { roots = roots }, true), new UTF8Encoding(false, true));
        }
    }
}
