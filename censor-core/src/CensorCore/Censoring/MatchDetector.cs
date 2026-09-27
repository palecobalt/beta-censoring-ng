namespace CensorCore.Censoring;

/// <summary>
/// Finds matches in images without censoring them, for clients that apply censoring themselves (such as video).
/// </summary>
/// <remarks>
/// The same result transformers as censoring are applied, so the boxes cover the areas a censored image would.
/// Censoring middleware isn't run, so matches it adds (such as facial features) aren't included.
/// </remarks>
public class MatchDetector {
    private readonly AIService _ai;
    private readonly GlobalCensorOptions _options;
    private readonly IEnumerable<IResultsTransformer> _transformers;

    public MatchDetector(AIService ai, GlobalCensorOptions? options = null, IEnumerable<IResultsTransformer>? transformers = null) {
        (_ai, _options, _transformers) = (ai, options ?? new GlobalCensorOptions(), transformers ?? Enumerable.Empty<IResultsTransformer>());
    }

    /// <summary>
    /// Runs the model on an image. Returns null when the model skipped the image.
    /// </summary>
    /// <param name="transform">Applies the result transformers (censor scaling and box merging) to the matches.</param>
    public async Task<DetectionResult?> Detect(byte[] data, MatchOptions? matchOptions = null, IResultParser? parser = null, bool transform = true) {
        var result = await _ai.RunModel(data, matchOptions);
        if (result == null) {
            return null;
        }
        var image = result.ImageData;
        try {
            var matches = transform ? _transformers.ApplyTransformers(result.Results, parser, _options) : result.Results;
            return new DetectionResult(image.SourceImage.Width, image.SourceImage.Height, matches.ToList());
        } finally {
            if (image.SampledImage != null && !ReferenceEquals(image.SampledImage, image.SourceImage)) {
                image.SampledImage.Dispose();
            }
            image.SourceImage.Dispose();
        }
    }

    /// <summary>
    /// Runs the model on several images, returning the results in the same order.
    /// </summary>
    public async Task<DetectionResult?[]> DetectMany(IReadOnlyList<byte[]> images, MatchOptions? matchOptions = null, IResultParser? parser = null, bool transform = true) {
        var results = new DetectionResult?[images.Count];
        // image decoding runs in parallel; GPU hosts still serialize model runs
        var parallelism = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
        await Parallel.ForEachAsync(Enumerable.Range(0, images.Count), parallelism, async (index, _) => {
            results[index] = await Detect(images[index], matchOptions, parser, transform);
        });
        return results;
    }
}
