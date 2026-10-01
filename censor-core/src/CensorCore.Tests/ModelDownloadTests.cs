using CensorCore.ModelLoader;
using Xunit;

namespace CensorCore.Tests;

public class ModelDownloadTests : IDisposable {
    private readonly DirectoryInfo _first = Directory.CreateTempSubdirectory("models-");
    private readonly DirectoryInfo _second = Directory.CreateTempSubdirectory("models-");

    public void Dispose() {
        _first.Delete(true);
        _second.Delete(true);
    }

    private static ModelLoader.ModelLoader Loader(string? model = null) {
        var builder = new ModelLoaderBuilder().UseModel(model);
        return builder.Build();
    }

    private void Put(DirectoryInfo dir, params string[] names) {
        foreach (var name in names) {
            File.WriteAllBytes(Path.Combine(dir.FullName, name), new byte[] { 1 });
        }
    }

    [Fact]
    public void AWrongChecksumIsAnError() {
        Assert.Throws<ModelChecksumException>(() => KnownModels.Verify(KnownModels.Model640m, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Prefers640mThen320nThenOthersByName() {
        Put(_first, "zzz.onnx", "abc.onnx");
        Assert.Equal("abc.onnx", Loader().FindModel(new[] { _first })!.Name);
        Put(_first, "320n.onnx");
        Assert.Equal("320n.onnx", Loader().FindModel(new[] { _first })!.Name);
        Put(_first, "640m.onnx");
        Assert.Equal("640m.onnx", Loader().FindModel(new[] { _first })!.Name);
    }

    [Fact]
    public void AKnownModelInALaterFolderBeatsAnUnknownOneInAnEarlierFolder() {
        // an existing install: 320n cached in the temp folder, nothing next to the server
        Put(_first, "custom.onnx");
        Put(_second, "320n.onnx");

        Assert.Equal("320n.onnx", Loader().FindModel(new[] { _first, _second })!.Name);
    }

    [Fact]
    public void AConfiguredModelIsTheOnlyOneAccepted() {
        Put(_first, "640m.onnx");

        Assert.Null(Loader("320n").FindModel(new[] { _first }));
        Put(_first, "320n.onnx");
        Assert.Equal("320n.onnx", Loader("320n").FindModel(new[] { _first })!.Name);
        Assert.Equal("640m.onnx", Loader("640m.onnx").FindModel(new[] { _first })!.Name);
    }

    [Fact]
    public void AHotscreenModelIsFoundByItsPublishedFileName() {
        Put(_first, "640m.onnx", "hs-real-y11n-640-fp32.onnx");

        Assert.Equal("hs-real-y11n-640-fp32.onnx", Loader("hotscreen-n640").FindModel(new[] { _first })!.Name);
        Assert.Equal("hs-real-y11n-640-fp32.onnx", Loader("hs-real-y11n-640-fp32.onnx").FindModel(new[] { _first })!.Name);
        Assert.Null(Loader("hotscreen-anime-s640").FindModel(new[] { _first }));
        // not configured: NudeNet's models stay first
        Assert.Equal("640m.onnx", Loader().FindModel(new[] { _first })!.Name);
    }

    [Fact]
    public void EveryKnownModelHasAPinnedAddressAndChecksum() {
        Assert.Equal(KnownModels.Model640m, KnownModels.Default);
        Assert.All(KnownModels.All, model => {
            Assert.StartsWith("https://", model.DownloadUrl);
            Assert.Matches("^[0-9a-f]{64}$", model.Sha256);
            Assert.EndsWith(".onnx", model.FileName);
            // the size is shown while downloading
            Assert.EndsWith("MB", model.Description.Split(',')[1].Trim());
        });
        Assert.DoesNotContain("/main/", KnownModels.HotscreenN640.DownloadUrl);
        // only NudeNet's own models may be replaced by whatever its newest release has
        Assert.True(KnownModels.Model640m.InNudeNetReleases);
        Assert.True(KnownModels.Model320n.InNudeNetReleases);
        Assert.False(KnownModels.HotscreenN640.InNudeNetReleases);
        Assert.False(KnownModels.HotscreenAnimeS640.InNudeNetReleases);
    }

    [Fact]
    public void AnUnknownModelNameIsAnError() {
        Assert.Throws<ArgumentException>(() => Loader("960x"));
    }

    [Fact]
    public void NothingIsFoundInEmptyFolders() {
        Assert.Null(Loader().FindModel(new[] { _first, _second }));
    }
}
