using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PlaytestOps.Editor
{
    [Serializable] public sealed class BuildConfigurationField
    {
        public string id = "", category = "", label = "", kind = "", value = "", note = "";
        public string[] choices = new string[0];
        public bool editable;
    }
    [Serializable] public sealed class BuildConfigurationSnapshot
    {
        public int protocolVersion = 1;
        public bool buildExecutionSupported = true;
        public string capturedAtUtc = "", revision = new string('0', 64), state = "available", errorCode = "";
        public string unityVersion = "", activeTarget = "", profileName = "", profilePath = "", scope = "";
        public BuildConfigurationField[] fields = new BuildConfigurationField[0];
        public string[] limitations = new string[0];
    }

    // The browser never supplies a type, property path or method name to invoke.
    // Every edit resolves against this freshly generated, typed local catalog.
    public static class WindowsBuildConfiguration
    {
        private const int MaximumValueLength = 16384;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private sealed class Binding
        {
            public BuildConfigurationField field;
            public Type type;
            public Func<object> get;
            public Action<object> set;
            public Func<string, object> decode;
        }
        [Serializable] private sealed class Strings { public string[] items = new string[0]; }
        [Serializable] private sealed class Quoted { public string text; }
        [Serializable] private sealed class Fingerprint
        {
            public string target, profilePath, profileIdentity, scope;
            public BuildConfigurationField[] fields;
        }
        private static readonly HashSet<string> ExcludedPlayerProperties = new HashSet<string>(new[]
        {
            "accelerometerFrequency", "adjustIOSFPSUsingThermalState", "allowedAutorotateToLandscapeLeft", "allowedAutorotateToLandscapeRight",
            "allowedAutorotateToPortrait", "allowedAutorotateToPortraitUpsideDown", "audioSpatialExperience", "defaultInterfaceOrientation",
            "defaultWebScreenHeight", "defaultWebScreenWidth", "enableMetalAPIValidation", "iPhoneBundleIdentifier", "keyaliasPass", "keystorePass",
            "locationUsageDescription", "logObjCUncaughtExceptions", "macFullscreenMode", "macRetinaSupport", "openGLRequireES31", "openGLRequireES31AEP",
            "openGLRequireES32", "preserveFramebufferAlpha", "protectGraphicsMemory", "statusBarHidden", "tvOSBundleVersion", "useAnimatedAutorotation",
            "useMacAppStoreValidation", "visionOSBundleVersion", "webGPUDeviceFilterListAsset", "xcodeProjectType", "advancedLicense", "cloudProjectId"
        }, StringComparer.Ordinal);
        private static readonly string[] BuildProperties =
        {
            "selectedStandaloneTarget", "standaloneBuildSubtarget", "development", "allowDebugging", "connectProfiler", "buildWithDeepProfilingSupport",
            "waitForManagedDebugger", "managedDebuggerFixedPort", "waitForPlayerConnection", "buildScriptsOnly", "buildWithCodeCoverage", "il2CppCodeGeneration",
            "explicitNullChecks", "explicitDivideByZeroChecks", "explicitArrayBoundsChecks", "forceOptimizeScriptCompilation", "overrideMaxTextureSize",
            "overrideTextureCompression", "symlinkSources"
        };
        private static readonly HashSet<string> ProfileLeaves = new HashSet<string>(new[]
        {
            "m_Development", "m_ConnectProfiler", "m_BuildWithDeepProfilingSupport", "m_BuildWithCodeCoverage", "m_AllowDebugging",
            "m_WaitForManagedDebugger", "m_ManagedDebuggerFixedPort", "m_ExplicitNullChecks", "m_ExplicitDivideByZeroChecks", "m_ExplicitArrayBoundsChecks",
            "m_CompressionType", "m_InstallInBuildFolder", "m_InsightsSettingsContainer.m_BuildProfileEngineDiagnosticsState", "m_AdaptivePerformanceEnabled",
            "m_Architecture", "m_CreateSolution", "m_CopyPDBFiles"
        }, StringComparer.Ordinal);
        public static bool Busy => PlaytestSession.Busy || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer || WindowsBuildExecution.Busy;

        public static BuildConfigurationSnapshot Read() => Capture(out _);
        internal static BuildConfigurationSnapshot ReadForBuild() => Capture(out _, allowBusy: true);

        public static BuildConfigurationSnapshot Apply(string revision, string settingId, string value)
        {
            var before = Capture(out var bindings);
            if (before.state != "available") return before;
            if (before.revision != revision) { before.errorCode = "stale"; return before; }
            var binding = bindings.FirstOrDefault(item => item.field.id == settingId);
            if (binding == null || !binding.field.editable || binding.set == null) { before.errorCode = "invalidSetting"; return before; }
            object parsed;
            try
            {
                if (value == null || value.Length > MaximumValueLength || value.Contains("\0")) throw new ArgumentException();
                parsed = binding.decode != null ? binding.decode(value) : Decode(value, binding.type);
                Validate(binding, parsed);
            }
            catch { before.errorCode = "invalidValue"; return before; }
            // One setting at a time; unlike bulk settings replacement, unrelated local
            // settings and hidden platform values are never written back from a form.
            var previous = binding.get();
            try
            {
                EditorApplication.LockReloadAssemblies();
                try
                {
                    binding.set(parsed);
                    Persist();
                }
                finally { EditorApplication.UnlockReloadAssemblies(); }
                var result = Capture(out _, allowBusy: true);
                result.errorCode = "saved";
                return result;
            }
            catch
            {
                try { binding.set(previous); Persist(); } catch { }
                var result = Capture(out _, allowBusy: true);
                result.errorCode = "applyFailed";
                return result;
            }
        }

        private static BuildConfigurationSnapshot Capture(out List<Binding> bindings, bool allowBusy = false)
        {
            bindings = new List<Binding>();
            var snapshot = new BuildConfigurationSnapshot { capturedAtUtc = DateTime.UtcNow.ToString("o", Invariant), unityVersion = Application.unityVersion,
                activeTarget = EditorUserBuildSettings.activeBuildTarget.ToString() };
            if (Application.platform != RuntimePlatform.WindowsEditor) { snapshot.state = "failed"; snapshot.errorCode = "unsupportedPlatform"; return snapshot; }
            if (Busy && !allowBusy) { snapshot.state = "busy"; snapshot.errorCode = "busy"; return snapshot; }
            var notes = new List<string>
            {
                "PlayerSettings APIs target the active Windows profile's Player Settings override when present, otherwise global project settings. Shared settings also affect other platforms that inherit them.",
                "Each Save writes one setting to Unity, not a dashboard-only preset. Script, graphics, input and backend changes may trigger import, compilation, reload or require an Editor restart. No restart is automatic.",
                "Assets use project-relative Assets/ or Packages/ paths, optionally #localFileId for sub-assets. Empty means None. No upload, arbitrary filesystem path or URL is accepted.",
                "Lists and compound values use JSON. Lists have the form {\"items\":[\"value\",\"value\"]}; order is preserved. Scene entries start + (enabled) or - (disabled). Vector/color components are ordered x,y[,z,w] or r,g,b,a.",
                "Only the active Windows platform/profile is edited. Creating, deleting or activating profiles, switching platform, cloud-build/deployment credentials, UWP/other-platform settings and build/run actions are outside this page.",
                "Project-wide Graphics/Quality/Pipeline asset inspectors, package-defined custom Build Profile components, and platform SDK/toolchain installers are not Player/Windows Build Settings and are not rewritten here."
            };
            var profile = BuildProfile.GetActiveBuildProfile();
            snapshot.profileName = profile == null ? "Windows platform (global settings)" : profile.name;
            snapshot.profilePath = profile == null ? "" : AssetDatabase.GetAssetPath(profile);
            snapshot.scope = profile == null ? "Global Windows platform + shared Player Settings" : "Active Build Profile + effective Player Settings";
            var windows = IsWindows(EditorUserBuildSettings.activeBuildTarget);
            var routingMatches = PlayerRoutingMatches(profile);
            if (!routingMatches)
            {
                snapshot.scope += " (Player Settings routing mismatch; read-only)";
                notes.Add("Unity's active Player Settings API object does not match the active profile component. All edits are disabled to prevent changing global settings accidentally. Restore the profile context in Unity, then refresh.");
            }
            if (!windows) notes.Add("The active target is not Windows. Values are read-only to avoid modifying another platform/profile. Activate a Windows profile in Unity, then refresh.");
            foreach (var name in BuildProperties)
            {
                var property = typeof(EditorUserBuildSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                if (property == null || Obsolete(property)) { notes.Add("Unavailable in this Unity version: Build." + name); continue; }
                AddProperty(bindings, property, null, "Build", "Windows build options", notes);
            }
            AddCompression(bindings, notes);
            AddPlayerProperties(bindings, typeof(PlayerSettings), "Player", "Player Settings", notes);
            AddPlayerProperties(bindings, typeof(PlayerSettings.SplashScreen), "Splash", "Splash screen", notes);
            var standalone = typeof(PlayerSettings).GetNestedType("Standalone", BindingFlags.Public);
            if (standalone != null) AddPlayerProperties(bindings, standalone, "Standalone", "Windows standalone", notes);
            AddPlayerMethods(bindings, notes);
            AddScenesAndProfile(bindings, profile, notes);
            AddInputHandling(bindings, notes);
            AddAdvancedPlayerBindings(bindings, notes);
            AddCoverageNotes(notes);
            foreach (var item in bindings)
            {
                item.field.editable &= windows && routingMatches;
                if (item.field.id == "Build.selectedStandaloneTarget") item.field.choices = item.field.choices.Where(IsWindowsName).ToArray();
            }
            snapshot.fields = bindings.Select(item => item.field).OrderBy(field => field.category, StringComparer.Ordinal).ThenBy(field => field.id, StringComparer.Ordinal).ToArray();
            snapshot.limitations = notes.Distinct().Take(128).ToArray();
            snapshot.revision = Hash(JsonUtility.ToJson(new Fingerprint { target = snapshot.activeTarget, profilePath = snapshot.profilePath,
                profileIdentity = profile == null ? "global" : GlobalObjectId.GetGlobalObjectIdSlow(profile).ToString(), scope = snapshot.scope, fields = snapshot.fields }));
            if (snapshot.fields.Length > 512 || snapshot.fields.Any(field => field.value.Length > MaximumValueLength) || Encoding.UTF8.GetByteCount(JsonUtility.ToJson(snapshot)) > 1000000)
            { snapshot.state = "failed"; snapshot.errorCode = "outputLimit"; snapshot.fields = new BuildConfigurationField[0]; bindings.Clear(); }
            return snapshot;
        }

        private static bool PlayerRoutingMatches(BuildProfile profile)
        {
            if (profile == null) return true;
            try
            {
                var method = typeof(PlayerSettings).GetMethod("InternalGetPlayerSettingsObject", BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null) return false;
                return (Object)method.Invoke(null, null) == profile.GetComponent<PlayerSettings>();
            }
            catch { return false; }
        }
        private static void Persist()
        {
            var profile = BuildProfile.GetActiveBuildProfile();
            if (profile != null)
            {
                // Unity 6 versions using YAML-backed Player Settings overrides need
                // this fixed serialization hook before saving the parent asset.
                var serialize = typeof(BuildProfile).GetMethod("SerializePlayerSettings", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                serialize?.Invoke(profile, null);
                EditorUtility.SetDirty(profile);
            }
            AssetDatabase.SaveAssets();
        }

        private static void AddPlayerProperties(List<Binding> bindings, Type owner, string prefix, string category, List<string> notes)
        {
            foreach (var property in owner.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(item => item.Name))
            {
                if (Obsolete(property) || (owner == typeof(PlayerSettings) && ExcludedPlayerProperty(property.Name))) continue;
                AddProperty(bindings, property, null, prefix, category, notes);
            }
        }
        private static void AddCoverageNotes(List<string> notes)
        {
            var excluded = typeof(PlayerSettings).GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(property => property.Name).Where(ExcludedPlayerProperty).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            for (var i = 0; i < excluded.Length; i += 12) notes.Add("Excluded non-Windows/platform-only/credential Player APIs: " + string.Join(", ", excluded.Skip(i).Take(12)));
            var obsolete = typeof(PlayerSettings).GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(Obsolete).Select(item => item.Name).OrderBy(name => name).ToArray();
            for (var i = 0; i < obsolete.Length; i += 12) notes.Add("Obsolete Player API aliases excluded (prefer supported replacements): " + string.Join(", ", obsolete.Skip(i).Take(12)));
        }
        private static bool ExcludedPlayerProperty(string name) => ExcludedPlayerProperties.Contains(name) ||
            new[] { "xbox", "ps3", "ps4", "ps5", "android", "iPhone", "iOS", "tvOS", "visionOS", "webGL", "webGPU", "switch", "wsa" }.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        private static void AddProperty(List<Binding> bindings, PropertyInfo property, object target, string prefix, string category, List<string> notes)
        {
            if (property.GetIndexParameters().Length != 0 || property.GetGetMethod() == null) return;
            var id = prefix + "." + property.Name;
            if (!Supported(property.PropertyType)) { notes.Add("Unsupported API value type: " + id + " (" + property.PropertyType.Name + ")"); return; }
            Add(bindings, id, category, property.Name, property.PropertyType, () => property.GetValue(target, null),
                property.GetSetMethod() == null ? (Action<object>)null : value => property.SetValue(target, value, null), notes,
                prefix == "Build" ? "Editor-local build option; active profile options are also shown below when present." : "Effective Player Settings; shared unless overridden by the active profile.");
        }

        private static void AddPlayerMethods(List<Binding> bindings, List<string> notes)
        {
            // Fixed public API surface. Only these exact Get/Set pairs can be invoked.
            var target = IsWindows(EditorUserBuildSettings.activeBuildTarget) ? EditorUserBuildSettings.activeBuildTarget : BuildTarget.StandaloneWindows64;
            var named = EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Server ? NamedBuildTarget.Server : NamedBuildTarget.Standalone;
            var methods = new[] { "AdditionalCompilerArguments", "AdditionalIl2CppArgs", "ApiCompatibilityLevel", "ApplicationIdentifier", "Architecture", "CaptureStartupLogs", "ColorGamuts",
                "DefaultShaderChunkCount", "DefaultShaderChunkSizeInMB", "DynamicBatchingForPlatform", "GraphicsAPIs", "Il2CppCodeGeneration", "Il2CppCompilerConfiguration",
                "Il2CppLTOMode", "Il2CppStacktraceInformation", "IncrementalIl2CppBuild", "ManagedCodeVariant", "ManagedStrippingLevel", "NormalMapEncoding",
                "OverrideShaderChunkSettingsForPlatform", "PreloadedAssets", "ScriptingBackend", "ScriptingDefineSymbols", "ShaderChunkCountForPlatform",
                "ShaderChunkSizeInMBForPlatform", "ShaderPrecisionModel", "StaticBatchingForPlatform", "UseDefaultGraphicsAPIs", "VirtualTexturingSupportEnabled",
                "LightmapEncodingQualityForPlatform", "HDRCubemapEncodingQualityForPlatform", "LightmapStreamingEnabledForPlatformGroup", "LightmapStreamingPriorityForPlatformGroup",
                "LoadStoreDebugModeEnabledForPlatformGroup", "LoadStoreDebugModeEditorOnlyForPlatformGroup", "EditorAssembliesCompatibilityLevel" };
            foreach (var suffix in methods)
            {
                var getters = typeof(PlayerSettings).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Where(method => method.Name == "Get" + suffix && !Obsolete(method) && Supported(method.ReturnType))
                    .Where(method => method.GetParameters().Length == 0 || (method.GetParameters().Length == 1 && ContextType(method.GetParameters()[0].ParameterType)))
                    .OrderBy(method => method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(NamedBuildTarget) ? 0 : 1);
                var getter = getters.FirstOrDefault();
                if (getter == null) { notes.Add("Unavailable API: Player.Get" + suffix); continue; }
                var parameters = getter.GetParameters();
                var arguments = parameters.Select(parameter => Context(parameter.ParameterType, target, named)).ToArray();
                var setter = typeof(PlayerSettings).GetMethod("Set" + suffix, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                    parameters.Select(parameter => parameter.ParameterType).Concat(new[] { getter.ReturnType }).ToArray(), null);
                if (setter != null && Obsolete(setter)) setter = null;
                var capturedSetter = setter;
                Add(bindings, "Target." + suffix, "Windows target Player Settings", suffix, getter.ReturnType, () => getter.Invoke(null, arguments),
                    capturedSetter == null ? (Action<object>)null : value => capturedSetter.Invoke(null, arguments.Concat(new[] { value }).ToArray()), notes,
                    (parameters.Length == 0 ? "Shared Player Settings." : "Windows " + named.TargetName + " / " + target + "; desktop-family settings may also affect Linux and macOS.") + (getter.IsPublic ? "" : " Version-guarded Unity internal API adapter."));
            }
            foreach (LogType type in Enum.GetValues(typeof(LogType)))
            {
                var logType = type;
                Add(bindings, "Logging." + type, "Stack traces", type + " stack trace", typeof(StackTraceLogType), () => PlayerSettings.GetStackTraceLogType(logType), value => PlayerSettings.SetStackTraceLogType(logType, (StackTraceLogType)value), notes, "Shared Player Settings.");
            }
            AddLegacyDynamicBatching(bindings, target, notes);
            AddIcons(bindings, named, "Windows", notes);
            AddIcons(bindings, NamedBuildTarget.Unknown, "Default", notes);
        }
        private static void AddLegacyDynamicBatching(List<Binding> bindings, BuildTarget target, List<string> notes)
        {
            if (bindings.Any(item => item.field.id == "Target.DynamicBatchingForPlatform")) return;
            var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            var getter = typeof(PlayerSettings).GetMethod("GetBatchingForPlatform", flags, null, new[] { typeof(BuildTarget), typeof(int).MakeByRefType(), typeof(int).MakeByRefType() }, null);
            var setter = typeof(PlayerSettings).GetMethod("SetBatchingForPlatform", flags, null, new[] { typeof(BuildTarget), typeof(int), typeof(int) }, null);
            if (getter == null || setter == null) return;
            Func<object[]> read = () => { var args = new object[] { target, 0, 0 }; getter.Invoke(null, args); return args; };
            Add(bindings, "Target.DynamicBatchingForPlatform", "Windows target Player Settings", "Dynamic batching (legacy)", typeof(bool),
                () => (int)read()[2] != 0, value => { var current = read(); setter.Invoke(null, new[] { (object)target, current[1], (bool)value ? 1 : 0 }); }, notes,
                "Legacy stored dynamic-batching flag; preserves static batching. Modern Unity/SRP pipelines may ignore this option. Version-guarded internal adapter.");
            notes.Remove("Unavailable API: Player.GetDynamicBatchingForPlatform");
        }
        private static void AddIcons(List<Binding> bindings, NamedBuildTarget target, string label, List<string> notes)
        {
            try
            {
                var sizes = PlayerSettings.GetIconSizes(target, IconKind.Any);
                Add(bindings, "Icons." + label, "Icons", label + " icons", typeof(Texture2D[]), () => PlayerSettings.GetIcons(target, IconKind.Any),
                    value => PlayerSettings.SetIcons(target, (Texture2D[])value, IconKind.Any), notes,
                    "Texture asset list in Unity's icon-size order: " + string.Join(", ", sizes) + ". Use all size slots to override; an empty list clears the override. Empty entries inherit defaults.");
            }
            catch { notes.Add("Icons unavailable for " + label + " in this Unity version."); }
        }
        private static bool ContextType(Type type) => type == typeof(NamedBuildTarget) || type == typeof(BuildTarget) || type == typeof(BuildTargetGroup);
        private static object Context(Type type, BuildTarget target, NamedBuildTarget named) => type == typeof(NamedBuildTarget) ? (object)named : type == typeof(BuildTarget) ? target : (object)BuildTargetGroup.Standalone;

        private static void AddScenesAndProfile(List<Binding> bindings, BuildProfile profile, List<string> notes)
        {
            var global = typeof(EditorBuildSettings).GetProperty("globalScenes", BindingFlags.Public | BindingFlags.Static);
            Add(bindings, "Scenes.Global", "Scenes", "Global scene list", typeof(EditorBuildSettingsScene[]),
                () => global != null ? global.GetValue(null) : EditorBuildSettings.scenes,
                value => { if (global != null) global.SetValue(null, value); else EditorBuildSettings.scenes = (EditorBuildSettingsScene[])value; }, notes,
                "Ordered global scenes, each +Assets/path.unity (enabled) or -Assets/path.unity (disabled). Unsaved scene contents are not saved by this page.");
            if (profile == null) return;
            AddProperty(bindings, typeof(BuildProfile).GetProperty("overrideGlobalScenes"), profile, "Profile", "Active Build Profile", notes);
            AddProperty(bindings, typeof(BuildProfile).GetProperty("scenes"), profile, "Profile", "Active Build Profile", notes);
            AddProperty(bindings, typeof(BuildProfile).GetProperty("scriptingDefines"), profile, "Profile", "Active Build Profile", notes);
            notes.Add("Effective scenes: " + (profile.overrideGlobalScenes ? "active profile scene list (Profile.scenes)" : "global scene list (Scenes.Global)") + ". Profile.scriptingDefines are additive to Target.ScriptingDefineSymbols.");
            // Platform options do not have public per-option APIs. Read/write only
            // actual scalar fields beneath the active profile's platform settings.
            // Never expose identity, object references, managed type names or targets.
            using (var serialized = new SerializedObject(profile))
            {
                var root = serialized.FindProperty("m_PlatformBuildProfile");
                if (root == null) { notes.Add("This Unity version has no supported serialized profile platform-settings layout."); return; }
                var iterator = root.Copy(); var end = iterator.GetEndProperty();
                while (iterator.NextVisible(true) && !SerializedProperty.EqualContents(iterator, end))
                {
                    var path = iterator.propertyPath;
                    if (!path.StartsWith("m_PlatformBuildProfile.", StringComparison.Ordinal)) break;
                    var leaf = path.Substring("m_PlatformBuildProfile.".Length);
                    if (!ProfileLeaves.Contains(leaf))
                    {
                        if (!iterator.isArray && iterator.propertyType != SerializedPropertyType.Generic && iterator.propertyType != SerializedPropertyType.ManagedReference)
                            notes.Add("Profile field excluded (deployment/credentials or unknown version-specific field): " + leaf);
                        continue;
                    }
                    if (iterator.isArray || iterator.propertyType == SerializedPropertyType.Generic || iterator.propertyType == SerializedPropertyType.ManagedReference) continue;
                    var kind = iterator.propertyType;
                    if (kind != SerializedPropertyType.Boolean && kind != SerializedPropertyType.Integer && kind != SerializedPropertyType.Float && kind != SerializedPropertyType.String && kind != SerializedPropertyType.Enum)
                    { notes.Add("Unsupported profile field: " + path); continue; }
                    var id = "ProfilePlatform." + leaf;
                    var capturedPath = path;
                    var valueType = kind == SerializedPropertyType.Boolean ? typeof(bool) : kind == SerializedPropertyType.Integer || kind == SerializedPropertyType.Enum ? typeof(int) : kind == SerializedPropertyType.Float ? typeof(float) : typeof(string);
                    Add(bindings, id, "Active profile platform options", iterator.displayName, valueType,
                        () => ReadSerialized(profile, capturedPath, kind), value => WriteSerialized(profile, capturedPath, kind, value), notes,
                        "Active profile platform option (Unity serialized adapter). " + (kind == SerializedPropertyType.Enum ? "Enum indices: " + string.Join(", ", iterator.enumDisplayNames.Select((name, index) => index + "=" + name)) : ""));
                    if (kind == SerializedPropertyType.Enum)
                    {
                        var binding = bindings.LastOrDefault(item => item.field.id == id);
                        if (binding != null)
                        {
                            binding.field.kind = "enum";
                            binding.field.choices = Enumerable.Range(0, iterator.enumNames.Length).Select(index => index.ToString(Invariant)).ToArray();
                            if (leaf == "m_CompressionType")
                            {
                                binding.field.choices = new[] { "-1" }.Concat(binding.field.choices).ToArray();
                                binding.field.note += "; -1=Default/inherit";
                            }
                        }
                    }
                }
            }
        }

        private static void AddInputHandling(List<Binding> bindings, List<string> notes)
        {
            // A narrowly guarded adapter for a Windows Player setting with no public
            // property API. Never expose arbitrary caller-provided serialized paths.
            var method = typeof(PlayerSettings).GetMethod("GetSerializedObject", BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null) { notes.Add("Active Input Handling is unavailable: Unity PlayerSettings serialized adapter missing."); return; }
            try
            {
                var settings = (SerializedObject)method.Invoke(null, null);
                if (settings.FindProperty("activeInputHandler") == null) { notes.Add("Active Input Handling is unavailable in this Unity serialization layout."); return; }
                Add(bindings, "Player.activeInputHandler", "Player Settings", "Active Input Handling", typeof(int),
                    () => { var current = (SerializedObject)method.Invoke(null, null); current.Update(); return current.FindProperty("activeInputHandler").intValue; },
                    value => { var current = (SerializedObject)method.Invoke(null, null); current.Update(); current.FindProperty("activeInputHandler").intValue = (int)value; current.ApplyModifiedProperties(); }, notes,
                    "0=Input Manager, 1=Input System package, 2=Both. Requires installed input package as appropriate; Editor restart may be required. Version-guarded serialized adapter.");
            }
            catch { notes.Add("Active Input Handling could not be read safely in this Unity version."); }
        }

        private static void AddAdvancedPlayerBindings(List<Binding> bindings, List<string> notes)
        {
            var method = typeof(PlayerSettings).GetMethod("GetSerializedObject", BindingFlags.NonPublic | BindingFlags.Static);
            var names = new[] { "unsupportedMSAAFallback", "keepLoadedShadersAlive", "VertexChannelCompressionMask", "useDeterministicCompilation" };
            if (method == null) { notes.Add("Advanced Windows Player Settings adapter unavailable: " + string.Join(", ", names)); return; }
            foreach (var name in names)
            {
                try
                {
                    var settings = (SerializedObject)method.Invoke(null, null); settings.Update();
                    var property = settings.FindProperty(name);
                    if (property == null || (property.propertyType != SerializedPropertyType.Boolean && property.propertyType != SerializedPropertyType.Integer && property.propertyType != SerializedPropertyType.Enum))
                    { notes.Add("Advanced Player field unavailable or unsupported: " + name); continue; }
                    var kind = property.propertyType;
                    var type = kind == SerializedPropertyType.Boolean ? typeof(bool) : typeof(int);
                    var capturedName = name;
                    var label = name == "unsupportedMSAAFallback" ? "MSAA fallback" : ObjectNames.NicifyVariableName(name);
                    var note = "Effective Player Settings object; fixed version-guarded serialized adapter.";
                    if (name == "VertexChannelCompressionMask") note += " Bitmask of vertex channels (0..16383): position=1, normal=2, tangent=4, color=8, UV0..UV7=16..2048, blend weights=4096, blend indices=8192.";
                    if (name == "useDeterministicCompilation") note += " Changing this flag requests script compilation; wait for reload and refresh.";
                    Add(bindings, "Player." + name, "Advanced Windows Player Settings", label, type,
                        () => ReadSerialized(((SerializedObject)method.Invoke(null, null)).targetObject, capturedName, kind),
                        value =>
                        {
                            WriteSerialized(((SerializedObject)method.Invoke(null, null)).targetObject, capturedName, kind, value);
                            if (capturedName == "useDeterministicCompilation") UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                        }, notes, note);
                    if (kind == SerializedPropertyType.Enum)
                    {
                        var binding = bindings.LastOrDefault(item => item.field.id == "Player." + name);
                        if (binding != null)
                        {
                            binding.field.kind = "enum";
                            binding.field.choices = Enumerable.Range(0, property.enumNames.Length).Select(index => index.ToString(Invariant)).ToArray();
                            binding.field.note += " Enum indices: " + string.Join(", ", property.enumDisplayNames.Select((labelName, index) => index + "=" + labelName));
                        }
                    }
                }
                catch { notes.Add("Advanced Player field could not be read: " + name); }
            }
        }

        private static object ReadSerialized(Object target, string path, SerializedPropertyType kind)
        {
            using (var serialized = new SerializedObject(target))
            {
                serialized.Update(); var property = serialized.FindProperty(path);
                if (property == null || property.propertyType != kind) throw new InvalidOperationException();
                return kind == SerializedPropertyType.Boolean ? (object)property.boolValue : kind == SerializedPropertyType.Integer ? property.intValue :
                    kind == SerializedPropertyType.Enum ? property.enumValueIndex : kind == SerializedPropertyType.Float ? property.floatValue : (object)property.stringValue;
            }
        }
        private static void WriteSerialized(Object target, string path, SerializedPropertyType kind, object value)
        {
            using (var serialized = new SerializedObject(target))
            {
                serialized.Update(); var property = serialized.FindProperty(path);
                if (property == null || property.propertyType != kind) throw new InvalidOperationException();
                if (kind == SerializedPropertyType.Boolean) property.boolValue = (bool)value;
                else if (kind == SerializedPropertyType.Integer) property.intValue = (int)value;
                else if (kind == SerializedPropertyType.Enum)
                {
                    if ((int)value == -1 && path == "m_PlatformBuildProfile.m_CompressionType") property.intValue = -1;
                    else { if ((int)value < 0 || (int)value >= property.enumNames.Length) throw new ArgumentException(); property.enumValueIndex = (int)value; }
                }
                else if (kind == SerializedPropertyType.Float) property.floatValue = (float)value;
                else property.stringValue = (string)value;
                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(target);
            }
        }

        private static void AddCompression(List<Binding> bindings, List<string> notes)
        {
            var getter = typeof(EditorUserBuildSettings).GetMethod("GetCompressionType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var setter = typeof(EditorUserBuildSettings).GetMethod("SetCompressionType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (getter == null || setter == null) { notes.Add("Compression method API unavailable in this Unity version; active profile's serialized compression field is shown if present."); return; }
            Add(bindings, "Build.compressionType", "Windows build options", "Compression method", getter.ReturnType,
                () => getter.Invoke(null, new object[] { BuildTargetGroup.Standalone }), value => setter.Invoke(null, new[] { (object)BuildTargetGroup.Standalone, value }), notes, "Standalone player data compression. Version-guarded internal API when no public API exists.");
        }

        private static void Add(List<Binding> bindings, string id, string category, string label, Type type, Func<object> getter, Action<object> setter, List<string> notes, string note)
        {
            try
            {
                var value = Encode(getter(), type);
                if (type == typeof(PlayerSettings.SplashScreenLogo[])) note += " Each items entry is a JSON string containing {\"items\":[\"2\",\"Assets/logo.png#localFileId\"]} (duration seconds, sprite). Empty sprite is a delay; @unity-logo is Unity's built-in logo.";
                if (type.IsArray && type.GetElementType().IsEnum) note += " Array values: " + string.Join(", ", Enum.GetNames(type.GetElementType()).Where(name => !Obsolete(type.GetElementType().GetField(name))));
                var field = new BuildConfigurationField { id = id, category = category, label = ObjectNames.NicifyVariableName(label), kind = Kind(type), value = value, editable = setter != null,
                    choices = type.IsEnum ? Enum.GetNames(type).Where(name => !Obsolete(type.GetField(name))).ToArray() : new string[0], note = note };
                if (type.IsEnum && !field.choices.Contains(value))
                {
                    field.editable = false;
                    field.note += " Current enum value has no supported API name; preserved read-only rather than substituting a default. Use the profile's explicit inherit/default choice if available.";
                }
                bindings.Add(new Binding { field = field, type = type, get = getter, set = setter });
            }
            catch { notes.Add("Unavailable or unsupported by this Editor: " + id); }
        }

        private static bool Supported(Type type) => type == typeof(bool) || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(float) || type == typeof(double) || type == typeof(string) || type.IsEnum ||
            typeof(Object).IsAssignableFrom(type) || type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Color) || type == typeof(Guid) ||
            type == typeof(EditorBuildSettingsScene) || type == typeof(PlayerSettings.SplashScreenLogo) || (type.IsArray && Supported(type.GetElementType()));
        private static string Kind(Type type) => type == typeof(bool) ? "bool" : type == typeof(int) || type == typeof(uint) || type == typeof(long) ? "integer" : type == typeof(float) || type == typeof(double) ? "number" :
            type == typeof(string) || type == typeof(Guid) ? "string" : type.IsEnum ? "enum" : typeof(Object).IsAssignableFrom(type) ? "asset" : "json";
        private static bool Obsolete(MemberInfo member) => member != null && member.IsDefined(typeof(ObsoleteAttribute), true);
        public static bool IsWindows(BuildTarget target) => IsWindowsName(target.ToString());
        private static bool IsWindowsName(string name) => name == "StandaloneWindows" || name == "StandaloneWindows64" || name == "StandaloneWindowsArm64";
        private static string Hash(string value) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }

        private static string Encode(object value, Type type)
        {
            if (typeof(Object).IsAssignableFrom(type))
            {
                var asset = value as Object; if (asset == null) return "";
                var path = AssetDatabase.GetAssetPath(asset);
                if (string.IsNullOrEmpty(path) || (!path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal))) throw new InvalidOperationException();
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long localId);
                return path + "#" + localId.ToString(Invariant);
            }
            if (type.IsArray) return JsonUtility.ToJson(new Strings { items = ((Array)value ?? Array.CreateInstance(type.GetElementType(), 0)).Cast<object>().Select(item => Encode(item, type.GetElementType())).ToArray() });
            if (type == typeof(bool)) return (bool)value ? "true" : "false";
            if (type == typeof(EditorBuildSettingsScene)) { var scene = (EditorBuildSettingsScene)value; return (scene.enabled ? "+" : "-") + scene.path; }
            if (type == typeof(PlayerSettings.SplashScreenLogo))
            {
                var logo = (PlayerSettings.SplashScreenLogo)value;
                return JsonUtility.ToJson(new Strings { items = new[] { logo.duration.ToString("R", Invariant), logo.logo == PlayerSettings.SplashScreenLogo.unityLogo ? "@unity-logo" : Encode(logo.logo, typeof(Sprite)) } });
            }
            if (type == typeof(Vector2)) { var v = (Vector2)value; return Components(v.x, v.y); }
            if (type == typeof(Vector3)) { var v = (Vector3)value; return Components(v.x, v.y, v.z); }
            if (type == typeof(Vector4)) { var v = (Vector4)value; return Components(v.x, v.y, v.z, v.w); }
            if (type == typeof(Color)) { var v = (Color)value; return Components(v.r, v.g, v.b, v.a); }
            return Convert.ToString(value, Invariant) ?? "";
        }
        private static string Components(params float[] values) => JsonUtility.ToJson(new Strings { items = values.Select(value => value.ToString("R", Invariant)).ToArray() });
        private static object Decode(string value, Type type)
        {
            if (type == typeof(string)) return value;
            if (type == typeof(Guid)) return Guid.Parse(value);
            if (type == typeof(bool)) { if (value != "true" && value != "false") throw new ArgumentException(); return value == "true"; }
            if (type == typeof(int)) return int.Parse(value, NumberStyles.Integer, Invariant);
            if (type == typeof(uint)) return uint.Parse(value, NumberStyles.Integer, Invariant);
            if (type == typeof(long)) return long.Parse(value, NumberStyles.Integer, Invariant);
            if (type == typeof(float)) { var result = float.Parse(value, NumberStyles.Float, Invariant); if (float.IsInfinity(result) || float.IsNaN(result)) throw new ArgumentException(); return result; }
            if (type == typeof(double)) { var result = double.Parse(value, NumberStyles.Float, Invariant); if (double.IsInfinity(result) || double.IsNaN(result)) throw new ArgumentException(); return result; }
            if (type.IsEnum) { if (!Enum.GetNames(type).Contains(value) || Obsolete(type.GetField(value))) throw new ArgumentException(); return Enum.Parse(type, value); }
            if (typeof(Object).IsAssignableFrom(type)) return DecodeAsset(value, type);
            if (type.IsArray)
            {
                var list = new Strings { items = ParseStringList(value) };
                var result = Array.CreateInstance(type.GetElementType(), list.items.Length);
                for (var i = 0; i < result.Length; i++) result.SetValue(Decode(list.items[i], type.GetElementType()), i);
                return result;
            }
            if (type == typeof(EditorBuildSettingsScene))
            {
                if (value.Length < 2 || (value[0] != '+' && value[0] != '-')) throw new ArgumentException();
                var path = value.Substring(1);
                if (!SafeAssetPath(path) || AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null) throw new ArgumentException();
                return new EditorBuildSettingsScene(path, value[0] == '+');
            }
            if (type == typeof(PlayerSettings.SplashScreenLogo))
            {
                var logo = ParseStringList(value);
                if (logo.Length != 2) throw new ArgumentException();
                var duration = (float)Decode(logo[0], typeof(float));
                if (duration < 2 || duration > 10) throw new ArgumentException();
                if (logo[1] == "@unity-logo") return PlayerSettings.SplashScreenLogo.CreateWithUnityLogo(duration);
                return PlayerSettings.SplashScreenLogo.Create(duration, (Sprite)DecodeAsset(logo[1], typeof(Sprite)));
            }
            if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Color))
            {
                var items = ParseStringList(value);
                var count = type == typeof(Vector2) ? 2 : type == typeof(Vector3) ? 3 : 4;
                if (items == null || items.Length != count) throw new ArgumentException();
                var components = items.Select(item => (float)Decode(item, typeof(float))).ToArray();
                if (count == 2) return new Vector2(components[0], components[1]);
                if (count == 3) return new Vector3(components[0], components[1], components[2]);
                if (type == typeof(Color)) return new Color(components[0], components[1], components[2], components[3]);
                return new Vector4(components[0], components[1], components[2], components[3]);
            }
            throw new ArgumentException();
        }
        private static Object DecodeAsset(string value, Type type)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var split = value.LastIndexOf('#'); var path = split < 0 ? value : value.Substring(0, split);
            if (!SafeAssetPath(path)) throw new ArgumentException();
            if (split < 0) { var asset = AssetDatabase.LoadAssetAtPath(path, type); if (asset == null) throw new ArgumentException(); return asset; }
            var id = long.Parse(value.Substring(split + 1), Invariant);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path).Where(type.IsInstanceOfType))
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long localId);
                if (localId == id) return asset;
            }
            throw new ArgumentException();
        }
        // Strict, deliberately small JSON schema: exactly {"items":[strings]}.
        // JsonUtility alone silently ignores unknown/missing members, which could
        // otherwise turn a malformed edit into an unintended empty list.
        private static string[] ParseStringList(string json)
        {
            var index = 0;
            Action whitespace = () => { while (index < json.Length && char.IsWhiteSpace(json[index])) index++; };
            Action<char> expect = character => { whitespace(); if (index >= json.Length || json[index++] != character) throw new ArgumentException(); };
            Func<string> readString = () =>
            {
                whitespace(); var start = index;
                if (index >= json.Length || json[index++] != '"') throw new ArgumentException();
                while (index < json.Length)
                {
                    var character = json[index++];
                    if (character < 32) throw new ArgumentException();
                    if (character == '\\')
                    {
                        if (index >= json.Length) throw new ArgumentException();
                        var escape = json[index++];
                        if (escape == 'u')
                        {
                            for (var i = 0; i < 4; i++) { if (index >= json.Length || !Uri.IsHexDigit(json[index++])) throw new ArgumentException(); }
                        }
                        else if ("\"\\/bfnrt".IndexOf(escape) < 0) throw new ArgumentException();
                    }
                    else if (character == '"')
                    {
                        var decoded = JsonUtility.FromJson<Quoted>("{\"text\":" + json.Substring(start, index - start) + "}").text;
                        if (decoded == null || decoded.Contains("\0")) throw new ArgumentException();
                        return decoded;
                    }
                }
                throw new ArgumentException();
            };
            expect('{'); if (readString() != "items") throw new ArgumentException(); expect(':'); expect('[');
            var items = new List<string>(); whitespace();
            if (index < json.Length && json[index] != ']')
                while (true)
                {
                    items.Add(readString()); if (items.Count > 256) throw new ArgumentException(); whitespace();
                    if (index >= json.Length) throw new ArgumentException();
                    if (json[index] != ',') break;
                    index++;
                }
            expect(']'); expect('}'); whitespace(); if (index != json.Length) throw new ArgumentException();
            return items.ToArray();
        }
        private static bool SafeAssetPath(string path) => (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)) &&
            path.Length <= 1024 && !path.Contains("\\") && !path.Contains(":") && !path.Any(char.IsControl) && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
        private static void Validate(Binding binding, object value)
        {
            var id = binding.field.id;
            if (id == "Build.selectedStandaloneTarget" && !IsWindowsName(value.ToString())) throw new ArgumentException();
            if (id == "Player.activeInputHandler" && ((int)value < 0 || (int)value > 2)) throw new ArgumentException();
            if ((id == "Player.defaultScreenWidth" || id == "Player.defaultScreenHeight") && ((int)value < 1 || (int)value > 32768)) throw new ArgumentException();
            if (id == "Build.managedDebuggerFixedPort" && ((int)value < 0 || (int)value > 65535)) throw new ArgumentException();
            if (id == "Player.VertexChannelCompressionMask" && ((int)value < 0 || (int)value > 16383)) throw new ArgumentException();
            if (id.StartsWith("Icons.", StringComparison.Ordinal))
            {
                var count = ((Array)value).Length;
                var target = id == "Icons.Default" ? NamedBuildTarget.Unknown : NamedBuildTarget.Standalone;
                if (count != 0 && count != PlayerSettings.GetIconSizes(target, IconKind.Any).Length) throw new ArgumentException();
            }
            if (id == "Target.LightmapStreamingPriorityForPlatformGroup" && ((int)value < -128 || (int)value > 127)) throw new ArgumentException();
            if (id == "Target.GraphicsAPIs")
            {
                var values = ((Array)value).Cast<object>().ToArray();
                if (values.Length == 0 || values.Distinct().Count() != values.Length) throw new ArgumentException();
                var supportedMethod = typeof(PlayerSettings).GetMethod("GetSupportedGraphicsAPIs", BindingFlags.NonPublic | BindingFlags.Static);
                if (supportedMethod == null) throw new ArgumentException();
                var allowed = ((Array)supportedMethod.Invoke(null, new object[] { EditorUserBuildSettings.activeBuildTarget })).Cast<object>().ToArray();
                if (values.Any(item => !allowed.Contains(item))) throw new ArgumentException();
            }
            if (id == "Target.ColorGamuts" && !((Array)value).Cast<object>().Any(item => item.ToString() == "sRGB")) throw new ArgumentException();
            if (binding.type == typeof(EditorBuildSettingsScene[]))
            {
                var scenes = (EditorBuildSettingsScene[])value;
                if (scenes.Select(scene => scene.path).Distinct(StringComparer.Ordinal).Count() != scenes.Length) throw new ArgumentException();
            }
        }
    }
}
