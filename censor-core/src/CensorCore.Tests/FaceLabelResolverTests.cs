using CensorCore.Censoring;
using Xunit;

namespace CensorCore.Tests;

public class FaceLabelResolverTests {
    private static Classification Match(string label, float confidence, float x = 100, float y = 100, float size = 100) =>
        new(new BoundingBox(x, y, x + size, y + size), confidence, label);

    [Fact]
    public void KeepsTheMoreConfidentLabelOfOneFace() {
        var results = new[] { Match("FACE_M", 0.6F), Match("FACE_F", 0.8F, 105, 95), Match("EXPOSED_FEET", 0.9F, 500, 500) };

        var kept = new FaceLabelResolver().TransformResults(results, null).Select(r => r.Label).ToList();

        Assert.Equal(new[] { "FACE_F", "EXPOSED_FEET" }, kept);
    }

    [Fact]
    public void KeepsSeparateFaces() {
        var results = new[] { Match("FACE_M", 0.6F), Match("FACE_F", 0.8F, 400, 100) };

        Assert.Equal(2, new FaceLabelResolver().TransformResults(results, null).Count());
    }

    [Fact]
    public void KeepsTwoFacesOfTheSameLabelThatOverlap() {
        var results = new[] { Match("FACE_F", 0.6F), Match("FACE_F", 0.8F, 110, 100) };

        Assert.Equal(2, new FaceLabelResolver().TransformResults(results, null).Count());
    }

    [Fact]
    public void ComputesOverlap() {
        Assert.Equal(1F, FaceLabelResolver.Overlap(Match("FACE_F", 1).Box, Match("FACE_F", 1).Box));
        Assert.Equal(0F, FaceLabelResolver.Overlap(Match("FACE_F", 1).Box, Match("FACE_F", 1, 300, 300).Box));
        Assert.InRange(FaceLabelResolver.Overlap(Match("FACE_F", 1).Box, Match("FACE_F", 1, 150, 100).Box), 0.33F, 0.34F);
    }
}
