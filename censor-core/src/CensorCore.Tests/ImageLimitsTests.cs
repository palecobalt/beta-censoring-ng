using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CensorCore.Tests;

// changes the shared limit; the images other tests load through ImageSharpHandler stay far below the value used here
[Collection("ImageLimits")]
public class ImageLimitsTests : IDisposable {
    private readonly long _maxPixels = ImageLimits.MaxPixels;

    public void Dispose() => ImageLimits.MaxPixels = _maxPixels;

    private static byte[] Png(int width, int height) {
        using var image = new Image<Rgba32>(width, height);
        using var encoded = new MemoryStream();
        image.SaveAsPng(encoded);
        return encoded.ToArray();
    }

    [Fact]
    public async Task RefusesImagesOverTheLimitWithoutDecodingThem() {
        ImageLimits.MaxPixels = 10_000;
        var image = Png(200, 100);

        await Assert.ThrowsAsync<ImageTooLargeException>(() => new ImageSharpHandler().LoadImageData(image));
    }

    [Fact]
    public async Task LoadsImagesAtTheLimit() {
        ImageLimits.MaxPixels = 10_000;

        var image = await new ImageSharpHandler().LoadImageData(Png(100, 100));

        Assert.Equal(100, image.SourceImage.Width);
    }
}
