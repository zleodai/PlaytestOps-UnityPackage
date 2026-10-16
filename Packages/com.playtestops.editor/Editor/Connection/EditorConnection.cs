using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace PlaytestOps.Editor
{
    [InitializeOnLoad]
    public static partial class EditorConnection
    {
        private const string StateKey = "PlaytestOps.Connection";
        private static CancellationTokenSource lifetime;
        private static ClientWebSocket socket;
        private static readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
        private static Credentials credentials;
        private static bool needsDiscovery;
        private static string runProbeId;
        private static int generation;
        private static ClientWebSocket sourceBusyConnection;
        private sealed class DeferredBuildMessage { public ClientWebSocket connection; public string json; }
        private static readonly ConcurrentQueue<DeferredBuildMessage> DeferredBuildMessages = new ConcurrentQueue<DeferredBuildMessage>();
        private static ClientWebSocket versionControlBusyConnection;
        private static CancellationTokenSource versionControlReading;
        public static event Action Changed;
        public static string Status { get; private set; } = "Disconnected";
        public static string Error { get; private set; }
        public static bool Connected => socket?.State == WebSocketState.Open && Status == "Connected";
        public static bool Active => lifetime != null;
        public static int SyncedCount { get; private set; }
        public static string LastSync { get; private set; }
        public static string DefaultAddress => EditorPrefs.GetString("PlaytestOps.ServerAddress", "http://localhost:5282");

        [Serializable] private sealed class Credentials { public string address; public string token; public string expiresAt; }
        [Serializable] private sealed class ProjectIdentity { public string projectId; }
        [Serializable] private sealed class PairInput { public string code; public string projectId; public string projectName; public string unityVersion; }
        [Serializable] private sealed class PairOutput { public string token = ""; public string expiresAt = ""; }
        [Serializable] private sealed class Envelope { public string type = ""; public string requestId = ""; public string error = ""; public int count = 0; public string runId; public string mode; public string uniqueName; public int sequence; public string path; public int offset; public string scope = ""; public string revision = ""; public string settingId = ""; public string value = ""; public string buildId = ""; public string buildToken = ""; }
        [Serializable] private sealed class VersionControlMessage
        {
            public string type = "vcs.snapshot.result", requestId = "";
            public VersionControlSnapshot snapshot = new VersionControlSnapshot();
        }
        [Serializable] private sealed class SourceListMessage
        {
            public string type = "source.list.result"; public string requestId; public string error = "";
            public string[] roots = new string[0]; public SourceFileEntry[] files = new SourceFileEntry[0]; public bool truncated;
        }
        [Serializable] private sealed class SourceReadMessage
        {
            public string type = "source.read.result"; public string requestId; public string error = "";
            public string path = ""; public long byteLength; public string content = ""; public string sha256 = ""; public string lastModifiedUtc = "";
        }
        [Serializable] private sealed class RunMessage {
            public string type = "run.update"; public string runId; public int sequence; public string state; public string outcome;
            public double durationSeconds; public string message; public string stackTrace; public string output;
            public RunLogEntry[] logs; public bool logsTruncated; public int droppedLogCount;
        }
        [Serializable] private sealed class Outbox { public List<RunMessage> items = new List<RunMessage>(); }
        private static Outbox outbox = new Outbox();
        private const string OutboxKey = "PlaytestOps.RunOutbox";
        [Serializable] private sealed class Catalog { public string type = "catalog.replace"; public string requestId; public TestCase[] tests; }

        static EditorConnection()
        {
            var pending = SessionState.GetString(OutboxKey, "");
            if (!string.IsNullOrEmpty(pending)) { try { outbox = JsonUtility.FromJson<Outbox>(pending) ?? new Outbox(); } catch { } }
            PlaytestSession.RunChanged += QueueRun;
            PlaytestSession.RemoteRejected += (id, error) => Queue(new RunMessage { runId = id, sequence = 1, state = "Failed", outcome = "Rejected", message = Clip(error, 16000) });
            PlaytestSession.CatalogChanged += PublishCatalog;
            PlaytestSession.Changed += DiscoverWhenReady;
            AssemblyReloadEvents.beforeAssemblyReload += StopForReload;
            EditorApplication.quitting += StopForReload;
            var saved = SessionState.GetString(StateKey, "");
            if (string.IsNullOrEmpty(saved)) return;
            try { credentials = JsonUtility.FromJson<Credentials>(saved); }
            catch { SessionState.EraseString(StateKey); return; }
            // delayCall can wait for an Inspector repaint while Unity is in the background.
            // This one-shot hook exists only while restoring a previously paired session.
            EditorApplication.update += ResumeAfterReload;
        }

        private static void ResumeAfterReload()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            EditorApplication.update -= ResumeAfterReload;
            Resume();
        }

        public static async void Connect(string address, string code)
        {
            if (Active) return;
            var currentGeneration = ++generation;
            lifetime = new CancellationTokenSource();
            var ct = lifetime.Token;
            Status = "Pairing"; Error = null; LastSync = null; Notify();
            try
            {
                var uri = ValidateAddress(address);
                var identityFile = Path.GetFullPath("ProjectSettings/PlaytestOps.json");
                ProjectIdentity identity;
                if (File.Exists(identityFile)) identity = JsonUtility.FromJson<ProjectIdentity>(File.ReadAllText(identityFile));
                else
                {
                    identity = new ProjectIdentity { projectId = Guid.NewGuid().ToString("N") };
                    File.WriteAllText(identityFile, JsonUtility.ToJson(identity, true));
                }
                if (identity == null || !Guid.TryParseExact(identity.projectId, "N", out _))
                    throw new InvalidOperationException("ProjectSettings/PlaytestOps.json has an invalid projectId.");
                var input = new PairInput { code = code.Trim(), projectId = identity.projectId,
                    projectName = Application.productName, unityVersion = Application.unityVersion };
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                using (var body = new StringContent(JsonUtility.ToJson(input), Encoding.UTF8, "application/json"))
                using (var response = await client.PostAsync(new Uri(uri, "api/editor/pair"), body, ct))
                {
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                            ? "Invalid or expired pairing code. Generate a new code in the dashboard."
                            : response.StatusCode == System.Net.HttpStatusCode.Conflict
                                ? "This dashboard is already connected to a Unity Editor. Disconnect it on the dashboard Editors page before connecting another."
                                : "Pairing failed (HTTP " + (int)response.StatusCode + "). Check the dashboard address and try again.");
                    var result = JsonUtility.FromJson<PairOutput>(json);
                    if (result == null || string.IsNullOrEmpty(result.token)) throw new InvalidOperationException("The server returned an invalid pairing response.");
                    ct.ThrowIfCancellationRequested();
                    outbox.items.Clear(); SaveOutbox();
                    credentials = new Credentials { address = uri.AbsoluteUri, token = result.token, expiresAt = result.expiresAt };
                    SessionState.SetString(StateKey, JsonUtility.ToJson(credentials));
                    EditorPrefs.SetString("PlaytestOps.ServerAddress", uri.AbsoluteUri.TrimEnd('/'));
                }
                await ConnectionLoop(currentGeneration, ct);
            }
            catch (OperationCanceledException)
            {
                if (currentGeneration == generation) { Error = "Pairing timed out. Check the address and try again."; Stop(false); }
            }
            catch (Exception ex) { if (currentGeneration == generation) { Error = ex.Message; Stop(false); } }
        }

        private static Uri ValidateAddress(string address)
        {
            if (!Uri.TryCreate(address.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
                uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
                throw new InvalidOperationException("Use the server origin, for example http://localhost:5282, without a path.");
            if (uri.Scheme == "http" && !uri.IsLoopback)
                throw new InvalidOperationException("Use HTTPS when the server is on another machine.");
            return uri;
        }

        private static async void Resume()
        {
            if (Active || credentials == null) return;
            var currentGeneration = ++generation;
            lifetime = new CancellationTokenSource();
            try { await ConnectionLoop(currentGeneration, lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (currentGeneration == generation) { Error = ex.Message; Stop(false); } }
        }

        private static async Task ConnectionLoop(int currentGeneration, CancellationToken ct)
        {
            var failures = 0;
            while (!ct.IsCancellationRequested && currentGeneration == generation)
            {
                if (!DateTimeOffset.TryParse(credentials.expiresAt, out var expires) || expires <= DateTimeOffset.UtcNow)
                    throw new InvalidOperationException("Pairing expired. Generate a new code to connect again.");
                Status = failures == 0 ? "Connecting" : "Reconnecting"; Notify();
                using (var connection = new ClientWebSocket())
                {
                    socket = connection;
                    connection.Options.SetRequestHeader("Authorization", "Bearer " + credentials.token);
                    connection.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    var uri = new UriBuilder(new Uri(new Uri(credentials.address), "api/editor/connect"));
                    uri.Scheme = uri.Scheme == "https" ? "wss" : "ws";
                    try
                    {
                        using (var opening = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            opening.CancelAfter(TimeSpan.FromSeconds(10));
                            await connection.ConnectAsync(uri.Uri, opening.Token);
                        }
                        var ready = JsonUtility.FromJson<Envelope>(await Receive(connection, ct));
                        if (ready?.type != "session.ready") throw new InvalidOperationException("Unexpected server handshake.");
                        failures = 0; Status = "Connected"; Error = null; needsDiscovery = true;
                        Notify();
                        StartBuildDelivery(connection, currentGeneration, credentials.address, ct);
                        foreach (var pendingRun in outbox.items.ToArray()) PublishRun(pendingRun);
                        DiscoverWhenReady();
                        while (connection.State == WebSocketState.Open && !ct.IsCancellationRequested)
                        {
                            var json = await Receive(connection, ct);
                            if (json == null) break;
                            var message = JsonUtility.FromJson<Envelope>(json);
                            if (message.type == "catalog.accepted")
                            {
                                SyncedCount = message.count; LastSync = DateTime.Now.ToString("HH:mm:ss"); Error = null;
                            }
                            else if (message.type == "run.probe")
                            {
                                if (Guid.TryParseExact(message.requestId, "N", out _))
                                {
                                    runProbeId = message.requestId;
                                    EditorApplication.update -= ReportReadyWhenIdle;
                                    EditorApplication.update += ReportReadyWhenIdle;
                                }
                            }
                            else if (message.type == "run.request") PlaytestSession.RunRemote(message.runId, message.mode, message.uniqueName);
                            else if (message.type == "source.list" || message.type == "source.read")
                                ReplySource(message, connection, currentGeneration, ct);
                            else if (message.type == "vcs.snapshot")
                                ReplyVersionControl(message, connection, currentGeneration, ct);
                            else if (message.type == "build.settings.get" || message.type == "build.settings.set")
                                ReplyBuildSettings(message, connection, currentGeneration, ct);
                            else if (message.type == "build.request")
                                WindowsBuildExecution.Request(message.buildId, message.buildToken, message.revision, credentials.address);
                            else if (message.type == "run.accepted")
                            {
                                outbox.items.RemoveAll(x => x.runId == message.runId && x.sequence <= message.sequence);
                                SaveOutbox();
                            }
                            else if (message.type == "error") Error = message.error;
                            Notify();
                        }
                        ct.ThrowIfCancellationRequested();
                        throw new WebSocketException("The server closed the connection.");
                    }
                    catch (Exception ex) when (ex is WebSocketException || ex is HttpRequestException || ex is OperationCanceledException)
                    {
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        if (++failures >= 3) throw new InvalidOperationException("Connection lost. Check the server and pair again if it restarted.");
                        Error = "Connection interrupted; retrying shortly."; Status = "Reconnecting"; Notify();
                    }
                    finally { CancelBuildDelivery(); ClearRunProbe(); CancelVersionControl(connection); if (ReferenceEquals(socket, connection)) socket = null; }
                }
                await Task.Delay(TimeSpan.FromSeconds(failures * 3), ct);
            }
        }

        private static void ClearRunProbe()
        {
            runProbeId = null;
            EditorApplication.update -= ReportReadyWhenIdle;
        }

        private static async void ReportReadyWhenIdle()
        {
            if (!Connected) { ClearRunProbe(); return; }
            // Check after PlayMode exit and settings restoration, not merely RunFinished.
            if (PlaytestSession.Busy || WindowsBuildExecution.Busy || EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            DiscoverWhenReady();
            if (needsDiscovery || PlaytestSession.Busy || outbox.items.Count > 0) return;
            var id = runProbeId;
            ClearRunProbe();
            var connection = socket;
            var ct = lifetime.Token;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new Envelope { type = "run.ready", requestId = id }));
                await SendLock.WaitAsync(ct);
                try { await connection.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct); }
                finally { SendLock.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { connection.Abort(); }
        }

        private static void DiscoverWhenReady()
        {
            if (!Connected || !needsDiscovery || PlaytestSession.Busy || WindowsBuildExecution.Busy || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            needsDiscovery = false;
            PlaytestSession.Discover();
        }

        private static async void PublishCatalog()
        {
            if (!Connected || !PlaytestSession.HasDiscovered) return;
            var connection = socket;
            var ct = lifetime.Token;
            var currentGeneration = generation;
            var message = new Catalog { requestId = Guid.NewGuid().ToString("N"), tests = PlaytestSession.Catalog.ToArray() };
            var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(message));
            if (bytes.Length > 2 * 1024 * 1024 || message.tests.Length > 5000)
            { Error = "Catalog exceeds the server limit (5000 tests / 2 MiB)."; Notify(); return; }
            try
            {
                await SendLock.WaitAsync(ct);
                try { await connection.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct); }
                finally { SendLock.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (currentGeneration == generation) { Error = "Catalog sync failed: " + ex.Message; Notify(); connection.Abort(); }
            }
        }

        private static string Clip(string text, int limit)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length <= limit) return text;
            var cut = limit - 20;
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            return text.Substring(0, cut) + "\n[truncated]";
        }

        private static async void ReplySource(Envelope request, ClientWebSocket connection, int currentGeneration, CancellationToken ct)
        {
            if (!Guid.TryParseExact(request.requestId, "N", out _)) return;
            var list = request.type == "source.list";
            var ownsRequest = !ReferenceEquals(sourceBusyConnection, connection);
            object response;
            if (!ownsRequest)
            {
                response = SourceFailure(request, "Another source request is in progress. Try again shortly.");
            }
            else
            {
                sourceBusyConnection = connection;
                try
                {
                    if (PlaytestSession.Busy || EditorApplication.isCompiling || EditorApplication.isUpdating)
                        throw new InvalidOperationException("Unity is busy compiling, importing, or running tests. Retry the source request when it is idle.");
                    // Unity APIs and JsonUtility settings run on the Editor thread; the
                    // bounded filesystem scan/read uses only pure C# on a worker thread.
                    var projectRoot = SourceSettings.ProjectRoot;
                    var roots = SourceSettings.Load(projectRoot);
                    using (var reading = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        reading.CancelAfter(TimeSpan.FromSeconds(10));
                        var readToken = reading.Token;
                        if (list)
                        {
                            var result = await Task.Run(() => new SourceReader(projectRoot, roots).List(readToken), readToken);
                            response = new SourceListMessage { requestId = request.requestId, roots = result.roots, files = result.files, truncated = result.truncated };
                        }
                        else
                        {
                            var path = request.path;
                            var result = await Task.Run(() => new SourceReader(projectRoot, roots).Read(path, readToken), readToken);
                            response = new SourceReadMessage { requestId = request.requestId, path = result.path, byteLength = result.byteLength,
                                content = result.content, sha256 = result.sha256, lastModifiedUtc = result.lastModifiedUtc };
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                    {
                        if (ReferenceEquals(sourceBusyConnection, connection)) sourceBusyConnection = null;
                        return;
                    }
                    response = SourceFailure(request, "The source request reached its 10-second safety limit. Retry with narrower author folders.");
                }
                catch (Exception exception)
                {
                    // Do not send filesystem exceptions containing absolute host paths.
                    response = SourceFailure(request, exception is InvalidOperationException
                        ? Clip(exception.Message, 512) : "The source file or folder is unavailable. Check its permissions and source-folder settings in Unity.");
                }
            }
            try
            {
                if (ct.IsCancellationRequested || currentGeneration != generation || !ReferenceEquals(socket, connection)) return;
                var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(response));
                if (bytes.Length > SourceReader.MaxResponseBytes)
                    bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(SourceFailure(request, "The source response exceeds its safety limit.")));
                await SendLock.WaitAsync(ct);
                try
                {
                    // Reconnecting may replace the socket while this response awaits the
                    // send gate. Never replay source contents onto a new Editor session.
                    if (currentGeneration != generation || !ReferenceEquals(socket, connection) || connection.State != WebSocketState.Open) return;
                    using (var sending = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        sending.CancelAfter(TimeSpan.FromSeconds(10));
                        await connection.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sending.Token);
                    }
                }
                finally { SendLock.Release(); }
            }
            catch (OperationCanceledException)
            {
                if (!ct.IsCancellationRequested && currentGeneration == generation && ReferenceEquals(socket, connection)) connection.Abort();
            }
            catch (Exception)
            {
                if (currentGeneration == generation && ReferenceEquals(socket, connection)) connection.Abort();
            }
            finally
            {
                if (ownsRequest && ReferenceEquals(sourceBusyConnection, connection)) sourceBusyConnection = null;
            }
        }

        private static object SourceFailure(Envelope request, string message) => request.type == "source.list"
            ? (object)new SourceListMessage { requestId = request.requestId, error = message }
            : new SourceReadMessage { requestId = request.requestId, path = Clip(request.path, 512), error = message };

        private static bool VersionControlEditorBusy => PlaytestSession.Busy || WindowsBuildExecution.Busy || EditorApplication.isCompiling ||
            EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode;

        private static void CancelVersionControl(ClientWebSocket connection)
        {
            if (!ReferenceEquals(versionControlBusyConnection, connection)) return;
            versionControlReading?.Cancel();
            EditorApplication.update -= CancelVersionControlWhenBusy;
        }

        private static void CancelVersionControlWhenBusy()
        {
            if (VersionControlEditorBusy || !ReferenceEquals(socket, versionControlBusyConnection) || !Connected)
                CancelVersionControl(versionControlBusyConnection);
        }

        private static async void ReplyVersionControl(Envelope request, ClientWebSocket connection, int currentGeneration, CancellationToken ct)
        {
            if (!Guid.TryParseExact(request.requestId, "N", out _) || request.offset < 0 || request.offset > 100000 ||
                (request.scope != "repository" && request.scope != "branch")) return;
            var ownsRequest = versionControlBusyConnection == null;
            var response = new VersionControlMessage { requestId = request.requestId };
            if (!ownsRequest || VersionControlEditorBusy)
                response.snapshot = VersionControlSnapshot.Failure("busy", "editorBusy", request.offset, request.scope);
            else
            {
                versionControlBusyConnection = connection;
                using (var reading = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    versionControlReading = reading;
                    EditorApplication.update += CancelVersionControlWhenBusy;
                    try
                    {
                        // Capture Unity state on its main thread; all cm execution,
                        // XML parsing and local workspace reads happen off that thread.
                        var projectRoot = Path.GetDirectoryName(Application.dataPath);
                        if (Path.DirectorySeparatorChar != '\\')
                            response.snapshot = VersionControlSnapshot.Failure("failed", "unsupportedPlatform", request.offset, request.scope);
                        else
                        {
                            var executable = VersionControlCommandRunner.FindTrustedExecutable(projectRoot);
                            response.snapshot = await Task.Run(() => new CliVersionControlReader(projectRoot, executable)
                                .ReadAsync(request.offset, request.scope, reading.Token), reading.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (ct.IsCancellationRequested || currentGeneration != generation || !ReferenceEquals(socket, connection)) return;
                        response.snapshot = VersionControlSnapshot.Failure("busy", "editorBusy", request.offset, request.scope);
                    }
                    catch (VersionControlReadException ex) { response.snapshot = VersionControlSnapshot.Failure("failed", ex.code, request.offset, request.scope); }
                    catch { response.snapshot = VersionControlSnapshot.Failure("failed", "invalidData", request.offset, request.scope); }
                    finally
                    {
                        EditorApplication.update -= CancelVersionControlWhenBusy;
                        if (ReferenceEquals(versionControlReading, reading)) versionControlReading = null;
                        if (ReferenceEquals(versionControlBusyConnection, connection)) versionControlBusyConnection = null;
                    }
                }
            }
            try
            {
                if (ct.IsCancellationRequested || currentGeneration != generation || !ReferenceEquals(socket, connection)) return;
                var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(response));
                if (bytes.Length > CliVersionControlReader.MaxResponseBytes)
                    bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new VersionControlMessage { requestId = request.requestId,
                        snapshot = VersionControlSnapshot.Failure("failed", "outputLimit", request.offset, request.scope) }));
                using (var sending = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    sending.CancelAfter(TimeSpan.FromSeconds(3));
                    await SendLock.WaitAsync(sending.Token);
                    try
                    {
                        if (currentGeneration != generation || !ReferenceEquals(socket, connection) || connection.State != WebSocketState.Open) return;
                        await connection.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sending.Token);
                    }
                    finally { SendLock.Release(); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (currentGeneration == generation && ReferenceEquals(socket, connection)) connection.Abort(); }
            finally
            {
                if (ownsRequest && ReferenceEquals(versionControlBusyConnection, connection)) versionControlBusyConnection = null;
            }
        }

        private static void QueueRun(RunRecord run)
        {
            if (string.IsNullOrEmpty(run.dashboardRunId) || run.lifecycle == "Starting") return;
            Queue(new RunMessage {
                runId = run.dashboardRunId, sequence = run.revision,
                state = run.IsActive ? "Running" : run.lifecycle == "Completed" && run.outcome == "Passed" ? "Passed" : "Failed",
                outcome = run.IsActive ? "" : run.lifecycle == "Interrupted" ? "Interrupted" : Clip(run.outcome, 128),
                durationSeconds = run.durationSeconds, message = Clip(run.message, 16000),
                stackTrace = Clip(run.stackTrace, 32000), output = Clip(run.output, 64000),
                logs = run.logs?.ToArray() ?? new RunLogEntry[0], logsTruncated = run.logsTruncated, droppedLogCount = run.droppedLogCount
            });
        }

        private static void Queue(RunMessage message)
        {
            outbox.items.RemoveAll(x => x.runId == message.runId);
            outbox.items.Add(message);
            SaveOutbox();
            PublishRun(message);
        }

        private static void SaveOutbox() => SessionState.SetString(OutboxKey, JsonUtility.ToJson(outbox));

        private static async void PublishRun(RunMessage message)
        {
            if (!Connected) return;
            var connection = socket;
            var ct = lifetime.Token;
            var currentGeneration = generation;
            try
            {
                await SendLock.WaitAsync(ct);
                try
                {
                    // A newer cumulative snapshot supersedes waiting updates. Do not
                    // allocate large payloads before the send gate or for old sockets.
                    if (!Connected || !ReferenceEquals(socket, connection) ||
                        !outbox.items.Any(x => x.runId == message.runId && x.sequence == message.sequence)) return;
                    var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(message));
                    using (var sending = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        sending.CancelAfter(TimeSpan.FromSeconds(10));
                        await connection.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sending.Token);
                    }
                }
                finally { SendLock.Release(); }
            }
            catch (OperationCanceledException)
            {
                if (!ct.IsCancellationRequested && currentGeneration == generation)
                { Error = "Run update delivery timed out; reconnecting with the saved snapshot."; Notify(); connection.Abort(); }
            }
            catch (Exception ex)
            {
                if (currentGeneration == generation) { Error = "Run update not delivered: " + ex.Message; Notify(); connection.Abort(); }
            }
        }

        public static async void Disconnect()
        {
            var previous = credentials;
            Stop(true);
            if (previous == null) return;
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", previous.token);
                    using (await client.DeleteAsync(new Uri(new Uri(previous.address), "api/editor/session"))) { }
                }
            }
            catch { /* Local disconnect is complete even when the server is unavailable. */ }
        }

        private static void Stop(bool clearError)
        {
            CancelBuildDelivery();
            ++generation;
            ClearRunProbe();
            CancelVersionControl(versionControlBusyConnection);
            EditorApplication.update -= ResumeAfterReload;
            lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
            socket?.Abort(); socket = null; credentials = null; needsDiscovery = false;
            SessionState.EraseString(StateKey);
            Status = "Disconnected"; if (clearError) Error = null;
            Notify();
        }
        private static void StopForReload()
        {
            CancelBuildDelivery();
            ++generation;
            ClearRunProbe();
            CancelVersionControl(versionControlBusyConnection);
            EditorApplication.update -= ResumeAfterReload;
            lifetime?.Cancel(); socket?.Abort();
            // Keep session credentials only in SessionState; never write them to the project.
        }
        private static void Notify() => Changed?.Invoke();
        private static async Task<string> Receive(ClientWebSocket connection, CancellationToken ct)
        {
            while (true)
            {
            if (!WindowsBuildExecution.Busy)
                while (DeferredBuildMessages.TryDequeue(out var deferred))
                    if (ReferenceEquals(deferred.connection, connection)) return deferred.json;
            using (var stream = new MemoryStream())
            {
                var buffer = new byte[4096]; WebSocketReceiveResult read;
                do
                {
                    read = await connection.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (read.MessageType == WebSocketMessageType.Close) return null;
                    if (read.MessageType != WebSocketMessageType.Text || stream.Length + read.Count > 65536)
                        throw new InvalidOperationException("Invalid server message.");
                    stream.Write(buffer, 0, read.Count);
                } while (!read.EndOfMessage);
                var json = Encoding.UTF8.GetString(stream.ToArray());
                // Continue receiving acknowledgements on this worker even while a
                // synchronous BuildPipeline call blocks the Unity main thread.
                if (WindowsBuildExecution.Accept(json)) continue;
                // A queued settings/source/test message must not stop the worker
                // receiver and therefore block build-log acknowledgements. Execute
                // only after the build yields the main thread, on the same socket.
                if (WindowsBuildExecution.Busy)
                {
                    if (DeferredBuildMessages.Count >= 64) throw new InvalidOperationException("Too many deferred Editor messages during a build.");
                    DeferredBuildMessages.Enqueue(new DeferredBuildMessage { connection = connection, json = json });
                    continue;
                }
                return json;
            }
            }
        }
    }
}
