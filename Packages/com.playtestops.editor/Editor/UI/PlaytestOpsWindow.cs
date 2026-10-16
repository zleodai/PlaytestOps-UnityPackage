using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PlaytestOps.Editor
{
    public sealed class PlaytestOpsWindow : EditorWindow
    {
        private Label summary;
        private TextField address;
        private TextField pairingCode;
        private Label connection;
        private Button connect;
        private Button disconnect;
        private Label result;
        private HelpBox error;
        private ListView tests;
        private Button refresh;
        private Button run;
        [SerializeField] private string selectedKey;

        [MenuItem("Tools/PlaytestOps/Open PlaytestOps")]
        public static void Open()
        {
            var window = GetWindow<PlaytestOpsWindow>();
            window.titleContent = new GUIContent("PlaytestOps");
            window.minSize = new Vector2(410, 420);
        }

        private void OnEnable() { PlaytestSession.Changed += Render; EditorConnection.Changed += RenderConnection; }
        private void OnDisable() { PlaytestSession.Changed -= Render; EditorConnection.Changed -= RenderConnection; }

        private void RenderConnection()
        {
            if (connection == null) return;
            connection.text = EditorConnection.Status + (EditorConnection.LastSync == null ? "" : $" · {EditorConnection.SyncedCount} tests synced at {EditorConnection.LastSync}") +
                (string.IsNullOrEmpty(EditorConnection.Error) ? "" : "\n" + EditorConnection.Error);
            connect.SetEnabled(!EditorConnection.Active);
            disconnect.SetEnabled(EditorConnection.Active);
            address.SetEnabled(!EditorConnection.Active);
            pairingCode.SetEnabled(!EditorConnection.Active);
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.paddingLeft = root.style.paddingRight = 12;
            root.style.paddingTop = root.style.paddingBottom = 12;
            root.Add(new Label("PlaytestOps") { style = { fontSize = 22, unityFontStyleAndWeight = FontStyle.Bold } });
            root.Add(new Label($"{Application.productName} · Unity {Application.unityVersion}"));
            address = new TextField("Dashboard address") { value = EditorConnection.DefaultAddress };
            pairingCode = new TextField("Pairing code");
            root.Add(address); root.Add(pairingCode);
            connect = new Button(() => { EditorConnection.Connect(address.value, pairingCode.value); pairingCode.value = ""; }) { text = "Connect to PlaytestOps" };
            disconnect = new Button(EditorConnection.Disconnect) { text = "Disconnect" };
            root.Add(connect); root.Add(disconnect);
            connection = new Label { style = { whiteSpace = WhiteSpace.Normal } };
            root.Add(connection);
            root.Add(new HelpBox("Get a pairing code from the dashboard's Unity Editors page. Connecting and refreshing discovery synchronize tests. Use Run test in the dashboard to execute a selected test and return its result.", HelpBoxMessageType.Info));
            RenderConnection();
            AddSourceFolderSettings(root);
            root.Add(new HelpBox("Let any existing Unity Test Runner run finish before using this preview. Play Mode tests can reload the scripting domain.", HelpBoxMessageType.None));
            summary = new Label { style = { whiteSpace = WhiteSpace.Normal } };
            root.Add(summary);
            refresh = new Button(PlaytestSession.Discover) { text = "Discover / refresh tests" };
            root.Add(refresh);
            tests = new ListView {
                selectionType = SelectionType.Single, fixedItemHeight = 48,
                makeItem = () => new Label { style = { whiteSpace = WhiteSpace.Normal, paddingTop = 4 } },
                bindItem = (element, index) => {
                    var item = (TestCase)tests.itemsSource[index];
                    ((Label)element).text = $"[{item.mode}] {item.name}\n{item.assembly} · {item.runState}";
                    element.tooltip = item.fullName + "\n" + item.description + "\n" + item.skipReason;
                }
            };
            tests.style.flexGrow = 1;
            tests.style.minHeight = 120;
            tests.selectionChanged += selection => {
                selectedKey = selection.OfType<TestCase>().FirstOrDefault()?.Key;
                UpdateButtons();
            };
            root.Add(tests);
            run = new Button(() => PlaytestSession.Run(selectedKey)) { text = "Run selected test" };
            root.Add(run);
            error = new HelpBox("", HelpBoxMessageType.Error);
            root.Add(error);
            var resultScroll = new ScrollView { style = { maxHeight = 200 } };
            result = new Label { style = { whiteSpace = WhiteSpace.Normal } };
            resultScroll.Add(result);
            root.Add(resultScroll);
            root.Add(new Button(() => {
                System.IO.Directory.CreateDirectory(PlaytestSession.ResultsDirectory);
                EditorUtility.RevealInFinder(PlaytestSession.ResultsDirectory);
            }) { text = "Open saved reports" });
            Render();
        }

        private static void AddSourceFolderSettings(VisualElement root)
        {
            var section = new Foldout { text = "Read-only source folders", value = false };
            section.Add(new HelpBox("The dashboard can browse C# files only in these explicit author folders. Defaults are Assets/Scripts and Assets/Tests. Package, Library, plugin, sample, tutorial, template, example, ThirdParty, Third-Party, Vendor, and External folders are always excluded. Browsing never writes source files. An empty list disables source browsing.", HelpBoxMessageType.Info));
            var folders = new TextField("One Assets subfolder per line") { multiline = true };
            folders.style.minHeight = 64;
            var feedback = new HelpBox("Only Save source folders changes ProjectSettings/PlaytestOpsSource.json.", HelpBoxMessageType.None);
            try { folders.value = string.Join("\n", SourceSettings.Load(SourceSettings.ProjectRoot)); }
            catch (System.Exception) { feedback.text = "Source-folder settings could not be loaded. Browsing is blocked until you save a valid explicit folder list."; }
            section.Add(folders);
            section.Add(new Button(() => {
                try
                {
                    var roots = folders.value.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
                    SourceSettings.Save(SourceSettings.ProjectRoot, roots);
                    feedback.text = roots.Length == 0 ? "Saved. Source browsing is disabled." : "Saved. The next source request uses these author folders.";
                }
                catch (System.Exception exception)
                {
                    feedback.text = exception is System.InvalidOperationException ? exception.Message : "Could not save source-folder settings. Check project folder permissions.";
                }
            }) { text = "Save source folders" });
            section.Add(new Button(() => {
                folders.value = string.Join("\n", SourceReader.DefaultRoots);
                feedback.text = "Default folders selected. Click Save source folders to apply them.";
            }) { text = "Use default folders (unsaved)" });
            section.Add(feedback);
            root.Add(section);
        }

        private void UpdateButtons()
        {
            if (run == null) return;
            refresh.SetEnabled(!PlaytestSession.Busy);
            run.SetEnabled(!PlaytestSession.Busy && PlaytestSession.Catalog.Any(x => x.Key == selectedKey && x.IsRunnable));
        }

        private void Render()
        {
            if (tests == null) return;
            var items = PlaytestSession.Catalog.ToList();
            var key = selectedKey;
            tests.itemsSource = items;
            tests.Rebuild();
            var index = items.FindIndex(x => x.Key == key);
            tests.SetSelectionWithoutNotify(index < 0 ? new int[0] : new[] { index });
            selectedKey = index < 0 ? null : key;
            summary.text = PlaytestSession.IsDiscovering ? "Discovering tests…" :
                !PlaytestSession.HasDiscovered ? "Tests have not been loaded yet. Click Discover to query Unity Test Framework." :
                items.Count == 0 ? "No EditMode or PlayMode tests found. Import Bridge Smoke Tests from Package Manager > PlaytestOps Editor > Samples, then refresh." :
                $"EditMode: {items.Count(x => x.mode == "EditMode")} · PlayMode: {items.Count(x => x.mode == "PlayMode")}";
            error.text = PlaytestSession.Error ?? "";
            error.style.display = string.IsNullOrEmpty(PlaytestSession.Error) ? DisplayStyle.None : DisplayStyle.Flex;
            var last = PlaytestSession.LastRun;
            result.text = last == null ? "No PlaytestOps run in this Editor session." :
                $"{last.test.fullName}\n{last.lifecycle} · {last.outcome ?? "Awaiting result"} · {last.durationSeconds:F3}s\n{last.message}\n{last.stackTrace}\n{last.output}";
            UpdateButtons();
        }
    }
}
