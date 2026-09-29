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
}
