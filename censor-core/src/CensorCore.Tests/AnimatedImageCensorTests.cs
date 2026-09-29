using CensorCore.Censoring;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CensorCore.Tests;

public class AnimatedImageCensorTests {
    [Fact]
    public void AlwaysDetectsFirstAndLastFrames() {
        var frames = AnimatedImageCensor.SelectDetectionFrames(new[] { 100, 100 }, 200);

        Assert.Equal(new[] { 0, 1 }, frames);
    }

    [Fact]
    public void SamplesFramesByElapsedTime() {
        // ten 50 ms frames with a 200 ms interval
        var frames = AnimatedImageCensor.SelectDetectionFrames(Enumerable.Repeat(50, 10).ToList(), 200);

        Assert.Equal(new[] { 0, 4, 8, 9 }, frames);
    }

    [Fact]
    public void TreatsTinyDelaysAsBrowsersDo() {
        // delays of 10 ms or less play as 100 ms
        var frames = AnimatedImageCensor.SelectDetectionFrames(new[] { 0, 10, 0, 10, 0 }, 200);

        Assert.Equal(new[] { 0, 2, 4 }, frames);
    }

    [Fact]
    public void ZeroIntervalDetectsEveryFrame() {
        var frames = AnimatedImageCensor.SelectDetectionFrames(new[] { 40, 40, 40 }, 0);

        Assert.Equal(new[] { 0, 1, 2 }, frames);
    }

    [Theory]
    [InlineData(5, 4, 8)]
    [InlineData(4, 4, 4)]
    [InlineData(9, 9, 9)]
    [InlineData(0, 0, 0)]
    public void FindsNearestDetectionFrames(int index, int expectedBefore, int expectedAfter) {
        var (before, after) = AnimatedImageCensor.NearestDetectionFrames(new[] { 0, 4, 8, 9 }, index);

        Assert.Equal(expectedBefore, before);
        Assert.Equal(expectedAfter, after);
    }

    [Fact]
    public void ReadsFrameDelaysIncludingZero() {
        var delays = new[] { 10, 0, 20 };
        using var gif = new Image<Rgba32>(8, 8);
        gif.Frames.CreateFrame();
        gif.Frames.CreateFrame();
        for (int i = 0; i < delays.Length; i++) {
            gif.Frames[i][0, 0] = new Rgba32((byte)(i * 100), 0, 0);
            gif.Frames[i].Metadata.GetGifMetadata().FrameDelay = delays[i];
        }
        using var encoded = new MemoryStream();
        gif.SaveAsGif(encoded, new GifEncoder { ColorTableMode = GifColorTableMode.Local });

        Assert.Equal(delays, AnimatedImageCensor.ReadFrameDelays(encoded.ToArray()));
    }

    [Fact]
    public void ReadsNothingFromNonGifData() {
        Assert.Empty(AnimatedImageCensor.ReadFrameDelays(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void RecognisesAnimationFormats() {
        using var animation = Frames(3);

        Assert.Equal(AnimationFormat.Gif, AnimatedImageCensor.GetAnimationFormat(Save(animation, isGif: true)));
        Assert.Equal(AnimationFormat.Webp, AnimatedImageCensor.GetAnimationFormat(Save(animation, isGif: false)));
        using var still = new Image<Rgba32>(8, 8);
        using var encoded = new MemoryStream();
        still.SaveAsWebp(encoded);
        Assert.Null(AnimatedImageCensor.GetAnimationFormat(encoded.ToArray()));
        Assert.Null(AnimatedImageCensor.GetAnimationFormat(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void WebpKeepsFramesDelaysAndLoopCount() {
        using var animation = Frames(3);
        for (int i = 0; i < 3; i++) {
            animation.Frames[i].Metadata.GetWebpMetadata().FrameDelay = (uint)(40 + i * 30);
        }
        animation.Metadata.GetWebpMetadata().RepeatCount = 3;
        using var source = Image.Load<Rgba32>(Save(animation, isGif: false));
        var frames = Enumerable.Range(0, 3).Select(i => source.Frames.CloneFrame(i)).ToList();

        var censored = AnimatedImageCensor.Encode(source, AnimationFormat.Webp, frames, new[] { 40, 70, 100 });

        Assert.Equal("image/webp", censored.MimeType);
        using var output = Image.Load<Rgba32>(censored.ImageContents);
        Assert.Equal(3, output.Frames.Count);
        Assert.Equal(new uint[] { 40, 70, 100 }, output.Frames.Select(f => f.Metadata.GetWebpMetadata().FrameDelay));
        Assert.Equal(3, output.Metadata.GetWebpMetadata().RepeatCount);
    }

    [Fact]
    public void WebpFramesReplaceTransparentPixels() {
        // an opaque first frame, then a fully transparent one: blending would leave the first frame showing through
        using var animation = new Image<Rgba32>(8, 8, new Rgba32(255, 0, 0, 255));
        animation.Frames.AddFrame(new Image<Rgba32>(8, 8, new Rgba32(0, 0, 0, 0)).Frames.RootFrame);
        var frames = new[] { animation.Frames.CloneFrame(0), animation.Frames.CloneFrame(1) };

        var censored = AnimatedImageCensor.Encode(animation, AnimationFormat.Webp, frames, new[] { 100, 100 });

        using var output = Image.Load<Rgba32>(censored.ImageContents);
        Assert.Equal(0, output.Frames[1][4, 4].A);
    }

    [Fact]
    public void GifKeepsFramesAndDelays() {
        using var animation = Frames(3);
        using var source = Image.Load<Rgba32>(Save(animation, isGif: true));
        var frames = Enumerable.Range(0, 3).Select(i => source.Frames.CloneFrame(i)).ToList();

        var censored = AnimatedImageCensor.Encode(source, AnimationFormat.Gif, frames, new[] { 0, 100, 200 });

        Assert.Equal("image/gif", censored.MimeType);
        Assert.Equal(new[] { 0, 10, 20 }, AnimatedImageCensor.ReadFrameDelays(censored.ImageContents));
    }

    // frames of different colours, so encoders keep them all
    private static Image<Rgba32> Frames(int count) {
        var image = new Image<Rgba32>(8, 8, new Rgba32(0, 0, 0, 255));
        for (int i = 1; i < count; i++) {
            image.Frames.AddFrame(new Image<Rgba32>(8, 8, new Rgba32((byte)(i * 80), 0, 0, 255)).Frames.RootFrame);
        }
        return image;
    }

    private static byte[] Save(Image<Rgba32> image, bool isGif) {
        using var encoded = new MemoryStream();
        if (isGif) {
            image.SaveAsGif(encoded);
        } else {
            image.SaveAsWebp(encoded, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
        }
        return encoded.ToArray();
    }
}
