using CensorCore.Censoring;
using Xunit;

namespace CensorCore.Tests;

public class AnimationChoicesTests {
    private static int _counter;
    private static Task<string?> Next() => Task.FromResult<string?>(Interlocked.Increment(ref _counter).ToString());

    [Fact]
    public async Task ChoosesEveryTimeOutsideAnAnimation() {
        var first = await AnimationChoices.Choose("sticker|FACE_F|pack", Next);
        var second = await AnimationChoices.Choose("sticker|FACE_F|pack", Next);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task SharesChoicesAcrossTheFramesOfOneAnimation() {
        var (face, other) = await Animation();

        Assert.Single(face.Distinct());
        Assert.Single(other.Distinct());
        Assert.NotEqual(face[0], other[0]);
    }

    [Fact]
    public async Task EachAnimationChoosesAgain() {
        var (first, _) = await Animation();
        var (second, _) = await Animation();

        Assert.NotEqual(first[0], second[0]);
    }

    // like AnimatedImageCensor: Begin, then the frames in parallel
    private static async Task<(List<string> Face, List<string> Other)> Animation() {
        AnimationChoices.Begin();
        var face = new System.Collections.Concurrent.ConcurrentBag<string>();
        var other = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(Enumerable.Range(0, 20), async (_, _) => {
            face.Add((await AnimationChoices.Choose("sticker|FACE_F|pack", Next))!);
            other.Add((await AnimationChoices.Choose("sticker|EXPOSED_FEET|pack", Next))!);
        });
        return (face.ToList(), other.ToList());
    }

    [Fact]
    public void TellsAreasApartByPosition() {
        static Classification Face(float x, float y) => new(new BoundingBox(x, y, x + 100, y + 120), 1F, "FACE_F");
        AnimationChoices.Begin();

        var left = AnimationChoices.GetArea(Face(100, 100));
        var right = AnimationChoices.GetArea(Face(400, 100));

        Assert.NotEqual(left, right);
        // the same faces a frame later, moved a little
        Assert.Equal(left, AnimationChoices.GetArea(Face(130, 110)));
        Assert.Equal(right, AnimationChoices.GetArea(Face(380, 90)));
        // another label in the same place is another area
        Assert.NotEqual(left, AnimationChoices.GetArea(new Classification(new BoundingBox(100, 100, 200, 220), 1F, "FACE_M")));
    }
}
