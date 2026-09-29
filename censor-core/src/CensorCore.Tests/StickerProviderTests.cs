using CensorCore.Censoring;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace CensorCore.Tests;

public class StickerProviderTests {
    private static readonly Rgba32 Red = new(255, 0, 0);
    private static readonly Rgba32 Green = new(0, 255, 0);

    // implementsRandom: whether the store picks stickers itself, or StickerProvider has to choose from GetImages
    private class FakeStore : IAssetStore {
        private readonly List<RawImageData> _images;
        private readonly bool _implementsRandom;

        public FakeStore(bool implementsRandom, params (int Width, int Height, Rgba32 Color)[] stickers) {
            _implementsRandom = implementsRandom;
            _images = stickers.Select(s => {
                using var img = new Image<Rgba32>(s.Width, s.Height, s.Color);
                using var encoded = new MemoryStream();
                img.SaveAsPng(encoded);
                return new RawImageData(encoded.ToArray(), "image/png");
            }).ToList();
        }

        public Task<string?> GetRandomCaption(string? category) => Task.FromResult<string?>(null);
        public Task<RawImageData?> GetRandomImage(string imageType, float? ratio, List<string>? category) =>
            _implementsRandom ? Task.FromResult<RawImageData?>(_images.First()) : throw new NotImplementedException();
        public Task<IEnumerable<RawImageData>> GetImages(string imageType, List<string>? category) => Task.FromResult(_images.AsEnumerable());
    }

    private static int CountPixels(Image<Rgba32> img, Rgba32 color) {
        var count = 0;
        img.ProcessPixelRows(rows => {
            for (int y = 0; y < rows.Height; y++) {
                foreach (var p in rows.GetRowSpan(y)) {
                    if (Math.Abs(p.R - color.R) < 16 && Math.Abs(p.G - color.G) < 16 && Math.Abs(p.B - color.B) < 16) {
                        count++;
                    }
                }
            }
        });
        return count;
    }

    private static async Task<Image<Rgba32>> Draw(IAssetStore store, BoundingBox box, float? angle = null) {
        var img = new Image<Rgba32>(1000, 1000, new Rgba32(0, 0, 128));
        var match = new Classification(box, 1F, "FACE_F") { SourceAngle = angle };
        var mutation = await new StickerProvider(store).CensorImage(img, match, "sticker:test", 10);
        img.Mutate(x => mutation!(x));
        return img;
    }

    [Theory]
    [InlineData(200, 300, true)]  // portrait: its integer aspect ratio was 0
    [InlineData(300, 200, true)]  // 1.5 was truncated to 1
    [InlineData(200, 300, false)]
    [InlineData(300, 200, false)]
    public async Task DrawsStickerWithMatchingAspectRatio(int width, int height, bool implementsRandom) {
        using var img = await Draw(new FakeStore(implementsRandom, (width, height, Red)), new BoundingBox(100, 100, 100 + width, 100 + height));

        Assert.True(CountPixels(img, Red) > width * height * 0.9);
    }

    [Fact]
    public async Task PicksAFittingStickerEveryTime() {
        var store = new FakeStore(false, (200, 200, Green), (200, 300, Red), (400, 100, Green));
        for (int i = 0; i < 20; i++) {
            using var img = await Draw(store, new BoundingBox(100, 100, 300, 400));

            Assert.True(CountPixels(img, Red) > 200 * 300 * 0.9);
            Assert.Equal(0, CountPixels(img, Green));
        }
    }

    [Fact]
    public async Task DrawsNoStickerWhenNoneFits() {
        using var img = await Draw(new FakeStore(false, (400, 100, Red)), new BoundingBox(100, 100, 300, 400));

        Assert.Equal(0, CountPixels(img, Red));
    }

    [Theory]
    [InlineData(0F)]
    [InlineData(10F)]
    [InlineData(-25F)]
    public async Task DrawsRotatedStickerFarFromOrigin(float angle) {
        using var img = await Draw(new FakeStore(true, (200, 100, Red)), new BoundingBox(700, 800, 900, 900), angle);

        Assert.True(CountPixels(img, Red) > 200 * 100 * 0.8);
    }
}
