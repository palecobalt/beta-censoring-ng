namespace CensorCore.Censoring;

/// <summary>
/// Merges overlapping (or nearby) matches of any class into a single box, as long as they're censored with the same censor type.
/// </summary>
/// <remarks>
/// Only runs when <see cref="GlobalCensorOptions.MergeOverlapping"/> is enabled. Merging repeats until no merged boxes
/// overlap. A merged match takes the label (and so the censor options) of the member with the highest censor level.
/// </remarks>
public class OverlapMerger : IResultsTransformer {
    private readonly GlobalCensorOptions _options;

    public OverlapMerger(GlobalCensorOptions? options = null) {
        _options = options ?? new GlobalCensorOptions();
    }

    private class Cluster {
        public List<Classification> Members { get; } = new();
        public float X1, Y1, X2, Y2;

        public Cluster(Classification match) {
            Members.Add(match);
            (X1, Y1, X2, Y2) = (match.Box.X, match.Box.Y, match.Box.X + match.Box.Width, match.Box.Y + match.Box.Height);
        }

        public void Absorb(Cluster other) {
            Members.AddRange(other.Members);
            (X1, Y1, X2, Y2) = (Math.Min(X1, other.X1), Math.Min(Y1, other.Y1), Math.Max(X2, other.X2), Math.Max(Y2, other.Y2));
        }

        // each box is enlarged by distance * its own width/height on every side before checking for overlap
        public bool IsNear(Cluster other, float distance) {
            float padX = (X2 - X1) * distance, padY = (Y2 - Y1) * distance;
            float otherPadX = (other.X2 - other.X1) * distance, otherPadY = (other.Y2 - other.Y1) * distance;
            return X1 - padX < other.X2 + otherPadX && other.X1 - otherPadX < X2 + padX
                && Y1 - padY < other.Y2 + otherPadY && other.Y1 - otherPadY < Y2 + padY;
        }
    }

    public IEnumerable<Classification> TransformResults(IEnumerable<Classification> results, IResultParser? parser) {
        var matches = results.ToList();
        if (_options.MergeOverlapping != true || parser == null) {
            return matches;
        }
        var distance = Math.Max(0F, _options.MergeDistance ?? 0F);
        var transformed = new List<Classification>();
        foreach (var group in matches.GroupBy(m => parser.GetOptions(m)?.CensorType?.ToLowerInvariant() ?? "none")) {
            if (group.Key == "none") {
                // classes the client didn't ask to censor
                transformed.AddRange(group);
                continue;
            }
            var clusters = group.Select(m => new Cluster(m)).ToList();
            var merging = true;
            while (merging) {
                merging = false;
                for (int i = 0; i < clusters.Count && !merging; i++) {
                    for (int j = i + 1; j < clusters.Count && !merging; j++) {
                        if (clusters[i].IsNear(clusters[j], distance)) {
                            clusters[i].Absorb(clusters[j]);
                            clusters.RemoveAt(j);
                            merging = true;
                        }
                    }
                }
            }
            foreach (var cluster in clusters) {
                if (cluster.Members.Count == 1) {
                    transformed.Add(cluster.Members[0]);
                    continue;
                }
                var strongest = cluster.Members
                    .OrderByDescending(m => parser.GetOptions(m)?.Level ?? 10)
                    .ThenByDescending(m => m.Confidence)
                    .First();
                transformed.Add(new Classification(
                    new BoundingBox(cluster.X1, cluster.Y1, cluster.X2, cluster.Y2),
                    cluster.Members.Max(m => m.Confidence),
                    strongest.Label));
            }
        }
        return transformed;
    }
}
