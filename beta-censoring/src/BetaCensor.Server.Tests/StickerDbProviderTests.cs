using BetaCensor.Web;
using BetaCensor.Web.Providers;
using CensorCore.Censoring;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace BetaCensor.Server.Tests;

public class StickerDbProviderTests : IDisposable {
    private readonly string _root = Directory.CreateTempSubdirectory("stickers-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void AddSticker(string relativePath, int width, int height) {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var img = new Image<Rgba32>(width, height, new Rgba32(255, 0, 0));
        img.SaveAsPng(path);
    }

    private StickerDbProvider Load() {
        var files = new NestedFilesProvider(new PhysicalFileProvider(_root));
        return new StickerDbProvider(new StickerOptions(), null, new IFileProvider[] { files }, NullLogger<StickerDbProvider>.Instance);
    }

    [Theory]
    [InlineData(200, 300)]
    [InlineData(300, 200)]
    [InlineData(200, 200)]
    public async Task FindsStickersByAspectRatio(int width, int height) {
        AddSticker("pack/sticker.png", width, height);

        var sticker = await Load().GetRandomImage(KnownAssetTypes.Stickers, (float)width / height, new List<string> { "pack" });

        Assert.NotNull(sticker);
        var info = Image.Identify(sticker.RawData);
        Assert.Equal((width, height), (info.Width, info.Height));
    }

    [Fact]
    public async Task ReturnsNothingWithoutAFittingSticker() {
        AddSticker("pack/wide.png", 400, 100);

        Assert.Null(await Load().GetRandomImage(KnownAssetTypes.Stickers, 0.67F, new List<string> { "pack" }));
    }

    [Fact]
    public void LoadsNestedAndSameNamedFiles() {
        AddSticker("pack/top.png", 100, 100);
        AddSticker("pack/a/same.png", 100, 100);
        AddSticker("pack/b/deeper/same.png", 100, 100);
        AddSticker("pack/with space.png", 100, 100);

        var stickers = Load().GetStickers();

        Assert.Equal(4, stickers["pack"].Count());
    }
}
