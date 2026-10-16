#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PlaytestOps.Editor
{
    [Serializable] public sealed class SourceFileEntry { public string path; public long byteLength; }
    [Serializable] public sealed class SourceListResult { public string[] roots; public SourceFileEntry[] files; public bool truncated; }
    [Serializable] public sealed class SourceReadResult
    {
        public string path; public long byteLength; public string content; public string sha256; public string lastModifiedUtc;
    }

    // Deliberately independent of Unity APIs, for filesystem security regression tests.
    // This class has no write operations and never follows a linked directory or file.
    public sealed class SourceReader
    {
        public const int MaxRoots = 16;
        public const int MaxPathCharacters = 512;
        public const int MaxFiles = 1000;
        public const int MaxFileBytes = 128 * 1024;
        public const int MaxResponseBytes = 1024 * 1024;
        private const int MaxScannedEntries = 20000;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public static readonly string[] DefaultRoots = { "Assets/Scripts", "Assets/Tests" };
        public static readonly string[] ExcludedFolders = {
            "Plugins", "Samples", "Samples~", "TutorialInfo", "ThirdParty", "Third-Party", "Vendor", "External", "Templates", "Template", "Examples", "Example",
            "Packages", "Library", "PackageCache", "ProjectSettings", "UserSettings", "Temp", "obj", "Logs",
            ".git", ".svn", "Standard Assets"
        };
        private static readonly HashSet<string> Excluded = new HashSet<string>(ExcludedFolders, StringComparer.OrdinalIgnoreCase);
        private static StringComparison PathComparison => Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static StringComparer PathComparer => Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private readonly string projectRoot;
        private readonly string[] roots;

        public SourceReader(string projectRoot, IEnumerable<string> configuredRoots = null)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Path.IsPathRooted(projectRoot))
                throw new InvalidOperationException("The Unity project folder is invalid.");
            var fullRoot = Path.GetFullPath(projectRoot);
            var trimmedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmedRoot, Path.GetPathRoot(fullRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison))
                throw new InvalidOperationException("A Unity project must use a dedicated project folder, not a filesystem root.");
            this.projectRoot = trimmedRoot;
            EnsureNoReparsePoints(this.projectRoot, false);
            if (!Directory.Exists(this.projectRoot)) throw new InvalidOperationException("The Unity project folder does not exist.");
            roots = (configuredRoots ?? DefaultRoots).ToArray();
            if (roots.Length > MaxRoots) throw new InvalidOperationException("Configure at most 16 source folders.");
            var seen = new HashSet<string>(PathComparer);
            foreach (var root in roots)
            {
                ValidateRelativePath(root);
                if (!root.StartsWith("Assets/", StringComparison.Ordinal) || root.Split('/').Length < 2)
                    throw new InvalidOperationException("Source folders must be explicit author folders inside Assets, not Assets itself.");
                if (!seen.Add(root)) throw new InvalidOperationException("Source folder entries must not be duplicated.");
                var absolute = Resolve(root);
                EnsureNoReparsePoints(absolute, true);
                if (File.Exists(absolute)) throw new InvalidOperationException("A configured source folder is a file.");
            }
        }

        public SourceListResult List(CancellationToken cancellationToken = default(CancellationToken))
        {
            var files = new List<SourceFileEntry>();
            var seen = new HashSet<string>(PathComparer);
            var scanned = 0;
            var estimatedJsonBytes = 256 + roots.Sum(root => root.Length * 6 + 8);
            var truncated = false;
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var absoluteRoot = Resolve(root);
                EnsureNoReparsePoints(absoluteRoot, true);
                if (!Directory.Exists(absoluteRoot)) continue;
                var pending = new Stack<string>();
                pending.Push(absoluteRoot);
                while (pending.Count > 0 && !truncated)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var directory = pending.Pop();
                    EnsureNoReparsePoints(directory, false);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++scanned > MaxScannedEntries) { truncated = true; break; }
                        var relative = Relative(entry);
                        try { ValidateRelativePath(relative); }
                        catch (InvalidOperationException) { continue; }
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                        if (!relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !seen.Add(relative)) continue;
                        EnsureNoReparsePoints(entry, false);
                        var length = new FileInfo(entry).Length;
                        // Advertise oversized files as metadata; Read still refuses their
                        // contents, and the dashboard can show an explicit size warning.
                        var descriptorBudget = relative.Length * 6 + 80;
                        if (files.Count >= MaxFiles || estimatedJsonBytes + descriptorBudget > MaxResponseBytes)
                        { truncated = true; break; }
                        files.Add(new SourceFileEntry { path = relative, byteLength = length });
                        estimatedJsonBytes += descriptorBudget;
                    }
                }
                if (truncated) break;
            }
            return new SourceListResult { roots = (string[])roots.Clone(), files = files.OrderBy(file => file.path, StringComparer.Ordinal).ToArray(), truncated = truncated };
        }

        public SourceReadResult Read(string path, CancellationToken cancellationToken = default(CancellationToken))
        {
            ValidateRelativePath(path);
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                !roots.Any(root => path.StartsWith(root + "/", PathComparison)))
                throw new InvalidOperationException("This file is outside the configured C# source folders.");
            var absolute = Resolve(path);
            EnsureNoReparsePoints(absolute, false);
            var attributes = File.GetAttributes(absolute);
            if ((attributes & FileAttributes.Directory) != 0) throw new InvalidOperationException("Select a C# file, not a folder.");
            cancellationToken.ThrowIfCancellationRequested();
            var modified = File.GetLastWriteTimeUtc(absolute);
            byte[] bytes;
            using (var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaxFileBytes) throw new InvalidOperationException("This source file exceeds 128 KiB.");
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (output.Length + count > MaxFileBytes) throw new InvalidOperationException("This source file exceeds 128 KiB.");
                        output.Write(buffer, 0, count);
                    }
                    bytes = output.ToArray();
                }
                EnsureNoReparsePoints(absolute, false);
                if (File.GetLastWriteTimeUtc(absolute) != modified || stream.Length != bytes.Length)
                    throw new InvalidOperationException("The source file changed while being read. Refresh and try again.");
            }
            string content;
            try { content = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { throw new InvalidOperationException("The source file is not valid UTF-8 text."); }
            if (content.IndexOf('\0') >= 0) throw new InvalidOperationException("The source file contains binary data, not C# text.");
            string hash;
            using (var algorithm = SHA256.Create()) hash = BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            return new SourceReadResult { path = path, byteLength = bytes.LongLength, content = content, sha256 = hash,
                lastModifiedUtc = modified.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture) };
        }

        public static void ValidateRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > MaxPathCharacters || Path.IsPathRooted(path) ||
                path.IndexOf('\\') >= 0 || path.Any(char.IsControl))
                throw new InvalidOperationException("Use a canonical project-relative path with forward slashes (at most 512 characters).");
            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment != segment.Trim() ||
                    segment.EndsWith(".", StringComparison.Ordinal) || segment.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0 || Excluded.Contains(segment))
                    throw new InvalidOperationException("This path contains an excluded or unsafe folder or name.");
            }
        }

        private string Resolve(string relative)
        {
            var absolute = Path.GetFullPath(Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!absolute.StartsWith(projectRoot + Path.DirectorySeparatorChar, PathComparison))
                throw new InvalidOperationException("The file is outside the Unity project.");
            return absolute;
        }

        private string Relative(string absolute)
        {
            if (!absolute.StartsWith(projectRoot + Path.DirectorySeparatorChar, PathComparison))
                throw new InvalidOperationException("The file is outside the Unity project.");
            return absolute.Substring(projectRoot.Length + 1).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        internal static void EnsureNoReparsePoints(string absolutePath, bool allowMissing)
        {
            var full = Path.GetFullPath(absolutePath);
            var root = Path.GetPathRoot(full);
            var current = root;
            foreach (var segment in full.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { if (allowMissing) return; throw new InvalidOperationException("The source file or folder no longer exists."); }
                catch (DirectoryNotFoundException) { if (allowMissing) return; throw new InvalidOperationException("The source file or folder no longer exists."); }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked files and folders are not exposed by the source browser.");
            }
        }
    }
}
