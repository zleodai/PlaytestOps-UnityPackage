#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlaytestOps.Editor
{
    public sealed class VersionControlCommandResult
    {
        public int exitCode;
        public string stdout = "", stderr = "";
    }
    public interface IVersionControlCommandRunner
    {
        Task<VersionControlCommandResult> RunAsync(string executable, string workingDirectory, string[] arguments, CancellationToken cancellationToken);
    }
    public sealed class VersionControlReadException : Exception
    {
        public readonly string code;
        public VersionControlReadException(string code) : base(code) { this.code = code; }
    }

    // No shell, project executables, PATH lookup, credentials, or write commands.
    public sealed class VersionControlCommandRunner : IVersionControlCommandRunner
    {
        private const int MaxOutputCharacters = 4 * 1024 * 1024;
        private const int MaxErrorCharacters = 64 * 1024;
        public static string FindTrustedExecutable(string projectRoot)
        {
            // The initial process adapter is verified on Windows. Other platforms
            // require separate executable/argument-escaping acceptance evidence.
            if (Path.DirectorySeparatorChar != '\\') return "";
            foreach (var path in TrustedPaths())
            {
                if (IsTrustedExecutable(path, projectRoot)) return path;
            }
            return "";
        }
        private static string[] TrustedPaths()
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                return new[] {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PlasticSCM5", "client", "cm.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PlasticSCM5", "client", "cm.exe")
                };
            }
            return new string[0];
        }
        public static bool IsTrustedExecutable(string executable, string projectRoot)
        {
            if (string.IsNullOrEmpty(executable) || !Path.IsPathRooted(executable) || !File.Exists(executable)) return false;
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var full = Path.GetFullPath(executable);
            if (!TrustedPaths().Any(path => Path.IsPathRooted(path) && string.Equals(Path.GetFullPath(path), full, comparison))) return false;
            var project = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.StartsWith(project + Path.DirectorySeparatorChar, comparison)) return false;
            try { SourceReader.EnsureNoReparsePoints(full, false); }
            catch { return false; }
            return true;
        }
        public static bool IsReadOnlyCommand(string[] arguments)
        {
            if (arguments == null || arguments.Length == 0 || arguments.Any(arg => arg == null || arg.Any(char.IsControl) || arg.Length > 8192)) return false;
            // Exact shapes. Never accept a browser-supplied command or options.
            if (arguments[0] == "getworkspacefrompath")
                return arguments.Length == 4 && arguments[2] == "--extended" && arguments[3] == "--format={wkname}{tab}{wkpath}{tab}{guid}{tab}{type}{tab}{dynamic}";
            if (arguments[0] == "workspaceinfo") return arguments.Length == 2;
            if (arguments[0] == "showselector") return arguments.Length == 2;
            if (arguments[0] == "status")
                return arguments.Length == 5 && arguments[2] == "--xml" && arguments[3] == "--encoding=utf-8" && arguments[4] == "--iscochanged" ||
                    arguments.Length == 6 && arguments[2] == "--header" && arguments[3] == "--head" && arguments[4] == "--xml" && arguments[5] == "--encoding=utf-8" ||
                    arguments.Length == 5 && arguments[2] == "--header" && arguments[3] == "--xml" && arguments[4] == "--encoding=utf-8";
            if (arguments[0] == "find")
                return arguments.Length == 5 && arguments[1] == "changeset" && arguments[3] == "--xml" && arguments[4] == "--encoding=utf-8" &&
                    Regex.IsMatch(arguments[2], @"\A(?:where branch = '[^'"";\p{Cc}]{1,256}' )?order by changesetId desc limit 51 offset (?:0|[1-9][0-9]{0,5})\z", RegexOptions.CultureInvariant);
            if (arguments[0] == "log")
                return arguments.Length == 5 && IsChangesetSpec(arguments[1], "cs:") && arguments[2] == "--ancestors" && arguments[3] == "--xml" && arguments[4] == "--encoding=utf-8" ||
                    arguments.Length == 6 && IsChangesetSpec(arguments[1], "cs:") && IsChangesetSpec(arguments[2], "--from=cs:") &&
                    arguments[3] == "--ancestors" && arguments[4] == "--xml" && arguments[5] == "--encoding=utf-8";
            return false;
        }
        private static bool IsChangesetSpec(string text, string prefix) => text.StartsWith(prefix, StringComparison.Ordinal) &&
            long.TryParse(text.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0;
        public async Task<VersionControlCommandResult> RunAsync(string executable, string workingDirectory, string[] arguments, CancellationToken cancellationToken)
        {
            if (!IsTrustedExecutable(executable, workingDirectory) || !IsReadOnlyCommand(arguments)) throw new VersionControlReadException("unsafeCommand");
            cancellationToken.ThrowIfCancellationRequested();
            using (var process = new Process())
            using (var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                operation.CancelAfter(TimeSpan.FromSeconds(12));
                var info = new ProcessStartInfo {
                    FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true)
                };
                // Unity's .NET Standard profile lacks ArgumentList. This implements
                // the platform command-line escaping convention, not shell syntax.
                info.Arguments = string.Join(" ", arguments.Select(QuoteArgument));
                process.StartInfo = info;
                try { if (!process.Start()) throw new VersionControlReadException("clientStartFailed"); }
                catch (VersionControlReadException) { throw; }
                catch { throw new VersionControlReadException("clientStartFailed"); }
                using (operation.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } }))
                {
                    var output = ReadBounded(process.StandardOutput, MaxOutputCharacters, operation.Token);
                    var errors = ReadBounded(process.StandardError, MaxErrorCharacters, operation.Token);
                    try
                    {
                        while (!process.HasExited)
                        {
                            if (output.IsFaulted) await output.ConfigureAwait(false);
                            if (errors.IsFaulted) await errors.ConfigureAwait(false);
                            operation.Token.ThrowIfCancellationRequested(); await Task.Delay(30, operation.Token).ConfigureAwait(false);
                        }
                        var stdout = await output.ConfigureAwait(false);
                        var stderr = await errors.ConfigureAwait(false);
                        operation.Token.ThrowIfCancellationRequested();
                        return new VersionControlCommandResult { exitCode = process.ExitCode, stdout = stdout, stderr = stderr };
                    }
                    catch (Exception ex)
                    {
                        operation.Cancel();
                        // Observe both readers after terminating only this owned cm process.
                        try { await output.ConfigureAwait(false); } catch { }
                        try { await errors.ConfigureAwait(false); } catch { }
                        cancellationToken.ThrowIfCancellationRequested();
                        if (ex is VersionControlReadException) throw;
                        if (operation.IsCancellationRequested) throw new VersionControlReadException("timeout");
                        throw;
                    }
                }
            }
        }
        private static async Task<string> ReadBounded(StreamReader reader, int limit, CancellationToken cancellationToken)
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            var bytes = 0;
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // A split surrogate pair can conservatively over-count by two
                    // bytes; it cannot under-count or allow a larger raw response.
                    bytes += Encoding.UTF8.GetByteCount(buffer, 0, read);
                    if (bytes > limit || text.Length + read > limit) throw new VersionControlReadException("outputLimit");
                    text.Append(buffer, 0, read);
                }
            }
            catch (DecoderFallbackException) { throw new VersionControlReadException("invalidData"); }
            return text.ToString();
        }
        public static string QuoteArgument(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0 || value.Any(char.IsControl)) throw new VersionControlReadException("unsafeCommand");
            var quoted = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') { quoted.Append('\\', slashes * 2 + 1); quoted.Append('"'); slashes = 0; continue; }
                quoted.Append('\\', slashes); slashes = 0; quoted.Append(character);
            }
            quoted.Append('\\', slashes * 2); quoted.Append('"');
            return quoted.ToString();
        }
    }
}
