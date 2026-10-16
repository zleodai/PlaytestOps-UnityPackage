using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace PlaytestOps.Editor
{
    // Persist before changing project settings. Library survives an Editor restart,
    // while SessionState alone only survives script/domain reloads.
    internal static class PlayModeReloadGuard
    {
        [Serializable] private sealed class SavedSettings
        {
            public int version = 1;
            public string runId;
            public bool optionsEnabled;
            public int options;
            public bool restoreRequested;
        }

        private static SavedSettings saved;
        internal static event Action Changed;
        internal static bool Active => saved != null || Error != null;
        internal static string Error { get; private set; }
        private static string JournalPath => Path.GetFullPath("Library/PlaytestOps/play-mode-settings.json");

        internal static void Recover(string activeRunId)
        {
            try
            {
                if (!File.Exists(JournalPath)) return;
                var recovered = JsonUtility.FromJson<SavedSettings>(File.ReadAllText(JournalPath));
                if (recovered == null || recovered.version != 1 || !Guid.TryParseExact(recovered.runId, "N", out _))
                    throw new InvalidDataException("Invalid saved Play Mode settings.");
                saved = recovered;
                Subscribe();
                // A new Editor session has no active RunRecord, so restore an orphaned override.
                if (saved.restoreRequested || saved.runId != activeRunId) RequestRestore();
            }
            catch (Exception ex) { ReportRestoreError(ex); }
        }

        internal static void Begin(string runId)
        {
            if (Active) throw new InvalidOperationException("Wait for PlaytestOps to restore the previous Play Mode settings.");
            var options = EditorSettings.enterPlayModeOptions;
            if (!EditorSettings.enterPlayModeOptionsEnabled || (options & EnterPlayModeOptions.DisableDomainReload) == 0) return;
            var next = new SavedSettings { runId = runId, optionsEnabled = EditorSettings.enterPlayModeOptionsEnabled, options = (int)options };
            WriteJournal(next); // Failure here leaves the user's settings unchanged.
            saved = next;
            Subscribe();
            // Keep the enabled flag and every other option, including DisableSceneReload.
            EditorSettings.enterPlayModeOptions = options & ~EnterPlayModeOptions.DisableDomainReload;
            Changed?.Invoke();
        }

        internal static void Complete(string runId)
        {
            if (saved?.runId == runId) RequestRestore();
        }

        internal static void RestoreOnQuit()
        {
            if (saved != null) Restore();
        }

        private static void Subscribe()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            // Also covers manual stop or an aborted run without a final result callback.
            if (change == PlayModeStateChange.EnteredEditMode && saved != null) RequestRestore();
        }

        private static void RequestRestore()
        {
            saved.restoreRequested = true;
            try { WriteJournal(saved); }
            catch (Exception ex) { Error = "Could not update the settings recovery record: " + ex.Message; }
            EditorApplication.update -= RestoreWhenIdle;
            EditorApplication.update += RestoreWhenIdle;
        }

        private static void RestoreWhenIdle()
        {
            // RunFinished arrives inside Play Mode. Restore only after its exit/reload is complete.
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            Restore();
        }

        private static void Restore()
        {
            EditorApplication.update -= RestoreWhenIdle;
            if (saved == null) return;
            try
            {
                EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)saved.options;
                EditorSettings.enterPlayModeOptionsEnabled = saved.optionsEnabled;
                PersistRestoredOptions();
                File.Delete(JournalPath);
                saved = null;
                Error = null;
                EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                Changed?.Invoke();
            }
            catch (Exception ex) { ReportRestoreError(ex); }
        }

        private static void PersistRestoredOptions()
        {
            // ProjectSettings are not AssetDatabase assets: SaveAssetIfDirty does not
            // persist them. Patch only our two YAML scalars, preserving every other
            // byte (including line endings/BOM), without saving unrelated user assets.
            var path = Path.GetFullPath("ProjectSettings/EditorSettings.asset");
            var encoding = new UTF8Encoding(false, true);
            var text = encoding.GetString(File.ReadAllBytes(path));
            var optionsPattern = new Regex(@"(?m)^(  m_EnterPlayModeOptions: )[0-9]+(?=\r?$)");
            var enabledPattern = new Regex(@"(?m)^(  m_EnterPlayModeOptionsEnabled: )[01](?=\r?$)");
            if (optionsPattern.Matches(text).Count != 1 || enabledPattern.Matches(text).Count != 1)
                throw new InvalidDataException("Unsupported EditorSettings.asset format; original settings remain in the recovery record.");
            var restored = optionsPattern.Replace(text, match => match.Groups[1].Value + ((int)EditorSettings.enterPlayModeOptions).ToString(CultureInfo.InvariantCulture));
            restored = enabledPattern.Replace(restored, match => match.Groups[1].Value + (EditorSettings.enterPlayModeOptionsEnabled ? "1" : "0"));
            if (restored == text) return;
            var temporary = path + ".playtestops.tmp";
            File.WriteAllBytes(temporary, encoding.GetBytes(restored));
            File.Replace(temporary, path, null);
        }

        private static void WriteJournal(SavedSettings value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath));
            var temporary = JournalPath + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(value, true));
            if (File.Exists(JournalPath)) File.Replace(temporary, JournalPath, null);
            else File.Move(temporary, JournalPath);
        }

        private static void ReportRestoreError(Exception ex)
        {
            Error = "PlaytestOps could not restore Play Mode settings. Recovery record: " + JournalPath + ". " + ex.Message;
            Changed?.Invoke();
        }
    }
}
