using CensorCore;
using CensorCore.Censoring;
using Xunit;

namespace CensorCore.Tests;

public class OverlapMergerTests {
    private static OverlapMerger CreateMerger(float distance = 0F, bool enabled = true) =>
        new(new GlobalCensorOptions { MergeOverlapping = enabled, MergeDistance = distance });

    private static StaticResultsParser CreateParser(params (string Label, string CensorType, int Level)[] options) =>
        new(options.ToDictionary(o => o.Label, o => new ImageCensorOptions(o.CensorType, o.Level)));

    [Fact]
    public void DoesNothingWhenDisabled() {
        var parser = CreateParser(("A", "blackbars", 10), ("B", "blackbars", 10));
        var classifications = new[] {
            new Classification(new BoundingBox(0, 0, 20, 20), 1, "A"),
            new Classification(new BoundingBox(10, 10, 30, 30), 1, "B")
        };
        var transformed = CreateMerger(enabled: false).TransformResults(classifications, parser).ToList();

        Assert.Equal(2, transformed.Count);
    }

    [Fact]
    public void MergesDifferentClassesWithSameCensorType() {
        var parser = CreateParser(("A", "blackbars", 10), ("B", "blackbars", 15));
        var classifications = new[] {
            new Classification(new BoundingBox(0, 0, 20, 20), 0.9F, "A"),
            new Classification(new BoundingBox(10, 10, 30, 30), 0.5F, "B")
        };
        var transformed = CreateMerger().TransformResults(classifications, parser).ToList();

        var merged = Assert.Single(transformed);
        Assert.Equal(0, merged.Box.X);
        Assert.Equal(0, merged.Box.Y);
        Assert.Equal(30, merged.Box.Width);
        Assert.Equal(30, merged.Box.Height);
        Assert.Equal("B", merged.Label); // highest level wins
        Assert.Equal(0.9F, merged.Confidence);
    }

    [Fact]
    public void KeepsDifferentCensorTypesSeparate() {
        var parser = CreateParser(("A", "blur", 10), ("B", "blackbars", 10));
        var classifications = new[] {
            new Classification(new BoundingBox(0, 0, 20, 20), 1, "A"),
            new Classification(new BoundingBox(10, 10, 30, 30), 1, "B")
        };
        var transformed = CreateMerger().TransformResults(classifications, parser).ToList();

        Assert.Equal(2, transformed.Count);
    }

    [Fact]
    public void IgnoresClassesThatAreNotCensored() {
        var parser = CreateParser(("A", "blackbars", 10));
        var classifications = new[] {
            new Classification(new BoundingBox(0, 0, 20, 20), 1, "A"),
            new Classification(new BoundingBox(10, 10, 30, 30), 1, "UNLISTED")
        };
        var transformed = CreateMerger().TransformResults(classifications, parser).ToList();

        Assert.Equal(2, transformed.Count);
        Assert.Contains(transformed, c => c.Label == "A" && c.Box.Width == 20);
    }

    [Fact]
    public void MergesNearbyBoxesOnlyWithDistance() {
        var parser = CreateParser(("A", "blackbars", 10), ("B", "blackbars", 10));
        var classifications = new[] {
            new Classification(new BoundingBox(0, 0, 20, 20), 1, "A"),
            new Classification(new BoundingBox(22, 0, 42, 20), 1, "B")
        };

        Assert.Equal(2, CreateMerger(distance: 0F).TransformResults(classifications, parser).Count());
        var merged = Assert.Single(CreateMerger(distance: 0.1F).TransformResults(classifications, parser));
        Assert.Equal(42, merged.Box.Width);
    }

    [Fact]
    public void MergesChainsOfOverlappingBoxes() {
        var parser = CreateParser(("A", "pixelate", 10));
        var classifications = new[] {
            new Classification(new BoundingBox(0, 0, 10, 10), 1, "A"),
            new Classification(new BoundingBox(16, 0, 26, 10), 1, "A"),
            new Classification(new BoundingBox(8, 0, 18, 10), 1, "A")
        };
        var transformed = CreateMerger().TransformResults(classifications, parser).ToList();

        var merged = Assert.Single(transformed);
        Assert.Equal(26, merged.Box.Width);
    }
}
