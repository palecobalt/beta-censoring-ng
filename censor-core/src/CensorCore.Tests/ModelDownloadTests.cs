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
    public void AnUnknownModelNameIsAnError() {
        Assert.Throws<ArgumentException>(() => Loader("960x"));
    }

    [Fact]
    public void NothingIsFoundInEmptyFolders() {
        Assert.Null(Loader().FindModel(new[] { _first, _second }));
    }
}
