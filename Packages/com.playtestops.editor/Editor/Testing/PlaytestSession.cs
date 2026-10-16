using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlaytestOps.Editor
{
    // Run callbacks are event driven. Settings cleanup uses an update hook only while restoration is pending.
    [InitializeOnLoad]
    public static class PlaytestSession
    {
        private const string RunKey = "PlaytestOps.Run";
        private const string ExternalKey = "PlaytestOps.ExternalRun";
        private static readonly Callbacks Observer = new Callbacks();
        private static TestRunnerApi api;
        private static bool discovering;
        private static double nextLogFlush;
        private static List<TestCase> catalog = new List<TestCase>();
        public static event Action Changed;
        public static event Action CatalogChanged;
        public static event Action<RunRecord> RunChanged;
        public static event Action<string, string> RemoteRejected;
        public static IReadOnlyList<TestCase> Catalog => catalog;
        public static bool IsDiscovering => discovering;
        public static bool HasDiscovered { get; private set; }
        public static RunRecord LastRun { get; private set; }
        private static string operationError;
        public static string Error { get => PlayModeReloadGuard.Error ?? operationError; private set => operationError = value; }
        public static bool Busy => PlayModeReloadGuard.Active || discovering || LastRun?.IsActive == true || SessionState.GetBool(ExternalKey, false);
        public static string ResultsDirectory => Path.GetFullPath("Library/PlaytestOps/Runs");

        static PlaytestSession()
        {
            var json = SessionState.GetString(RunKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { LastRun = JsonUtility.FromJson<RunRecord>(json); }
                catch (Exception ex) { Error = "Could not restore run state: " + ex.Message; }
            }
            PlayModeReloadGuard.Changed += Notify;
            PlayModeReloadGuard.Recover(LastRun?.IsActive == true ? LastRun.id : null);
            TestRunnerApi.RegisterTestCallback(Observer);
            if (LastRun?.IsActive == true) StartLogCapture();
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += OnQuit;
        }

        private static TestRunnerApi Api => api != null ? api : api = ScriptableObject.CreateInstance<TestRunnerApi>();
        private static void BeforeReload()
        {
            StopLogCapture();
            TestRunnerApi.UnregisterTestCallback(Observer);
            if (api != null) UnityEngine.Object.DestroyImmediate(api);
        }
        private static void OnQuit()
        {
            if (LastRun?.IsActive == true) FinishInterrupted("Editor closed before a final result was received.");
            PlayModeReloadGuard.RestoreOnQuit();
        }

        public static void Discover()
        {
            if (!CanStart()) return;
            Error = null;
            discovering = true;
            Notify();
            try
            {
                Api.RetrieveTestList(TestMode.EditMode, edit => Guard(() =>
                {
                    var next = TestCatalog.Flatten(edit, TestMode.EditMode);
                    Api.RetrieveTestList(TestMode.PlayMode, play => Guard(() =>
                    {
                        next.AddRange(TestCatalog.Flatten(play, TestMode.PlayMode));
                        catalog = next;
                        HasDiscovered = true;
                        discovering = false;
                        Notify();
                        CatalogChanged?.Invoke();
                    }));
                }));
            }
            catch (Exception ex) { FailOperation(ex); }
        }

        public static void RunRemote(string runId, string mode, string uniqueName)
        {
            if (!Guid.TryParseExact(runId, "N", out _) || (mode != "EditMode" && mode != "PlayMode") || string.IsNullOrEmpty(uniqueName))
                return;
            var seen = SessionState.GetString("PlaytestOps.RemoteRequests", "");
            if (seen.Split('|').Contains(runId))
            {
                if (LastRun?.dashboardRunId == runId) RunChanged?.Invoke(LastRun);
                return;
            }
            // Remember receipt before any asynchronous discovery or execution. Never execute a replay.
            SessionState.SetString("PlaytestOps.RemoteRequests", string.Join("|", (seen + "|" + runId).Split('|').Where(x => x.Length > 0).Reverse().Take(100).Reverse()));
            Run(mode + ":" + uniqueName, runId);
        }

        public static void Run(string key, string dashboardRunId = null)
        {
            if (!CanStart()) { if (dashboardRunId != null) RemoteRejected?.Invoke(dashboardRunId, Error); return; }
            Error = null;
            try
            {
                var selected = TestCatalog.Resolve(catalog, key);
                var mode = (TestMode)Enum.Parse(typeof(TestMode), selected.mode);
                // Persist ownership before retrieval/Execute; PlayMode can reload this assembly.
                LastRun = new RunRecord {
                    id = Guid.NewGuid().ToString("N"), dashboardRunId = dashboardRunId, test = selected,
                    lifecycle = "Starting", startedUtc = DateTime.UtcNow.ToString("O")
                };
                StartLogCapture();
                Save();
                discovering = true;
                Notify();
                Api.RetrieveTestList(mode, root => Guard(() =>
                {
                    selected = TestCatalog.Resolve(TestCatalog.Flatten(root, mode), key);
                    discovering = false;
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || SessionState.GetBool(ExternalKey, false))
                        throw new InvalidOperationException("The Editor became busy; wait and try again.");
                    for (var i = 0; i < SceneManager.sceneCount; i++)
                        if (SceneManager.GetSceneAt(i).isDirty)
                            throw new InvalidOperationException("Save your modified scenes before starting a test. PlaytestOps will not save or discard them for you.");
                    LastRun.test = selected;
                    // Persist intent before Execute, which can call back synchronously.
                    Save();
                    try
                    {
                        if (mode == TestMode.PlayMode) PlayModeReloadGuard.Begin(LastRun.id);
                        var job = Api.Execute(new ExecutionSettings(new Filter {
                            testMode = mode, assemblyNames = new[] { selected.assembly }, testNames = new[] { selected.fullName }
                        }));
                        LastRun.unityJobId = job;
                        Save();
                    }
                    catch (Exception ex) { FinishInterrupted("Unity could not start the run: " + ex.Message); }
                    Notify();
                }));
            }
            catch (Exception ex)
            {
                if (dashboardRunId != null && LastRun?.dashboardRunId != dashboardRunId) RemoteRejected?.Invoke(dashboardRunId, ex.Message);
                FailOperation(ex);
            }
        }

        private static bool CanStart()
        {
            if (Busy || WindowsBuildExecution.Busy || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Error = "Wait for the current run, import, compilation, or Play Mode to finish.";
                Notify();
                return false;
            }
            return true;
        }

        private static void Guard(Action action)
        {
            try { action(); } catch (Exception ex) { FailOperation(ex); }
        }
        private static void FailOperation(Exception ex)
        {
            discovering = false;
            Error = ex.Message;
            if (LastRun?.IsActive == true) FinishInterrupted(ex.Message);
            Notify();
        }
        private static void Notify() => Changed?.Invoke();
        private static void StartLogCapture()
        {
            RunLogCapture.Begin(LastRun);
            nextLogFlush = EditorApplication.timeSinceStartup + 0.5;
            EditorApplication.update -= FlushLogs;
            EditorApplication.update += FlushLogs;
        }
        private static void FlushLogs()
        {
            if (LastRun?.IsActive != true) { StopLogCapture(); return; }
            if (EditorApplication.timeSinceStartup < nextLogFlush) return;
            nextLogFlush = EditorApplication.timeSinceStartup + 0.5;
            if (RunLogCapture.Drain(LastRun)) { Save(); Notify(); }
        }
        private static void StopLogCapture()
        {
            EditorApplication.update -= FlushLogs;
            if (LastRun != null && RunLogCapture.Drain(LastRun, true)) Save();
        }
        private static void Save()
        {
            LastRun.revision++;
            var json = JsonUtility.ToJson(LastRun, true);
            SessionState.SetString(RunKey, json);
            RunChanged?.Invoke(LastRun);
            try
            {
                Directory.CreateDirectory(ResultsDirectory);
                var path = Path.Combine(ResultsDirectory, LastRun.id + ".json");
                var temp = path + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception ex) { Error = "Run state is available for this Editor session, but writing its report failed: " + ex.Message; }
        }

        private static void FinishInterrupted(string message)
        {
            StopLogCapture();
            PlayModeReloadGuard.Complete(LastRun.id);
            LastRun.lifecycle = "Interrupted";
            LastRun.message = message;
            LastRun.finishedUtc = DateTime.UtcNow.ToString("O");
            Save();
            Notify();
        }

        private static bool Matches(ITestAdaptor test)
        {
            if (LastRun?.test == null || test.IsSuite) return false;
            return test.UniqueName == LastRun.test.uniqueName && test.FullName == LastRun.test.fullName;
        }

        private static bool ContainsSelectedResult(ITestResultAdaptor result)
        {
            if (Matches(result.Test)) return true;
            return result.HasChildren && result.Children.Any(ContainsSelectedResult);
        }

        private sealed class Callbacks : IErrorCallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                if (LastRun?.IsActive == true)
                {
                    var cases = TestCatalog.Flatten(testsToRun, (TestMode)Enum.Parse(typeof(TestMode), LastRun.test.mode));
                    // After a PlayMode reload Unity may provide an unfiltered discovery tree here.
                    // Verify executed leaves in TestStarted rather than mistaking that tree for execution.
                    if (cases.Count(x => x.uniqueName == LastRun.test.uniqueName) == 1)
                    {
                        LastRun.rootId = testsToRun.Id;
                        LastRun.lifecycle = "Running";
                        Save();
                        Notify();
                        return;
                    }
                    FinishInterrupted(cases.Count == 0
                        ? "Unity started an empty test run; no selected test was available for execution."
                        : "A different test run started. This run will not claim its results.");
                }
                SessionState.SetBool(ExternalKey, true);
                Notify();
            }

            public void TestStarted(ITestAdaptor test)
            {
                if (LastRun?.IsActive == true && !test.IsSuite && !Matches(test))
                {
                    FinishInterrupted("Unity started a different test; this run will not claim its results.");
                    SessionState.SetBool(ExternalKey, true);
                }
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (LastRun?.IsActive != true || !Matches(result.Test)) return;
                RunLogCapture.Drain(LastRun);
                LastRun.leafReceived = true;
                LastRun.outcome = result.ResultState;
                LastRun.durationSeconds = result.Duration;
                LastRun.message = result.Message;
                LastRun.stackTrace = result.StackTrace;
                LastRun.output = result.Output;
                Save();
                Notify();
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                SessionState.SetBool(ExternalKey, false);
                // Unity can rebuild the root around a PlayMode run; transient root IDs are not durable.
                if (LastRun?.IsActive == true &&
                    (result.Test.Id == LastRun.rootId || ContainsSelectedResult(result)))
                {
                    StopLogCapture();
                    LastRun.lifecycle = LastRun.leafReceived ? "Completed" : "Interrupted";
                    if (!LastRun.leafReceived) LastRun.message = "Unity finished without a result for the selected test.";
                    LastRun.finishedUtc = DateTime.UtcNow.ToString("O");
                    PlayModeReloadGuard.Complete(LastRun.id);
                    Save();
                    try { TestRunnerApi.SaveResultToFile(result, Path.Combine(ResultsDirectory, LastRun.id + ".xml")); }
                    catch (Exception ex) { Error = "The result was recorded, but XML export failed: " + ex.Message; }
                }
                Notify();
            }

            public void OnError(string message)
            {
                SessionState.SetBool(ExternalKey, false);
                if (LastRun?.IsActive == true) FinishInterrupted("Test Runner error: " + message);
                else { Error = message; Notify(); }
            }
        }
    }
}
