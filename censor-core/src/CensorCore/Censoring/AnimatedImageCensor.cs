using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace CensorCore.Censoring;

/// <summary>
/// Censors every frame of an animated GIF, running the model on frames sampled by elapsed animation time.
/// </summary>
/// <remarks>
/// Each frame is censored with the matches from the nearest sampled frames before and after it, so anything that
/// moves between samples stays covered.
/// </remarks>
public class AnimatedImageCensor {
    private readonly AIService _ai;
    private readonly ICensoringProvider _censor;
    private readonly GlobalCensorOptions _options;

    public AnimatedImageCensor(AIService ai, ICensoringProvider censor, GlobalCensorOptions? options = null) {
        (_ai, _censor, _options) = (ai, censor, options ?? new GlobalCensorOptions());
    }

    /// <summary>
    /// Censors an animated GIF. Returns null when the data isn't an animated GIF, animation support is disabled, or the
    /// GIF has too many frames; the image should then be censored as a still image.
    /// </summary>
    public async Task<CensoredImage?> CensorAnimatedGif(byte[] data, MatchOptions? matchOptions, IResultParser? parser) {
        if (_options.CensorAnimatedGifs == false || !IsGif(data)) {
            return null;
        }
        using var gif = Image.Load<Rgba32>(data);
        var frameCount = gif.Frames.Count;
        if (frameCount < 2 || frameCount > (_options.AnimationMaxFrames ?? 500)) {
            return null;
        }
        // ImageSharp 2's decoder reports zero delays as the previous frame's delay, so read them from the file
        var delays = ReadFrameDelays(data);
        if (delays.Count != frameCount) {
            delays = Enumerable.Range(0, frameCount).Select(i => gif.Frames[i].Metadata.GetGifMetadata().FrameDelay).ToList();
        }
        var detectionFrames = SelectDetectionFrames(delays, _options.AnimationDetectionIntervalMs ?? 200);
        var parallelism = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var matches = new List<Classification>[frameCount];
        await Parallel.ForEachAsync(detectionFrames, parallelism, async (index, _) => {
            using var frame = CloneFrame(gif, index);
            using var encoded = new MemoryStream();
            frame.SaveAsBmp(encoded);
            var result = await _ai.RunModel(encoded.ToArray(), matchOptions);
            matches[index] = result?.Results ?? new List<Classification>();
        });
        var detectionTime = timer.Elapsed;

        var censoredFrames = new Image<Rgba32>[frameCount];
        try {
            await Parallel.ForEachAsync(Enumerable.Range(0, frameCount), parallelism, async (index, _) => {
                var (before, after) = NearestDetectionFrames(detectionFrames, index);
                var frameMatches = (before == after ? matches[before] : matches[before].Concat(matches[after]))
                    .Select(CopyMatch)
                    .ToList();
                var frame = CloneFrame(gif, index);
                if (!NeedsCensoring(frameMatches, parser)) {
                    censoredFrames[index] = frame;
                    return;
                }
                using (frame) {
                    // PNG keeps transparency; the provider encodes its output in the input's format
                    var censored = await _censor.CensorImage(new ImageResult(new ImageData(frame) { Format = PngFormat.Instance }, frameMatches), parser);
                    censoredFrames[index] = Image.Load<Rgba32>(censored.ImageContents);
                }
            });
            var censoringTime = timer.Elapsed;

            using var output = new Image<Rgba32>(gif.Width, gif.Height);
            for (int i = 0; i < frameCount; i++) {
                var gifFrame = output.Frames.AddFrame(censoredFrames[i].Frames.RootFrame).Metadata.GetGifMetadata();
                gifFrame.FrameDelay = delays[i];
                // every frame is a complete image, so clear the previous one rather than drawing over it
                gifFrame.DisposalMethod = GifDisposalMethod.RestoreToBackground;
            }
            output.Frames.RemoveFrame(0);
            output.Metadata.GetGifMetadata().RepeatCount = gif.Metadata.GetGifMetadata().RepeatCount;
            using var result = new MemoryStream();
            output.SaveAsGif(result, new GifEncoder { ColorTableMode = GifColorTableMode.Local });
            Console.WriteLine($"Censored animated GIF: {frameCount} frames ({detectionFrames.Count} through the model) in {timer.Elapsed.TotalSeconds:F1}s "
                + $"(model {detectionTime.TotalSeconds:F1}s, censoring {(censoringTime - detectionTime).TotalSeconds:F1}s, encoding {(timer.Elapsed - censoringTime).TotalSeconds:F1}s)");
            return new CensoredImage(result.ToArray(), "image/gif", null);
        } finally {
            foreach (var frame in censoredFrames) {
                frame?.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads each frame's delay, in hundredths of a second, straight from the GIF data.
    /// </summary>
    public static List<int> ReadFrameDelays(byte[] data) {
        var delays = new List<int>();
        if (data.Length < 13) {
            return delays;
        }
        // header and logical screen descriptor, then the optional global color table
        var position = 13;
        if ((data[10] & 0x80) != 0) {
            position += 3 * (1 << ((data[10] & 0x07) + 1));
        }
        int? pendingDelay = null;
        while (position < data.Length) {
            switch (data[position]) {
                case 0x21: // extension block
                    if (position + 5 < data.Length && data[position + 1] == 0xF9) {
                        pendingDelay = data[position + 4] | (data[position + 5] << 8);
                    }
                    position = SkipSubBlocks(data, position + 2);
                    break;
                case 0x2C: // image descriptor
                    delays.Add(pendingDelay ?? 0);
                    pendingDelay = null;
                    if (position + 9 >= data.Length) {
                        return delays;
                    }
                    var flags = data[position + 9];
                    position += 10;
                    if ((flags & 0x80) != 0) {
                        position += 3 * (1 << ((flags & 0x07) + 1));
                    }
                    // skip the LZW minimum code size byte, then the image data
                    position = SkipSubBlocks(data, position + 1);
                    break;
                default: // trailer or unrecognised data
                    return delays;
            }
        }
        return delays;
    }

    private static int SkipSubBlocks(byte[] data, int position) {
        while (position < data.Length && data[position] != 0) {
            position += data[position] + 1;
        }
        return position + 1;
    }

    /// <summary>
    /// Picks the frames to run the model on: the first and last frames, plus a frame whenever at least
    /// <paramref name="intervalMs"/> of animation time has passed since the previous pick.
    /// </summary>
    /// <param name="frameDelays">GIF frame delays in hundredths of a second.</param>
    public static List<int> SelectDetectionFrames(IReadOnlyList<int> frameDelays, int intervalMs) {
        var frames = new List<int> { 0 };
        var elapsed = 0;
        for (int i = 1; i < frameDelays.Count; i++) {
            elapsed += EffectiveDelayMs(frameDelays[i - 1]);
            if (elapsed >= intervalMs) {
                frames.Add(i);
                elapsed = 0;
            }
        }
        if (frames[^1] != frameDelays.Count - 1) {
            frames.Add(frameDelays.Count - 1);
        }
        return frames;
    }

    /// <summary>
    /// Finds the closest detection frames at or before and at or after the given frame.
    /// </summary>
    /// <param name="detectionFrames">Sorted detection frame indexes, including the first and last frame.</param>
    public static (int Before, int After) NearestDetectionFrames(IReadOnlyList<int> detectionFrames, int index) {
        var before = detectionFrames.Last(f => f <= index);
        var after = detectionFrames.First(f => f >= index);
        return (before, after);
    }

    // frames with nothing the client asked to censor (and no whole-image obfuscation) can be used as they are
    private static bool NeedsCensoring(List<Classification> matches, IResultParser? parser) {
        if (parser == null) {
            return matches.Any();
        }
        return matches.Any(m => !IsUncensored(parser.GetOptions(m))) || !IsUncensored(parser.GetOptions("_OBFUSCATION"));
    }

    private static bool IsUncensored(ImageCensorOptions? options) =>
        options?.CensorType is not { } type || string.IsNullOrWhiteSpace(type) || type.Equals("none", StringComparison.OrdinalIgnoreCase);

    // browsers play GIF frame delays under 20 ms as 100 ms
    private static int EffectiveDelayMs(int delayCentiseconds) => delayCentiseconds < 2 ? 100 : delayCentiseconds * 10;

    private static bool IsGif(byte[] data) => data.Length > 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F';

    private static Image<Rgba32> CloneFrame(Image<Rgba32> gif, int index) {
        lock (gif) {
            return gif.Frames.CloneFrame(index);
        }
    }

    // censoring transformers resize boxes in place, so each frame needs its own copies
    private static Classification CopyMatch(Classification match) =>
        new(new BoundingBox(match.Box.X, match.Box.Y, match.Box.X + match.Box.Width, match.Box.Y + match.Box.Height), match.Confidence, match.Label) {
            SourceAngle = match.SourceAngle,
            VirtualBox = match.VirtualBox
        };
}
