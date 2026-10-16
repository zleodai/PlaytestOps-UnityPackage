#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace PlaytestOps.Editor
{
    // Pure C#: the Editor connection supplies its own project path on the main
    // thread. No browser paths, query strings, credentials or writable commands.
    public sealed class CliVersionControlReader
    {
        public const int MaxResponseBytes = 1024 * 1024;
        public const int MaxCliOutputCharacters = 4 * 1024 * 1024;
        public const int MaxPendingItems = 2000, MaxIncomingChangesets = 200, HistoryPageSize = 50;
        public const string WorkspaceFormat = "--format={wkname}{tab}{wkpath}{tab}{guid}{tab}{type}{tab}{dynamic}";
        private readonly string projectRoot, executable;
        private readonly IVersionControlCommandRunner runner;

        private sealed class WorkspaceIdentity
        {
            public string name, path, guid, type, dynamic;
            public string Key => name + "\n" + path + "\n" + guid + "\n" + type + "\n" + dynamic;
        }
        private sealed class StatusIdentity
        {
            public string repository, server, configType, configName, branch;
            public long changeset;
            public string Key => repository + "\n" + server + "\n" + configType + "\n" + configName + "\n" + changeset;
        }
        public CliVersionControlReader(string projectRoot, string executable, IVersionControlCommandRunner runner = null)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Path.IsPathRooted(projectRoot)) throw new VersionControlReadException("invalidProject");
            this.projectRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (this.projectRoot.Length == 0 || string.Equals(this.projectRoot, Path.GetPathRoot(this.projectRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new VersionControlReadException("invalidProject");
            SourceReader.EnsureNoReparsePoints(this.projectRoot, false);
            if (!Directory.Exists(this.projectRoot)) throw new VersionControlReadException("invalidProject");
            this.executable = executable ?? "";
            this.runner = runner ?? new VersionControlCommandRunner();
        }
        public async Task<VersionControlSnapshot> ReadAsync(int offset = 0, string scope = "repository", CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset < 0 || offset > 100000 || (scope != "repository" && scope != "branch"))
                return VersionControlSnapshot.Failure("failed", "invalidRequest");
            if (string.IsNullOrEmpty(executable)) return VersionControlSnapshot.Failure("missingClient", "missingClient", offset, scope);
            using (var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                operation.CancelAfter(TimeSpan.FromSeconds(25));
                var ct = operation.Token;
                try
                {
                    var workspace = ParseWorkspace(await Run(ct, "getworkspacefrompath", projectRoot, "--extended", WorkspaceFormat));
                    EnsureProjectInWorkspace(workspace.path);
                    var status = ParseStatus(await Run(ct, "status", workspace.path, "--header", "--xml", "--encoding=utf-8"));
                    var snapshot = new VersionControlSnapshot {
                        state = "available", capturedAtUtc = UtcNow(),
                        workspace = new VersionControlWorkspace { name = Metadata(workspace.name, 256), repository = Metadata(status.repository, 256),
                            branch = Metadata(status.branch, 256), selectorType = Metadata(status.configType.ToLowerInvariant(), 256),
                            currentChangeset = status.changeset, headChangeset = -1, workspaceType = workspace.type },
                        history = new VersionControlHistory { offset = offset, scope = scope }
                    };
                    try
                    {
                        var pendingXml = await Run(ct, "status", workspace.path, "--xml", "--encoding=utf-8", "--iscochanged");
                        snapshot.pending = ParsePending(pendingXml);
                    }
                    catch (VersionControlReadException ex) { snapshot.pending.errorCode = ex.code; }

                    StatusIdentity head = null;
                    try
                    {
                        head = ParseStatus(await Run(ct, "status", workspace.path, "--header", "--head", "--xml", "--encoding=utf-8"));
                        if (head.repository != status.repository || head.server != status.server || head.configName != status.configName || head.configType != status.configType)
                            throw new VersionControlReadException("workspaceChanged");
                        snapshot.workspace.headChangeset = head.changeset;
                    }
                    catch (VersionControlReadException ex) { snapshot.incoming.errorCode = ex.code; }

                    try
                    {
                        // Do not spend a second network timeout after a known remote
                        // failure. Preserve the independent local pending snapshot.
                        if (head == null && (snapshot.incoming.errorCode == "serverUnavailable" || snapshot.incoming.errorCode == "authenticationRequired" || snapshot.incoming.errorCode == "timeout"))
                            throw new VersionControlReadException(snapshot.incoming.errorCode);
                        if (scope == "branch" && string.IsNullOrEmpty(status.branch)) throw new VersionControlReadException("unsupportedSelector");
                        var filter = scope == "branch" ? "where branch = " + QueryLiteral(status.branch) + " " : "";
                        var query = filter + "order by changesetId desc limit 51 offset " + offset.ToString(CultureInfo.InvariantCulture);
                        var all = ParseChangesets(await Run(ct, "find", "changeset", query, "--xml", "--encoding=utf-8"), false);
                        if (scope == "branch" && all.Any(item => item.branch != status.branch)) throw new VersionControlReadException("invalidData");
                        snapshot.history = new VersionControlHistory { state = "available", offset = offset, scope = scope,
                            hasMore = all.Count > HistoryPageSize, changesets = all.Take(HistoryPageSize).Select(HistoryEntry).ToArray() };
                    }
                    catch (VersionControlReadException ex) { snapshot.history.errorCode = ex.code; }

                    if (workspace.type != "regular" || workspace.dynamic != "static" || status.configType != "Branch")
                        snapshot.incoming = new VersionControlIncoming { state = "unsupported", errorCode = "unsupportedWorkspace" };
                    else if (head != null)
                    {
                        try { snapshot.incoming = await ReadIncoming(status, head, ct); }
                        catch (VersionControlReadException ex) { snapshot.incoming.errorCode = ex.code; }
                    }
                    // A branch switch, check-in or workspace replacement during any
                    // query invalidates the whole response rather than mixing identities.
                    var finalStatus = ParseStatus(await Run(ct, "status", workspace.path, "--header", "--xml", "--encoding=utf-8"));
                    var finalWorkspace = ParseWorkspace(await Run(ct, "getworkspacefrompath", projectRoot, "--extended", WorkspaceFormat));
                    if (finalStatus.Key != status.Key || finalWorkspace.Key != workspace.Key)
                        return VersionControlSnapshot.Failure("failed", "workspaceChanged", offset, scope);
                    snapshot.capturedAtUtc = UtcNow();
                    return snapshot;
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return VersionControlSnapshot.Failure("failed", "timeout", offset, scope);
                }
                catch (VersionControlReadException ex)
                {
                    return VersionControlSnapshot.Failure(ex.code == "notUvcs" ? "notUvcs" : "failed", ex.code, offset, scope);
                }
                catch { return VersionControlSnapshot.Failure("failed", "invalidData", offset, scope); }
            }
        }
        private async Task<VersionControlIncoming> ReadIncoming(StatusIdentity loaded, StatusIdentity head, CancellationToken ct)
        {
            if (loaded.changeset == head.changeset) return new VersionControlIncoming { state = "available" };
            if (loaded.changeset > head.changeset || head.changeset < 0) throw new VersionControlReadException("ancestryUnknown");
            var fullHead = "cs:" + head.changeset.ToString(CultureInfo.InvariantCulture);
            var fromLoaded = "--from=cs:" + loaded.changeset.ToString(CultureInfo.InvariantCulture);
            // The interval intentionally omits the lower bound. First a bounded,
            // read-only ancestry query proves it was actually reachable, including 0.
            try
            {
                var ancestors = ParseChangesets(await Run(ct, "log", fullHead, "--ancestors", "--xml", "--encoding=utf-8"), true);
                if (!ancestors.Any(item => item.number == loaded.changeset)) throw new VersionControlReadException("ancestryUnknown");
            }
            catch (VersionControlReadException) { throw new VersionControlReadException("ancestryUnknown"); }
            var incoming = ParseChangesets(await Run(ct, "log", fullHead, fromLoaded, "--ancestors", "--xml", "--encoding=utf-8"), true);
            if (!incoming.Any(item => item.number == head.changeset && item.branch == loaded.branch) || incoming.Any(item => item.number <= loaded.changeset || item.number > head.changeset))
                throw new VersionControlReadException("ancestryUnknown");
            var result = new List<VersionControlChangeset>();
            var bytes = 0;
            var truncated = false;
            foreach (var entry in incoming.Where(item => item.branch == loaded.branch).OrderByDescending(item => item.number))
            {
                var size = EntryBytes(entry);
                if (result.Count >= MaxIncomingChangesets || bytes + size > 240000) { truncated = true; break; }
                result.Add(entry); bytes += size;
            }
            return new VersionControlIncoming { state = "available", changesets = result.ToArray(), truncated = truncated };
        }
        private async Task<string> Run(CancellationToken ct, params string[] arguments)
        {
            ct.ThrowIfCancellationRequested();
            if (!VersionControlCommandRunner.IsReadOnlyCommand(arguments)) throw new VersionControlReadException("unsafeCommand");
            var result = await runner.RunAsync(executable, projectRoot, arguments, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result == null || result.stdout == null || result.stdout.Length > MaxCliOutputCharacters || result.stderr == null || result.stderr.Length > 65536)
                throw new VersionControlReadException("outputLimit");
            if (result.exitCode != 0)
            {
                // Classify only; never reflect CLI diagnostics/credentials or paths.
                var text = (result.stderr + "\n" + result.stdout).ToLowerInvariant();
                if (arguments[0] == "getworkspacefrompath" && (text.Contains("not in a workspace") || text.Contains("no workspace") || text.Contains("not a workspace")))
                    throw new VersionControlReadException("notUvcs");
                if (text.Contains("locked workspace") || text.Contains("operation has locked") || text.Contains("workspace is locked")) throw new VersionControlReadException("workspaceBusy");
                if (text.Contains("authentication") || text.Contains("token") || text.Contains("login") || text.Contains("permission") || text.Contains("not authorized")) throw new VersionControlReadException("authenticationRequired");
                if (text.Contains("connect") || text.Contains("network") || text.Contains("server") || text.Contains("timeout")) throw new VersionControlReadException("serverUnavailable");
                throw new VersionControlReadException("commandFailed");
            }
            return result.stdout;
        }
        private WorkspaceIdentity ParseWorkspace(string text)
        {
            var fields = text.TrimEnd('\r', '\n').Split('\t');
            if (fields.Length != 5 || fields.Any(field => string.IsNullOrEmpty(field) || field.Length > 1024 || field.Any(char.IsControl)) || !Path.IsPathRooted(fields[1]))
                throw new VersionControlReadException("invalidWorkspace");
            var path = Path.GetFullPath(fields[1]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            SourceReader.EnsureNoReparsePoints(path, false);
            if ((fields[3] != "regular" && fields[3] != "partial") || (fields[4] != "static" && fields[4] != "dynamic"))
                throw new VersionControlReadException("invalidWorkspace");
            return new WorkspaceIdentity { name = fields[0], path = path, guid = fields[2], type = fields[3].ToLowerInvariant(), dynamic = fields[4].ToLowerInvariant() };
        }
        private void EnsureProjectInWorkspace(string workspacePath)
        {
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(projectRoot, workspacePath, comparison) && !projectRoot.StartsWith(workspacePath + Path.DirectorySeparatorChar, comparison))
                throw new VersionControlReadException("invalidWorkspace");
        }
        private static StatusIdentity ParseStatus(string xml)
        {
            var root = Xml(xml).Root;
            if (root == null || root.Name.LocalName != "StatusOutput") throw new VersionControlReadException("invalidData");
            var status = root.Element("WorkspaceStatus")?.Element("Status");
            var repository = Required(status?.Element("RepSpec"), "Name");
            var server = Required(status?.Element("RepSpec"), "Server");
            var number = Number(Required(status, "Changeset"));
            var configType = Required(root, "WkConfigType");
            var config = Required(root, "WkConfigName");
            var branch = "";
            if (configType == "Branch")
            {
                var suffix = "@" + repository + "@" + server;
                if (!config.EndsWith(suffix, StringComparison.Ordinal) || config.Length <= suffix.Length) throw new VersionControlReadException("invalidWorkspace");
                branch = config.Substring(0, config.Length - suffix.Length);
                if (!branch.StartsWith("/", StringComparison.Ordinal)) throw new VersionControlReadException("invalidWorkspace");
            }
            return new StatusIdentity { repository = repository, server = server, changeset = number, configType = configType, configName = config, branch = branch };
        }
        public static VersionControlPending ParsePending(string xml)
        {
            var root = Xml(xml).Root;
            if (root == null || root.Name.LocalName != "StatusOutput") throw new VersionControlReadException("invalidData");
            var items = new List<VersionControlPendingItem>();
            var bytes = 0;
            var truncated = false;
            foreach (var change in root.Descendants("Change"))
            {
                var entry = new VersionControlPendingItem { status = Clip(Required(change, "Type"), 256), path = PendingPath(Required(change, "Path"), false),
                    oldPath = PendingPath(change.Element("OldPath")?.Value ?? "", true) };
                var size = StringBytes(entry.status) + StringBytes(entry.path) + StringBytes(entry.oldPath) + 64;
                if (items.Count >= MaxPendingItems || bytes + size > 320000) { truncated = true; break; }
                items.Add(entry); bytes += size;
            }
            return new VersionControlPending { state = "available", items = items.ToArray(), truncated = truncated };
        }
        public static List<VersionControlChangeset> ParseChangesets(string xml, bool log)
        {
            var root = Xml(xml).Root;
            if (root == null || root.Name.LocalName != (log ? "LogList" : "PLASTICQUERY")) throw new VersionControlReadException("invalidData");
            var items = new List<VersionControlChangeset>();
            var seen = new HashSet<long>();
            foreach (var item in root.Elements(log ? "Changeset" : "CHANGESET"))
            {
                var number = Number(Required(item, log ? "ChangesetId" : "CHANGESETID"));
                if (!seen.Add(number)) throw new VersionControlReadException("invalidData");
                var dateText = Required(item, log ? "Date" : "DATE");
                var hasOffset = dateText.EndsWith("Z", StringComparison.Ordinal) || dateText.Length >= 6 &&
                    (dateText[dateText.Length - 6] == '+' || dateText[dateText.Length - 6] == '-') && dateText[dateText.Length - 3] == ':';
                if (!hasOffset || !DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new VersionControlReadException("invalidData");
                items.Add(new VersionControlChangeset { number = number, dateUtc = date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
                    author = Metadata(Required(item, log ? "Owner" : "OWNER"), 256), branch = Metadata(Required(item, log ? "Branch" : "BRANCH"), 256),
                    comment = Clip(item.Element(log ? "Comment" : "COMMENT")?.Value ?? "", 4096) });
                if (items.Count > 10000) throw new VersionControlReadException("outputLimit");
            }
            return items;
        }
        private static VersionControlChangeset HistoryEntry(VersionControlChangeset item) => new VersionControlChangeset {
            number = item.number, dateUtc = item.dateUtc, author = item.author, branch = item.branch, comment = Clip(item.comment, 1024)
        };
        private static XDocument Xml(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxCliOutputCharacters) throw new VersionControlReadException("invalidData");
            try
            {
                using (var input = new StringReader(text))
                using (var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = MaxCliOutputCharacters, MaxCharactersFromEntities = 1024 }))
                    return XDocument.Load(reader, LoadOptions.None);
            }
            catch { throw new VersionControlReadException("invalidData"); }
        }
        private static string Required(XElement element, string name)
        {
            var value = element?.Element(name)?.Value;
            if (string.IsNullOrEmpty(value) || value.Length > 8192 || value.IndexOf('\0') >= 0) throw new VersionControlReadException("invalidData");
            return value;
        }
        private static long Number(string text)
        {
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0) throw new VersionControlReadException("invalidData");
            return value;
        }
        private static string QueryLiteral(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 256 || text.Any(char.IsControl) || text.IndexOfAny(new[] { '\'', '"', ';' }) >= 0)
                throw new VersionControlReadException("unsupportedSelector");
            return "'" + text + "'";
        }
        private static string PendingPath(string text, bool allowEmpty)
        {
            if (allowEmpty && string.IsNullOrEmpty(text)) return "";
            var path = text.Replace('\\', '/');
            if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.StartsWith("/", StringComparison.Ordinal) || path.IndexOf(':') >= 0 ||
                path.Any(char.IsControl) || path.Split('/').Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
                throw new VersionControlReadException("invalidData");
            return path;
        }
        private static string Metadata(string text, int limit)
        {
            if (text.Any(char.IsControl)) throw new VersionControlReadException("invalidData");
            return Clip(text, limit);
        }
        private static string UtcNow() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        public static string Clip(string text, int length)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length <= length) return text;
            var cut = length;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            return text.Substring(0, cut);
        }
        private static int EntryBytes(VersionControlChangeset entry) => StringBytes(entry.dateUtc) + StringBytes(entry.author) + StringBytes(entry.comment) + StringBytes(entry.branch) + 128;
        private static int StringBytes(string text) => Encoding.UTF8.GetByteCount(text) + text.Count(character => character == '"' || character == '\\') +
            text.Count(character => character < 32) * 5;
    }
}
