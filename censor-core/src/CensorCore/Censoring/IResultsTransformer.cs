using SixLabors.ImageSharp;

namespace CensorCore.Censoring;

public interface IResultsTransformer {
    IEnumerable<Classification> TransformResults(IEnumerable<Classification> matches, IResultParser? parser);
}

public static class ResultsTransformerExtensions {
    /// <summary>
    /// Applies each transformer to the matches in turn, unless <see cref="GlobalCensorOptions.AllowTransformers"/> is disabled.
    /// </summary>
    /// <remarks>
    /// Transformers can change match boxes in place.
    /// </remarks>
    public static IEnumerable<Classification> ApplyTransformers(this IEnumerable<IResultsTransformer> transformers, IEnumerable<Classification> matches, IResultParser? parser, GlobalCensorOptions? options = null) {
        if (options?.AllowTransformers == false) {
            return matches;
        }
        foreach (var transformer in transformers) {
            matches = transformer.TransformResults(matches, parser);
        }
        return matches;
    }
}

public class CensorScaleTransformer : IResultsTransformer {
    private readonly float _scaleFactor;

    public CensorScaleTransformer(GlobalCensorOptions? censorOptions = null) {
        _scaleFactor = censorOptions?.RelativeCensorScale ?? 1F;

    }
    public IEnumerable<Classification> TransformResults(IEnumerable<Classification> matches, IResultParser? parser) {
        return matches.Select(m =>
        {
            m.Box = m.Box.ScaleBy(GetScaleAmount(m.Box.Width), GetScaleAmount(m.Box.Height));
            return m;
        });
    }

    private float GetScaleAmount(int srcValue, bool split = true) {
        return ((srcValue * _scaleFactor) - srcValue) / (split ? 2 : 1);
    }
}

/// <summary>
/// Merges overlapping matches of the same class into a single match.
/// </summary>
/// <remarks>
/// Matches below 75% of the confidence of the match they overlap don't grow the merged box, but are kept as
/// separate matches rather than dropped, since they're often a different person the model was less sure about.
/// Merged boxes are never rotated: a rotated union box doesn't cover the corners of the matches it replaced.
/// </remarks>
public class IntersectingMatchMerger : IResultsTransformer {
    public IEnumerable<Classification> TransformResults(IEnumerable<Classification> results, IResultParser? parser) {
        var transformed = new List<Classification>();
        foreach (var labelGroup in results.GroupBy(r => r.Label)) {
            var pending = labelGroup.OrderByDescending(r => r.Confidence).ToList();
            while (pending.Count > 0) {
                var seed = pending[0];
                pending.RemoveAt(0);
                var merged = seed.Box.ToRectangle();
                var mergedAny = false;
                var growing = true;
                while (growing) {
                    growing = false;
                    foreach (var candidate in pending) {
                        var candidateRect = candidate.Box.ToRectangle();
                        if (!merged.IntersectsWith(candidateRect) || candidate.Confidence < seed.Confidence * 0.75F) {
                            continue;
                        }
                        var unionRect = Rectangle.Union(merged, candidateRect);
                        var tooLarge = unionRect.Width > merged.Width * 2F && unionRect.Width > candidateRect.Width * 2F
                            && unionRect.Height > merged.Height * 2F && unionRect.Height > candidateRect.Height * 2F;
                        if (tooLarge) {
                            continue;
                        }
                        merged = unionRect;
                        pending.Remove(candidate);
                        mergedAny = growing = true;
                        break;
                    }
                }
                transformed.Add(mergedAny
                    ? new Classification(new BoundingBox(merged.X, merged.Y, merged.X + merged.Width, merged.Y + merged.Height), seed.Confidence, labelGroup.Key)
                    : seed);
            }
        }
        return transformed;
    }
}
