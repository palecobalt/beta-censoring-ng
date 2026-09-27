using System.Text.Json;
using CensorCore;
using CensorCore.Censoring;
using Xunit;

namespace CensorCore.Tests;

public class MatchDetectionTests {
    private class LabelSuffixTransformer : IResultsTransformer {
        private readonly string _suffix;

        public LabelSuffixTransformer(string suffix) => _suffix = suffix;

        public IEnumerable<Classification> TransformResults(IEnumerable<Classification> matches, IResultParser? parser) =>
            matches.Select(m => new Classification(m.Box, m.Confidence, m.Label + _suffix));
    }

    private static Classification[] SingleMatch() => new[] { new Classification(new BoundingBox(0, 0, 10, 10), 1, "A") };

    [Fact]
    public void AppliesTransformersInOrder() {
        var transformers = new IResultsTransformer[] { new LabelSuffixTransformer("1"), new LabelSuffixTransformer("2") };
        var transformed = transformers.ApplyTransformers(SingleMatch(), null);

        Assert.Equal("A12", Assert.Single(transformed).Label);
    }

    [Fact]
    public void SkipsTransformersWhenDisabled() {
        var transformers = new IResultsTransformer[] { new LabelSuffixTransformer("1") };
        var transformed = transformers.ApplyTransformers(SingleMatch(), null, new GlobalCensorOptions { AllowTransformers = false });

        Assert.Equal("A", Assert.Single(transformed).Label);
    }

    [Fact]
    public void ScalesBeforeMergingLikeImageCensoring() {
        var options = new GlobalCensorOptions { RelativeCensorScale = 2F, MergeOverlapping = true };
        var transformers = new IResultsTransformer[] { new CensorScaleTransformer(options), new OverlapMerger(options), new IntersectingMatchMerger() };
        var parser = new StaticResultsParser(new Dictionary<string, ImageCensorOptions> {
            ["A"] = new ImageCensorOptions("blackbars", 10),
            ["B"] = new ImageCensorOptions("blackbars", 10)
        });
        // 2 px apart until scaling doubles each box around its centre
        var matches = new[] {
            new Classification(new BoundingBox(0, 0, 10, 10), 1, "A"),
            new Classification(new BoundingBox(12, 0, 22, 10), 1, "B")
        };
        var merged = Assert.Single(transformers.ApplyTransformers(matches, parser, options).ToList());

        Assert.Equal(-5, merged.Box.X);
        Assert.Equal(32, merged.Box.Width);
        Assert.Equal(20, merged.Box.Height);
    }

    [Fact]
    public void SerializesDetectionResultForRestClients() {
        var result = new DetectionResult(640, 360, new List<Classification> {
            new(new BoundingBox(1.5F, 2, 11.5F, 22), 0.5F, "FACE_F")
        });
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = json.RootElement;

        Assert.Equal(640, root.GetProperty("width").GetInt32());
        Assert.Equal(360, root.GetProperty("height").GetInt32());
        var match = Assert.Single(root.GetProperty("results").EnumerateArray());
        Assert.Equal("FACE_F", match.GetProperty("label").GetString());
        Assert.Equal(0.5, match.GetProperty("confidence").GetDouble());
        var box = match.GetProperty("box");
        Assert.Equal(1.5, box.GetProperty("x").GetDouble());
        Assert.Equal(2, box.GetProperty("y").GetDouble());
        Assert.Equal(10, box.GetProperty("width").GetInt32());
        Assert.Equal(20, box.GetProperty("height").GetInt32());
    }
}
