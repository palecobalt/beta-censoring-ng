using System.Security.Cryptography;

namespace CensorCore.ModelLoader;

/// <summary>
/// A model the server can download: one file at a fixed address with a known SHA-256 (the same files as
/// scripts/fetch-models.sh).
/// </summary>
public record KnownModel(string Name, string FileName, string DownloadUrl, string Sha256, string Description, string Publisher, string Page);

public static class KnownModels {
    public const string Repository = "notAI-tech/NudeNet";
    public const string ReleasePage = "https://github.com/notAI-tech/NudeNet/releases/tag/v3.4-weights";
    /// <summary>The hotscreen models, at the revision the checksums belong to.</summary>
    public const string HotscreenPage = "https://huggingface.co/Perfectfox256/hotscreen-detection-models";
    private const string HotscreenRevision = "8134f9ea866820684cdb9f022a5ce3d306bed0a2";

    // NudeNet's v3 models, from its v3.4-weights release
    public static readonly KnownModel Model640m = NudeNet("640m", 176832019, "04fe3d77980780c1f8297dc6d7f942fd5b3abe6942a188f742a85241e4f634eb", "more accurate, 104 MB, 0.3-0.7 s per image on a CPU");
    public static readonly KnownModel Model320n = NudeNet("320n", 176831997, "c15d8273adad2d0a92f014cc69ab2d6c311a06777a55545f2c4eb46f51911f0f", "faster, 12 MB, 40-90 ms per image on a CPU");

    // optional: YOLO11 models published for the HotScreen application, never downloaded unless asked for
    public static readonly KnownModel HotscreenN640 = Hotscreen("hotscreen-n640", "hs-real-y11n-640-fp32.onnx", "b2b5adff81442762a93f78cfdad4b492471c92fa4e60fc6d553afffd0dc892d5", "for photos, 10 MB, about 0.1 s per image on a CPU");
    public static readonly KnownModel HotscreenAnimeS640 = Hotscreen("hotscreen-anime-s640", "hs-real-anime-y11s-640-fp32.onnx", "bd2f67c628adb20ab2f1ffdd45c37015103f6fa6140e1a6a942dbbc3121a44a9", "for photos and drawings, 36 MB, about 0.2 s per image on a CPU");

    /// <summary>In order of preference when nothing is configured.</summary>
    public static readonly IReadOnlyList<KnownModel> All = new[] { Model640m, Model320n, HotscreenN640, HotscreenAnimeS640 };

    public static KnownModel Default => Model640m;

    public static KnownModel? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(m => m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) || m.FileName.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    // the API asset address: direct release downloads can redirect to a login page
    private static KnownModel NudeNet(string name, long assetId, string sha256, string description) =>
        new(name, name + ".onnx", $"https://api.github.com/repos/{Repository}/releases/assets/{assetId}", sha256, description, "NudeNet", ReleasePage);

    private static KnownModel Hotscreen(string name, string fileName, string sha256, string description) =>
        new(name, fileName, $"{HotscreenPage}/resolve/{HotscreenRevision}/yolo-07-2025/{fileName}", sha256, description, "hotscreen", $"{HotscreenPage}/tree/main/yolo-07-2025");

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
