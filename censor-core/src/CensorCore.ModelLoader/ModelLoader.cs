using System.Reflection;

namespace CensorCore.ModelLoader;

public class ModelLoader {
    private readonly List<string> _searchPaths;
    private readonly List<Assembly> _searchAssemblies;
    private readonly ModelLoaderOptions _options;

    /// <summary>
    /// Where the model returned by <see cref="GetModel"/> came from: a file path, a download, or an embedded resource.
    /// </summary>
    public string? Source { get; private set; }

    public ModelLoader(List<string> searchPaths, List<Assembly> searchAssemblies, ModelLoaderOptions opts)
    {
        this._searchPaths = searchPaths;
        this._searchAssemblies = searchAssemblies;
        this._options = opts;
    }

    public async Task<byte[]?> GetModel(string? filePath = null) {
        var local = await GetLocalModel(filePath);
        if (local == null) {
            var dl = await DownloadModel(true);
            if (dl.HasValue) {
                local = dl.Value.ModelData;
            }
        }
        return local;
    }

    public async Task<byte[]?> GetLocalModel(string? filePath) {
        if (filePath != null && File.Exists(filePath) && Path.GetExtension(filePath) == ".onnx") {
            Source = Path.GetFullPath(filePath);
            return await File.ReadAllBytesAsync(filePath);
        }
        var directories = new List<string>();
        if (filePath != null && Directory.Exists(filePath)) {
            directories.Add(filePath);
        }
        directories.AddRange(_searchPaths);
        var local = FindModel(directories.Where(Directory.Exists).Distinct().Select(d => new DirectoryInfo(d)));
        if (local != null) {
            Source = local.FullName;
            return await File.ReadAllBytesAsync(local.FullName);
        }
        // Check for an embedded model.
        var embedded = GetModelResource(Assembly.GetEntryAssembly());
        if (embedded != null) {
            Source = "embedded resource";
            return embedded;
        }
        return null;
    }

    /// <summary>
    /// Picks the model file to use from the .onnx files in the directories (earlier directories first): the
    /// configured model only, or else the known models in order of preference, then any other by name.
    /// </summary>
    public FileInfo? FindModel(IEnumerable<DirectoryInfo> directories) {
        var files = directories.SelectMany(d => d.GetFiles("*.onnx")).ToList();
        if (_options.GetClassifier) {
            return files.FirstOrDefault(f => f.Name.StartsWith("classifier_"));
        }
        if (_options.Model != null) {
            return files.FirstOrDefault(f => f.Name.Equals(_options.Model.FileName, StringComparison.OrdinalIgnoreCase));
        }
        var candidates = files.Where(f => !f.Name.StartsWith("classifier_") && (_options.PreferBaseModel ? f.Name.Contains("_base_") : !f.Name.Contains("_base")));
        foreach (var known in KnownModels.All) {
            if (candidates.FirstOrDefault(f => f.Name.Equals(known.FileName, StringComparison.OrdinalIgnoreCase)) is { } file) {
                return file;
            }
        }
        return candidates.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    public async Task<(string FileName, byte[] ModelData)?> DownloadModel(bool saveToSharedLocation = false) {
        var client = new RepositoryDownloadClient(_options.RepositorySlug);
        (string FileName, byte[] ModelData)? model = null;
        var known = _options.Model ?? KnownModels.Default;
        // only NudeNet's models can also be found by searching its releases
        var fromReleases = known.Publisher == "NudeNet";
        if (!fromReleases || (!_options.GetClassifier && !_options.PreferBaseModel && _options.RepositorySlug == KnownModels.Repository)) {
            Console.WriteLine($"Downloading {known.Publisher}'s {known.FileName} ({known.Description.Split(',')[1].Trim()})...");
            try {
                model = await RepositoryDownloadClient.DownloadKnownModel(known);
            } catch (Exception e) when (fromReleases && e is not ModelChecksumException) {
                Console.WriteLine($"WARN: downloading {known.FileName} failed ({e.Message}), searching the releases instead");
            }
        }
        if (model == null) {
            model = await client.DownloadModel(_options.GetClassifier, _options.PreferBaseModel);
            if (model != null) {
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(model.Value.ModelData)).ToLowerInvariant();
                Console.WriteLine($"WARN: using {model.Value.FileName} from the newest release of {_options.RepositorySlug}, which has no known checksum (SHA-256 {hash})");
            }
        }
        if (model != null) {
            Source = $"a download of {model.Value.FileName}";
        }
        if (model != null && saveToSharedLocation) {
            foreach (var directory in new[] { _options.DownloadDirectory, Path.Combine(Path.GetTempPath(), ".nudenet") }) {
                if (string.IsNullOrWhiteSpace(directory)) {
                    continue;
                }
                try {
                    Directory.CreateDirectory(directory);
                    // written under another name first, so an interrupted write never leaves a partial model to be found later
                    var target = Path.Combine(directory, model.Value.FileName);
                    await File.WriteAllBytesAsync(target + ".part", model.Value.ModelData);
                    File.Move(target + ".part", target, overwrite: true);
                    Source = target;
                    break;
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    Console.WriteLine($"WARN: could not save the model in {directory} ({e.Message})");
                }
            }
        }
        return model;
    }

    private static byte[]? GetModelResource(Assembly? assembly = null) {

        var entryAssembly = assembly ?? typeof(ModelLoader).Assembly;
        var model = entryAssembly.GetManifestResourceNames();
        //TODO: this doesn't match right
        if (model != null && model.Any() && model.FirstOrDefault(r => r.EndsWith(".onnx")) is var modelResource && modelResource != null) {
            // Console.WriteLine($"reading stream from {modelResource}");
            using var resourceStream = entryAssembly.GetManifestResourceStream(modelResource);
            if (resourceStream != null && resourceStream.CanRead) {
                using var ms = new MemoryStream();
                resourceStream.CopyTo(ms);
                var modelBytes = ms.ToArray();
                if (modelBytes != null && modelBytes.Length > 0) {
                    return modelBytes;
                }
            }
        }
        return null;
    }
}
