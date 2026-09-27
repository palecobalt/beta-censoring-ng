using System.IO.Compression;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace BetaCensor.Web.Providers {
    /// <summary>
    /// Read-only file provider over a zip archive (sticker packs), using System.IO.Compression.
    /// Directories are the folders in the entry paths; backslashes in entry names are treated as separators.
    /// </summary>
    public class ZipArchiveFileProvider : IFileProvider {
        private readonly string _archivePath;
        private readonly Dictionary<string, ZipEntryInfo> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase) { string.Empty };

        public ZipArchiveFileProvider(string archivePath) {
            _archivePath = Path.GetFullPath(archivePath);
            using var archive = ZipFile.OpenRead(_archivePath);
            foreach (var entry in archive.Entries) {
                var path = Normalize(entry.FullName);
                if (path.Length == 0) {
                    continue;
                }
                var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                if (!isDirectory) {
                    _files[path] = new ZipEntryInfo(_archivePath, entry.FullName, path, entry.Length, entry.LastWriteTime);
                }
                var parent = isDirectory ? path : GetParent(path);
                while (parent.Length > 0 && _directories.Add(parent)) {
                    parent = GetParent(parent);
                }
            }
        }

        public IDirectoryContents GetDirectoryContents(string subpath) {
            var dir = Normalize(subpath);
            if (!_directories.Contains(dir)) {
                return NotFoundDirectoryContents.Singleton;
            }
            var subdirs = _directories.Where(d => d.Length > 0 && GetParent(d).Equals(dir, StringComparison.OrdinalIgnoreCase))
                .Select(d => (IFileInfo)new ZipDirectoryInfo(GetName(d)));
            var files = _files.Values.Where(f => GetParent(f.Path).Equals(dir, StringComparison.OrdinalIgnoreCase));
            return new VirtualDirectory(subdirs.Concat(files).ToList());
        }

        public IFileInfo GetFileInfo(string subpath) {
            var path = Normalize(subpath);
            if (_files.TryGetValue(path, out var file)) {
                return file;
            }
            return _directories.Contains(path) && path.Length > 0 ? new ZipDirectoryInfo(GetName(path)) : new NotFoundFileInfo(subpath);
        }

        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

        private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

        private static string GetParent(string path) {
            var i = path.LastIndexOf('/');
            return i < 0 ? string.Empty : path[..i];
        }

        private static string GetName(string path) => path[(path.LastIndexOf('/') + 1)..];

        private sealed class ZipEntryInfo : IFileInfo {
            private readonly string _archivePath;
            private readonly string _entryName;

            public ZipEntryInfo(string archivePath, string entryName, string path, long length, DateTimeOffset lastModified) {
                _archivePath = archivePath;
                _entryName = entryName;
                Path = path;
                Length = length;
                LastModified = lastModified;
            }

            public string Path { get; }
            public bool Exists => true;
            public long Length { get; }
            public string? PhysicalPath => null;
            public string Name => GetName(Path);
            public DateTimeOffset LastModified { get; }
            public bool IsDirectory => false;

            public Stream CreateReadStream() {
                // opened per read so concurrent requests don't share a ZipArchive
                using var archive = ZipFile.OpenRead(_archivePath);
                var entry = archive.GetEntry(_entryName) ?? throw new FileNotFoundException(_entryName, _archivePath);
                var buffer = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
                using (var stream = entry.Open()) {
                    stream.CopyTo(buffer);
                }
                buffer.Position = 0;
                return buffer;
            }
        }

        private sealed class ZipDirectoryInfo : IFileInfo {
            public ZipDirectoryInfo(string name) => Name = name;
            public bool Exists => true;
            public long Length => -1;
            public string? PhysicalPath => null;
            public string Name { get; }
            public DateTimeOffset LastModified => DateTimeOffset.MinValue;
            public bool IsDirectory => true;
            public Stream CreateReadStream() => throw new InvalidOperationException("Cannot read a directory.");
        }
    }
}
