using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlaytestOps.Editor
{
    [Serializable] public sealed class BuildLogMessage
    {
        public int sequence; public string timestampUtc = "", level = "Debug", message = "", stackTrace = ""; public bool truncated;
    }
    [Serializable] internal sealed class BuildJob
    {
        public string buildId = "", buildToken = "", origin = "", revision = "", requestedUtc = "";
        public string state = "Running", phase = "Preparing", message = "", outputPath = "", executablePath = "", manifestPath = "", profilePath = "", activeTarget = "", unityVersion = "";
        public double durationSeconds; public long totalBytes; public int totalWarnings, totalErrors, droppedLogCount, logCount, ackLogSequence, wireSequence, version = 1, ackVersion;
        public bool logsTruncated, serverClosed;
        public bool deliveryQuarantined;
        public string deliveryError = "";
    }
    [Serializable] internal sealed class BuildWireMessage
    {
        public string type = "build.update", buildId = "", buildToken = "", state = "", phase = "", message = "";
        public string outputPath = "", executablePath = "", manifestPath = "", profilePath = "", activeTarget = "", unityVersion = "";
        public int sequence, totalWarnings, totalErrors, droppedLogCount; public double durationSeconds; public long totalBytes; public bool logsTruncated;
        public BuildLogMessage[] logs = new BuildLogMessage[0];
    }
    [Serializable] internal sealed class BuildAck
    {
        public string type = "", buildId = "", error = "", state = ""; public int sequence, logSequence;
    }
    [Serializable] internal sealed class BuildArtifact
    {
        public string path = "", sha256 = ""; public long bytes;
    }
    [Serializable] internal sealed class BuildArtifactManifest
    {
        public int protocolVersion = 1; public string buildId = "", requestedUtc = "", finishedUtc = "", unityVersion = "", profilePath = "", activeTarget = "", revision = "", result = "", buildGuid = "";
        public double durationSeconds; public long totalBytes; public int totalWarnings, totalErrors; public bool artifactListTruncated;
        public string[] scenes = new string[0]; public BuildArtifact[] artifacts = new BuildArtifact[0];
    }

    [InitializeOnLoad]
    public static class WindowsBuildExecution
    {
        private const int MaximumLogs = 10000, MaximumCharacters = 8 * 1024 * 1024;
        private static readonly object Gate = new object();
        private static readonly SemaphoreSlim DeliveryWake = new SemaphoreSlim(0, 1);
        private sealed class Local
        {
            public BuildJob job;
            public List<BuildLogMessage> logs = new List<BuildLogMessage>();
            public int characters;
            public BuildWireMessage flight;
            public int flightVersion;
            public StreamWriter writer;
            public bool captureComplete;
            public bool persistenceFailed;
        }
        private static readonly Dictionary<string, Local> Jobs = new Dictionary<string, Local>(StringComparer.Ordinal);
        private static readonly string ProjectRoot = Path.GetDirectoryName(Application.dataPath);
        private static readonly string LedgerRoot = Path.Combine(ProjectRoot, "Library", "PlaytestOps", "BuildExecution");
        private static string activeId;
        private static Local pendingExecution;
        public static bool Busy { get { lock (Gate) return activeId != null; } }

        static WindowsBuildExecution()
        {
            // Credentials and ownership are retained only in ignored Library files.
            // A restored ledger can report a result, never re-enter BuildPipeline.
            try
            {
                if (!Directory.Exists(LedgerRoot)) return;
                foreach (var directory in new DirectoryInfo(LedgerRoot).EnumerateDirectories().OrderByDescending(item => item.LastWriteTimeUtc).Take(50))
                {
                    if (!Guid.TryParseExact(directory.Name, "N", out _)) continue;
                    var local = Load(directory.Name);
                    if (local == null || local.job.serverClosed) continue;
                    if (local.job.state == "Running")
                    {
                        local.job.state = "Unknown"; local.job.phase = "Editor reloaded";
                        local.job.message = "Editor reload/exit interrupted build reporting. Output may exist; this build will not be executed again.";
                        local.job.version++; Save(local);
                    }
                    if (local.job.ackVersion < local.job.version || local.job.ackLogSequence < local.logs.Count) Jobs[local.job.buildId] = local;
                }
            }
            catch { /* A damaged ledger is never executable. Existing output is preserved. */ }
        }

        internal static void Request(string id, string token, string revision, string origin)
        {
            if (!Guid.TryParseExact(id, "N", out _) || token == null || token.Length != 64 || !token.All(Uri.IsHexDigit) || revision == null || revision.Length != 64) return;
            Local local;
            lock (Gate)
            {
                if (Jobs.TryGetValue(id, out local)) return;
                if (Directory.Exists(Path.Combine(LedgerRoot, id)))
                {
                    local = Load(id);
                    if (local != null && local.job.buildToken == token && local.job.origin == origin) { local.flight = null; local.job.ackVersion = 0; Jobs[id] = local; }
                    return; // Never execute an ID already received, even with a broken ledger.
                }
                Directory.CreateDirectory(Path.Combine(LedgerRoot, id));
                local = new Local { job = new BuildJob { buildId = id, buildToken = token, revision = revision, origin = origin, requestedUtc = Utc(),
                    unityVersion = Application.unityVersion, activeTarget = WindowsBuildConfiguration.IsWindows(EditorUserBuildSettings.activeBuildTarget) ? EditorUserBuildSettings.activeBuildTarget.ToString() : "" } };
                Jobs[id] = local;
                if (!Save(local)) { local.job.state = "Rejected"; local.job.phase = "Ledger unavailable"; local.job.message = "Could not durably record build ownership. No build started."; local.job.version++; return; }
                // Capture preflight exceptions too, not only logs after BuildPipeline starts.
                try { local.writer = new StreamWriter(Path.Combine(LedgerRoot, id, "logs.jsonl"), true, new UTF8Encoding(false)) { AutoFlush = true }; }
                catch { PersistenceLost(local); local.job.state = "Rejected"; local.job.phase = "Log file unavailable"; local.job.message = "Could not open the build's local log ledger. No build started."; local.job.version++; Save(local); return; }
            }
            if (Busy || EditorBusy()) { Reject(local, "Unity is busy with another build, tests, import, compilation or Play Mode."); return; }
            var snapshot = WindowsBuildConfiguration.ReadForBuild();
            if (snapshot.state != "available" || !snapshot.fields.Any(field => field.editable) || snapshot.revision != revision || !WindowsBuildConfiguration.IsWindows(EditorUserBuildSettings.activeBuildTarget))
            { Reject(local, "The Windows build configuration changed or cannot be read safely. Refresh before requesting a new build."); return; }
            for (var i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) { Reject(local, "Save modified scenes in Unity before building. PlaytestOps will not save or discard them."); return; }
            lock (Gate) activeId = id;
            // One-shot update is reliable with a background/unfocused Editor;
            // delayCall can wait indefinitely for an Inspector repaint.
            pendingExecution = local;
            EditorApplication.update -= ExecutePending;
            EditorApplication.update += ExecutePending;
            EditorApplication.QueuePlayerLoopUpdate();
        }
        private static void ExecutePending()
        {
            EditorApplication.update -= ExecutePending;
            var local = pendingExecution; pendingExecution = null;
            if (local == null) return;
            lock (Gate)
                if (activeId != local.job.buildId || local.job.state != "Running" || local.job.phase != "Preparing") return;
            Execute(local);
        }
        private static bool EditorBusy() => PlaytestSession.Busy || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer;
        private static void Reject(Local local, string message)
        {
            Append(local, "Error", message, "");
            lock (Gate)
            {
                local.captureComplete = true; local.job.state = "Rejected"; local.job.phase = "Not started"; local.job.message = Summary(message);
                local.job.version++; CloseWriter(local); Save(local);
            }
        }

        private static void Execute(Local local)
        {
            var started = DateTime.UtcNow;
            var capture = false; var reloadLocked = false;
            try
            {
                if (EditorBusy()) throw new InvalidOperationException("Unity became busy before the build could start.");
                for (var i = 0; i < SceneManager.sceneCount; i++)
                    if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("A scene became modified before execution. Save it in Unity before requesting another build.");
                var snapshot = WindowsBuildConfiguration.ReadForBuild();
                if (snapshot.state != "available" || snapshot.revision != local.job.revision) throw new InvalidOperationException("Build configuration changed before execution. No build started.");
                ValidatePlayerConfiguration(snapshot);
                var profile = BuildProfile.GetActiveBuildProfile();
                var scenes = profile != null ? profile.GetScenesForBuild() : EditorBuildSettings.scenes;
                var enabled = scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
                if (enabled.Length == 0 || enabled.Any(path => string.IsNullOrEmpty(path) || AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null))
                    throw new InvalidOperationException("The effective build scene list has no enabled scenes or contains a missing scene.");
                if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, EditorUserBuildSettings.activeBuildTarget))
                    throw new InvalidOperationException("The active Windows build target module is not installed.");
                var output = Path.Combine(ProjectRoot, "Builds", "PlaytestOps", local.job.buildId);
                ValidateOutputParents();
                if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("This build's output folder already exists. It will never be overwritten.");
                Directory.CreateDirectory(output);
                using (new FileStream(Path.Combine(output, ".playtestops-build"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                lock (Gate)
                {
                    local.job.outputPath = output; local.job.executablePath = Path.Combine(output, "Player.exe"); local.job.manifestPath = Path.Combine(output, "build-manifest.json");
                    local.job.profilePath = profile == null ? "" : AssetDatabase.GetAssetPath(profile); local.job.phase = "Building player"; local.job.version++;
                    Save(local);
                }
                Application.logMessageReceivedThreaded += Capture;
                capture = true;
                Append(local, "Debug", "PlaytestOps started local Windows build " + local.job.buildId + ". Effective scenes: " + string.Join(", ", enabled), "");
                EditorApplication.LockReloadAssemblies(); reloadLocked = true;
                BuildReport report;
                if (profile != null)
                    report = BuildPipeline.BuildPlayer(new BuildPlayerWithProfileOptions { buildProfile = profile, locationPathName = local.job.executablePath, options = BuildOptions.DetailedBuildReport });
                else
                    report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = enabled, target = EditorUserBuildSettings.activeBuildTarget,
                        subtarget = (int)EditorUserBuildSettings.standaloneBuildSubtarget, locationPathName = local.job.executablePath, options = LegacyOptions(local.job.executablePath) | BuildOptions.DetailedBuildReport });
                if (report == null) throw new InvalidOperationException("Unity did not return a BuildReport.");
                foreach (var step in report.steps)
                    foreach (var message in step.messages) Append(local, Level(message.type), "[BuildReport: " + step.name + "] " + message.content, "");
                var summary = report.summary;
                var succeeded = summary.result == BuildResult.Succeeded && summary.totalErrors == 0 && File.Exists(local.job.executablePath) && new FileInfo(local.job.executablePath).Length > 0;
                var manifest = new BuildArtifactManifest { buildId = local.job.buildId, requestedUtc = local.job.requestedUtc, finishedUtc = Utc(),
                    unityVersion = Application.unityVersion, profilePath = local.job.profilePath, activeTarget = local.job.activeTarget, revision = local.job.revision,
                    result = summary.result.ToString(), buildGuid = summary.guid.ToString(), durationSeconds = summary.totalTime.TotalSeconds,
                    totalBytes = checked((long)summary.totalSize), totalWarnings = checked((int)summary.totalWarnings), totalErrors = checked((int)summary.totalErrors), scenes = enabled };
                var files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != ".playtestops-build").Take(10001).ToArray();
                manifest.artifactListTruncated = files.Length > 10000;
                manifest.artifacts = files.Take(10000).Select(path => new BuildArtifact { path = path.Substring(output.Length + 1).Replace('\\', '/'), bytes = new FileInfo(path).Length,
                    sha256 = path == local.job.executablePath || IsStreamingIdentityArtifact(path) ? FileHash(path) : "" }).ToArray();
                using (var file = new FileStream(local.job.manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false))) writer.Write(JsonUtility.ToJson(manifest, true));
                lock (Gate)
                {
                    local.captureComplete = true;
                    local.job.durationSeconds = manifest.durationSeconds; local.job.totalBytes = manifest.totalBytes; local.job.totalWarnings = manifest.totalWarnings; local.job.totalErrors = manifest.totalErrors;
                    local.job.state = succeeded ? "Succeeded" : "Failed"; local.job.phase = succeeded ? "Completed" : "Build failed";
                    local.job.message = succeeded ? "Unity BuildReport succeeded. Player.exe and the artifact manifest were verified. No player was launched." :
                        summary.result == BuildResult.Succeeded ? "Unity exported output but no runnable Player.exe was produced (for example Create Solution). Inspect the preserved output and build log." : "Unity BuildReport result: " + summary.result + ". Inspect warnings/errors below.";
                    local.job.version++; Save(local);
                }
            }
            catch (Exception exception)
            {
                Append(local, "Exception", exception.Message, exception.ToString());
                lock (Gate)
                {
                    local.captureComplete = true;
                    local.job.state = local.job.outputPath.Length == 0 ? "Rejected" : "Failed"; local.job.phase = "Build failed";
                    local.job.message = Summary(exception.Message); local.job.durationSeconds = (DateTime.UtcNow - started).TotalSeconds;
                    local.job.totalErrors = Math.Max(1, local.job.totalErrors); local.job.version++; Save(local);
                }
            }
            finally
            {
                try { if (capture) Application.logMessageReceivedThreaded -= Capture; }
                finally
                {
                    try { lock (Gate) { activeId = null; CloseWriter(local); Save(local); } }
                    finally { if (reloadLocked) EditorApplication.UnlockReloadAssemblies(); }
                }
            }
        }

        private static void ValidatePlayerConfiguration(BuildConfigurationSnapshot snapshot)
        {
            foreach (var id in new[] { "Build.buildScriptsOnly", "ProfilePlatform.m_CreateSolution", "ProfilePlatform.m_InstallInBuildFolder" })
                if (snapshot.fields.Any(field => field.id == id && field.value == "true"))
                    throw new InvalidOperationException(id + " is incompatible with a new isolated runnable-player output. Disable it in Build settings before building.");
            var install = typeof(EditorUserBuildSettings).GetProperty("installInBuildFolder", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (install != null && (bool)install.GetValue(null)) throw new InvalidOperationException("Disable Install In Build Folder before requesting an isolated dashboard build.");
            var profile = BuildProfile.GetActiveBuildProfile();
            if (profile != null)
            {
                using (var serialized = new SerializedObject(profile))
                {
                    var architecture = serialized.FindProperty("m_PlatformBuildProfile.m_Architecture");
                    if (architecture != null && architecture.propertyType == SerializedPropertyType.Enum && architecture.enumValueIndex >= 0)
                    {
                        var label = architecture.enumNames[architecture.enumValueIndex];
                        if (label.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("Both", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (label.IndexOf("X64", StringComparison.OrdinalIgnoreCase) >= 0 && label.IndexOf("ARM64", StringComparison.OrdinalIgnoreCase) >= 0))
                            throw new InvalidOperationException("Multi-architecture profile outputs are not supported by the single Player.exe artifact contract. Select one architecture before building.");
                    }
                }
            }
        }
        private static BuildOptions LegacyOptions(string output)
        {
            // Ask Unity's fixed built-in option resolver, not UI handlers or custom
            // build scripts. It respects classic-platform Windows/PDB/profiler flags
            // without changing the saved build location or opening a file dialog.
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("UnityEditor.Build.Profile.BuildProfileModuleUtil")).FirstOrDefault(candidate => candidate != null);
            var method = type?.GetMethod("GetBuildOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null,
                new[] { typeof(BuildTarget), typeof(BuildTargetGroup), typeof(string), typeof(BuildOptions) }, null);
            if (method == null) throw new InvalidOperationException("This Unity version cannot safely resolve classic Windows build options. Activate a Windows Build Profile, then retry.");
            var options = (BuildOptions)method.Invoke(null, new object[] { EditorUserBuildSettings.activeBuildTarget, BuildTargetGroup.Standalone, output, BuildOptions.None });
            foreach (var name in new[] { "AutoRunPlayer", "ShowBuiltPlayer", "InstallInBuildFolder", "BuildScriptsOnly", "PatchPackage", "AcceptExternalModificationsToPlayer" })
                if (Enum.IsDefined(typeof(BuildOptions), name) && (options & (BuildOptions)Enum.Parse(typeof(BuildOptions), name)) != 0)
                    throw new InvalidOperationException(name + " is incompatible with an isolated local player build. Disable it or activate a compatible Windows Build Profile.");
            return options;
        }
        private static void ValidateOutputParents()
        {
            foreach (var path in new[] { Path.Combine(ProjectRoot, "Builds"), Path.Combine(ProjectRoot, "Builds", "PlaytestOps") })
            {
                if (File.Exists(path) || (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                    throw new InvalidOperationException("Build output parents must be ordinary project-local directories, not links or files.");
            }
        }

        private static void Capture(string condition, string trace, LogType type)
        {
            Local local; lock (Gate) { if (activeId == null || !Jobs.TryGetValue(activeId, out local)) return; }
            Append(local, Level(type), condition, trace);
        }
        private static string Level(LogType type) => type == LogType.Log ? "Debug" : type.ToString();
        private static void Append(Local local, string level, string message, string trace)
        {
            lock (Gate)
            {
                if (local.captureComplete) return;
                message = (message ?? "").Replace("\0", ""); trace = (trace ?? "").Replace("\0", "");
                var clipped = message.Length > 16000 || trace.Length > 16000;
                if (clipped) { local.job.logsTruncated = true; local.job.droppedLogCount++; }
                message = Clip(message, 16000); trace = Clip(trace, 16000);
                if (local.logs.Count >= MaximumLogs || local.characters + message.Length + trace.Length > MaximumCharacters)
                { local.job.logsTruncated = true; local.job.droppedLogCount++; local.job.version++; return; }
                var entry = new BuildLogMessage { sequence = local.logs.Count + 1, timestampUtc = Utc(), level = level, message = message, stackTrace = trace, truncated = clipped };
                local.logs.Add(entry); local.characters += message.Length + trace.Length; local.job.logCount = local.logs.Count;
                try { local.writer?.WriteLine(JsonUtility.ToJson(entry)); }
                catch { PersistenceLost(local); CloseWriter(local); }
                if (local.job.state == "Running") { if (level == "Warning") local.job.totalWarnings++; if (level == "Error" || level == "Exception" || level == "Assert") local.job.totalErrors++; }
                local.job.version++;
                WakeDelivery();
            }
        }

        // Runs entirely off Unity's main thread. JsonUtility's plain-data serializer
        // is thread-safe; no Editor APIs, UI calls or SessionState are used here.
        internal static async Task DeliverAsync(string origin, Func<string, CancellationToken, Task> send, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                List<BuildWireMessage> messages;
                lock (Gate)
                {
                    messages = new List<BuildWireMessage>();
                    foreach (var local in Jobs.Values.Where(item => !item.job.serverClosed && !item.job.deliveryQuarantined && item.job.origin == origin).Take(50))
                    {
                        var job = local.job;
                        if (local.flight == null && (job.ackVersion < job.version || job.ackLogSequence < local.logs.Count || job.state == "Running"))
                        {
                            var batch = new List<BuildLogMessage>(); var count = 0;
                            foreach (var log in local.logs.Skip(job.ackLogSequence).Take(64))
                            { var size = log.message.Length + log.stackTrace.Length; if (count + size > 128000) break; batch.Add(log); count += size; }
                            var allLogs = job.ackLogSequence + batch.Count >= local.logs.Count;
                            var reportingState = allLogs ? job.state : "Running";
                            job.wireSequence++;
                            local.flight = new BuildWireMessage { buildId = job.buildId, buildToken = job.buildToken, sequence = job.wireSequence, state = reportingState,
                                phase = allLogs ? job.phase : "Uploading build logs", message = job.message, outputPath = job.outputPath, executablePath = job.executablePath, manifestPath = job.manifestPath,
                                profilePath = job.profilePath, activeTarget = job.activeTarget, unityVersion = job.unityVersion,
                                durationSeconds = job.state == "Running" && DateTime.TryParse(job.requestedUtc, null, DateTimeStyles.RoundtripKind, out var requested) ? (DateTime.UtcNow - requested).TotalSeconds : job.durationSeconds,
                                totalWarnings = job.totalWarnings, totalErrors = job.totalErrors, totalBytes = job.totalBytes, logsTruncated = job.logsTruncated, droppedLogCount = job.droppedLogCount, logs = batch.ToArray() };
                            local.flightVersion = job.version;
                            Save(local);
                        }
                        if (local.flight != null) messages.Add(local.flight);
                    }
                }
                foreach (var message in messages) { ct.ThrowIfCancellationRequested(); await send(JsonUtility.ToJson(message), ct).ConfigureAwait(false); }
                if (messages.Count == 0) await DeliveryWake.WaitAsync(ct).ConfigureAwait(false);
                else await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        internal static bool Accept(string json)
        {
            BuildAck ack;
            try { ack = JsonUtility.FromJson<BuildAck>(json); } catch { return false; }
            if (ack?.type != "build.accepted") return false;
            lock (Gate)
            {
                if (!Jobs.TryGetValue(ack.buildId, out var local) || local.flight == null) return true;
                if (!string.IsNullOrEmpty(ack.error))
                {
                    // A rejected schema/path/ownership payload is not a transient
                    // transport failure. Quarantine until the next connection rather
                    // than hammering the same permanent rejection every 500 ms.
                    local.job.deliveryError = Summary(ack.error, 200);
                    local.job.message = "Build reporting rejected; native execution may continue. " + local.job.deliveryError;
                    local.job.deliveryQuarantined = true; local.flight = null; local.job.version++; Save(local); return true;
                }
                if (ack.state == "Interrupted") { local.job.serverClosed = true; local.flight = null; Save(local); Jobs.Remove(ack.buildId); return true; }
                if (ack.sequence < local.flight.sequence) return true;
                if (ack.logSequence < local.job.ackLogSequence || ack.logSequence > local.logs.Count) return true;
                local.job.ackLogSequence = ack.logSequence; local.job.ackVersion = local.flightVersion;
                if (ack.state == "Succeeded" || ack.state == "Failed" || ack.state == "Rejected" || ack.state == "Interrupted") local.job.serverClosed = true;
                local.flight = null; Save(local);
                if (local.job.serverClosed) Jobs.Remove(ack.buildId);
            }
            return true;
        }
        internal static void ResumeDelivery(string origin)
        {
            lock (Gate)
                foreach (var local in Jobs.Values.Where(item => item.job.origin == origin && item.job.deliveryQuarantined))
                { local.job.deliveryQuarantined = false; local.flight = null; local.job.version++; Save(local); }
        }
        private static Local Load(string id)
        {
            try
            {
                var path = Path.Combine(LedgerRoot, id, "request.json");
                if (!File.Exists(path) || new FileInfo(path).Length > 32768) return null;
                var job = JsonUtility.FromJson<BuildJob>(File.ReadAllText(path));
                if (job == null || job.buildId != id || job.buildToken.Length != 64) return null;
                var local = new Local { job = job };
                var logPath = Path.Combine(LedgerRoot, id, "logs.jsonl");
                if (File.Exists(logPath) && new FileInfo(logPath).Length <= 40 * 1024 * 1024)
                    foreach (var line in File.ReadLines(logPath).Take(MaximumLogs))
                    {
                        var log = JsonUtility.FromJson<BuildLogMessage>(line);
                        if (log == null || log.sequence != local.logs.Count + 1) break;
                        local.logs.Add(log); local.characters += log.message.Length + log.stackTrace.Length;
                    }
                job.logCount = local.logs.Count;
                return local;
            }
            catch { return null; }
        }
        private static bool Save(Local local)
        {
            try
            {
            var directory = Path.Combine(LedgerRoot, local.job.buildId);
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "request.json"); var temp = target + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(local.job), new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
            WakeDelivery();
            return true;
            }
            catch { PersistenceLost(local); WakeDelivery(); return false; }
        }
        private static void PersistenceLost(Local local)
        {
            if (local.persistenceFailed) return;
            local.persistenceFailed = true; local.job.logsTruncated = true; local.job.droppedLogCount++; local.job.version++;
        }
        private static void CloseWriter(Local local)
        {
            var writer = local.writer; local.writer = null;
            try { writer?.Dispose(); } catch { PersistenceLost(local); }
        }
        private static string Summary(string text, int maximum = 2000)
        {
            text = new string((text ?? "").Select(character => char.IsControl(character) ? ' ' : character).ToArray());
            return text.Length <= maximum ? text : text.Substring(0, maximum - 12) + " [truncated]";
        }
        private static void WakeDelivery() { if (DeliveryWake.CurrentCount == 0) try { DeliveryWake.Release(); } catch (SemaphoreFullException) { } }
        private static string Utc() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        private static string Clip(string text, int maximum)
        {
            if (text.Length <= maximum) return text;
            var count = maximum - 16; if (count > 0 && char.IsHighSurrogate(text[count - 1])) count--;
            return text.Substring(0, count) + "\n[truncated]";
        }
        private static bool IsStreamingIdentityArtifact(string path)
        {
            var name = Path.GetFileName(path);
            return name.Equals("playtestops-streaming.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("PlaytestOps.Runtime.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("webrtc.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase);
        }
        private static string FileHash(string path)
        {
            using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
