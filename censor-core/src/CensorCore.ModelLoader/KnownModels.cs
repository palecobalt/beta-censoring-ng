using System.Security.Cryptography;

namespace CensorCore.ModelLoader;

/// <summary>
/// NudeNet's v3 models, from its v3.4-weights release (the same assets as scripts/fetch-models.sh).
/// </summary>
public record KnownModel(string Name, long AssetId, string Sha256, string Description) {
    public string FileName => Name + ".onnx";
}

public static class KnownModels {
    public const string Repository = "notAI-tech/NudeNet";
    public const string ReleasePage = "https://github.com/notAI-tech/NudeNet/releases/tag/v3.4-weights";

    public static readonly KnownModel Model640m = new("640m", 176832019, "04fe3d77980780c1f8297dc6d7f942fd5b3abe6942a188f742a85241e4f634eb", "more accurate, 104 MB, 0.3-0.7 s per image on a CPU");
    public static readonly KnownModel Model320n = new("320n", 176831997, "c15d8273adad2d0a92f014cc69ab2d6c311a06777a55545f2c4eb46f51911f0f", "faster, 12 MB, 40-90 ms per image on a CPU");

    /// <summary>In order of preference when nothing is configured.</summary>
    public static readonly IReadOnlyList<KnownModel> All = new[] { Model640m, Model320n };

    public static KnownModel Default => Model640m;

    public static KnownModel? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(m => m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) || m.FileName.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string DownloadUrl(KnownModel model) => $"https://api.github.com/repos/{Repository}/releases/assets/{model.AssetId}";

    /// <summary>Throws <see cref="ModelChecksumException"/> when the data isn't the model.</summary>
    public static void Verify(KnownModel model, byte[] data) {
        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (hash != model.Sha256) {
            throw new ModelChecksumException($"the downloaded {model.FileName} has SHA-256 {hash}, expected {model.Sha256}");
        }
    }
}

/// <summary>
/// The downloaded model isn't the expected file. Nothing else is downloaded in its place.
/// </summary>
public class ModelChecksumException : Exception {
    public ModelChecksumException(string message) : base(message) { }
}
