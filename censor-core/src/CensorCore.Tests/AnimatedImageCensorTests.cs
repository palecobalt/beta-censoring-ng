using CensorCore.Censoring;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CensorCore.Tests;

public class AnimatedImageCensorTests {
    [Fact]
    public void AlwaysDetectsFirstAndLastFrames() {
        var frames = AnimatedImageCensor.SelectDetectionFrames(new[] { 10, 10 }, 200);

        Assert.Equal(new[] { 0, 1 }, frames);
    }

    [Fact]
    public void SamplesFramesByElapsedTime() {
        // ten 50 ms frames with a 200 ms interval
        var frames = AnimatedImageCensor.SelectDetectionFrames(Enumerable.Repeat(5, 10).ToList(), 200);

        Assert.Equal(new[] { 0, 4, 8, 9 }, frames);
    }

    [Fact]
    public void TreatsTinyDelaysAsBrowsersDo() {
        // delays of 0 play as 100 ms
        var frames = AnimatedImageCensor.SelectDetectionFrames(new[] { 0, 0, 0, 0, 0 }, 200);

        Assert.Equal(new[] { 0, 2, 4 }, frames);
    }

    [Fact]
    public void ZeroIntervalDetectsEveryFrame() {
        var frames = AnimatedImageCensor.SelectDetectionFrames(new[] { 4, 4, 4 }, 0);

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
}
