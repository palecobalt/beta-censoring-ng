using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace CensorCore.Censoring;

/// <summary>
/// Censors every frame of an animated GIF or WebP, running the model on frames sampled by elapsed animation time.
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
    /// Censors an animated GIF or WebP. Returns null when the data isn't an animation, animation support is disabled,
    /// or it has too many frames; the image should then be censored as a still image.
    /// </summary>
    public async Task<CensoredImage?> CensorAnimated(byte[] data, MatchOptions? matchOptions, IResultParser? parser) {
        var format = GetAnimationFormat(data);
        if (format == null || (_options.CensorAnimatedImages ?? _options.CensorAnimatedGifs ?? true) == false) {
            return null;
        }
        using var source = Image.Load<Rgba32>(data);
        var frameCount = source.Frames.Count;
        if (frameCount < 2 || frameCount > (_options.AnimationMaxFrames ?? 500)) {
            return null;
        }
        var delaysMs = format == AnimationFormat.Gif
            ? GifDelaysMs(source, data)
            : Enumerable.Range(0, frameCount).Select(i => (int)source.Frames[i].Metadata.GetWebpMetadata().FrameDelay).ToList();
        var detectionFrames = SelectDetectionFrames(delaysMs, _options.AnimationDetectionIntervalMs ?? 200);
        var parallelism = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var matches = new List<Classification>[frameCount];
        await Parallel.ForEachAsync(detectionFrames, parallelism, async (index, _) => {
            using var frame = CloneFrame(source, index);
            using var encoded = new MemoryStream();
            frame.SaveAsBmp(encoded);
            var result = await _ai.RunModel(encoded.ToArray(), matchOptions);
            matches[index] = result?.Results ?? new List<Classification>();
        });
        var detectionTime = timer.Elapsed;

        var censoredFrames = new Image<Rgba32>[frameCount];
        // the same sticker or caption for an area in every frame
        AnimationChoices.Begin();
        try {
            await Parallel.ForEachAsync(Enumerable.Range(0, frameCount), parallelism, async (index, _) => {
                var (before, after) = NearestDetectionFrames(detectionFrames, index);
                var frameMatches = (before == after ? matches[before] : matches[before].Concat(matches[after]))
                    .Select(CopyMatch)
                    .ToList();
                var frame = CloneFrame(source, index);
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

            var output = Encode(source, format.Value, censoredFrames, delaysMs);
            Console.WriteLine($"Censored animated {format}: {frameCount} frames ({detectionFrames.Count} through the model) in {timer.Elapsed.TotalSeconds:F1}s "
                + $"(model {detectionTime.TotalSeconds:F1}s, censoring {(censoringTime - detectionTime).TotalSeconds:F1}s, encoding {(timer.Elapsed - censoringTime).TotalSeconds:F1}s)");
            return output;
        } finally {
            foreach (var frame in censoredFrames) {
                frame?.Dispose();
            }
        }
    }

    /// <summary>
    /// Puts censored frames back together as an animation in the source's format, with its frame delays and loop count.
    /// </summary>
    /// <param name="delaysMs">Frame delays in milliseconds, as read from the source.</param>
    public static CensoredImage Encode(Image<Rgba32> source, AnimationFormat format, IReadOnlyList<Image<Rgba32>> frames, IReadOnlyList<int> delaysMs) {
        using var output = new Image<Rgba32>(source.Width, source.Height);
        for (int i = 0; i < frames.Count; i++) {
            var metadata = output.Frames.AddFrame(frames[i].Frames.RootFrame).Metadata;
            if (format == AnimationFormat.Gif) {
                var gifFrame = metadata.GetGifMetadata();
                gifFrame.FrameDelay = delaysMs[i] / 10;
                // every frame is a complete image, so clear the previous one rather than drawing over it
                gifFrame.DisposalMethod = GifDisposalMethod.RestoreToBackground;
            } else {
                var webpFrame = metadata.GetWebpMetadata();
                webpFrame.FrameDelay = (uint)delaysMs[i];
                // every frame is a complete image, so replace the canvas rather than blending over the previous frame
                webpFrame.BlendMethod = WebpBlendMethod.Source;
                webpFrame.DisposalMethod = WebpDisposalMethod.DoNotDispose;
            }
        }
        output.Frames.RemoveFrame(0);
        using var result = new MemoryStream();
        if (format == AnimationFormat.Gif) {
            output.Metadata.GetGifMetadata().RepeatCount = source.Metadata.GetGifMetadata().RepeatCount;
            output.SaveAsGif(result, new GifEncoder { ColorTableMode = GifColorTableMode.Local });
            return new CensoredImage(result.ToArray(), "image/gif", null);
        }
        var sourceWebp = source.Metadata.GetWebpMetadata();
        var outputWebp = output.Metadata.GetWebpMetadata();
        outputWebp.RepeatCount = sourceWebp.RepeatCount;
        outputWebp.BackgroundColor = sourceWebp.BackgroundColor;
        // ImageSharp 3.1 doesn't report the file format of animated WebP, so those come out lossy
        output.SaveAsWebp(result, new WebpEncoder { FileFormat = sourceWebp.FileFormat ?? WebpFileFormatType.Lossy, Quality = 80 });
        return new CensoredImage(result.ToArray(), "image/webp", null);
    }

    /// <summary>
    /// The animation format of the data, or null for anything else. GIFs with a single frame also count as GIF.
    /// </summary>
    public static AnimationFormat? GetAnimationFormat(byte[] data) =>
        IsGif(data) ? AnimationFormat.Gif : IsAnimatedWebp(data) ? AnimationFormat.Webp : null;

    // ImageSharp 2's decoder reported zero delays as the previous frame's delay, so read them from the file
    private static List<int> GifDelaysMs(Image<Rgba32> gif, byte[] data) {
        var delays = ReadFrameDelays(data);
        if (delays.Count != gif.Frames.Count) {
            delays = Enumerable.Range(0, gif.Frames.Count).Select(i => gif.Frames[i].Metadata.GetGifMetadata().FrameDelay).ToList();
        }
        return delays.Select(d => d * 10).ToList();
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
    /// <param name="frameDelaysMs">Frame delays in milliseconds.</param>
    public static List<int> SelectDetectionFrames(IReadOnlyList<int> frameDelaysMs, int intervalMs) {
        var frames = new List<int> { 0 };
        var elapsed = 0;
        for (int i = 1; i < frameDelaysMs.Count; i++) {
            elapsed += EffectiveDelayMs(frameDelaysMs[i - 1]);
            if (elapsed >= intervalMs) {
                frames.Add(i);
                elapsed = 0;
            }
        }
        if (frames[^1] != frameDelaysMs.Count - 1) {
            frames.Add(frameDelaysMs.Count - 1);
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

    // browsers play frame delays of 10 ms or less (GIF: 0 or 1 hundredths) as 100 ms
    private static int EffectiveDelayMs(int delayMs) => delayMs <= 10 ? 100 : delayMs;

    private static bool IsGif(byte[] data) => data.Length > 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F';

    // RIFF....WEBPVP8X with the animation flag (0x02) set in the VP8X flags byte
    private static bool IsAnimatedWebp(byte[] data) =>
        data.Length > 20 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
        && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P'
        && data[12] == 'V' && data[13] == 'P' && data[14] == '8' && data[15] == 'X' && (data[20] & 0x02) != 0;

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

public enum AnimationFormat { Gif, Webp }
