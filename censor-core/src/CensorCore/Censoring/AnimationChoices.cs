using System.Collections.Concurrent;

namespace CensorCore.Censoring;

/// <summary>
/// Random choices (stickers, captions) shared by the frames of one animation, so a censored area keeps the same
/// sticker or caption from frame to frame instead of flickering between them.
/// </summary>
public static class AnimationChoices {
    private static readonly AsyncLocal<ConcurrentDictionary<string, Lazy<Task<object?>>>?> Choices = new();
    private static readonly AsyncLocal<List<(string Label, float X, float Y, float Size)>?> Areas = new();

    /// <summary>
    /// Shares choices from now on in the calling async flow and the tasks it starts (the frames of one animation).
    /// </summary>
    public static void Begin() {
        Choices.Value = new();
        Areas.Value = new();
    }

    /// <summary>
    /// Names the area a match belongs to, for use in a choice's key: matches with the same label whose centres are
    /// within one box size of each other, in any frame, are the same area. Two faces side by side are two areas.
    /// </summary>
    public static string GetArea(Classification match) {
        var areas = Areas.Value;
        if (areas == null) {
            return match.Label;
        }
        var (x, y) = (match.Box.X + match.Box.Width / 2F, match.Box.Y + match.Box.Height / 2F);
        var size = Math.Max(match.Box.Width, match.Box.Height);
        lock (areas) {
            var nearest = -1;
            var nearestDistance = float.MaxValue;
            for (int i = 0; i < areas.Count; i++) {
                var distance = MathF.Sqrt(MathF.Pow(areas[i].X - x, 2) + MathF.Pow(areas[i].Y - y, 2));
                if (areas[i].Label == match.Label && distance < Math.Max(size, areas[i].Size) && distance < nearestDistance) {
                    (nearest, nearestDistance) = (i, distance);
                }
            }
            if (nearest < 0) {
                areas.Add((match.Label, x, y, size));
                nearest = areas.Count - 1;
            }
            return $"{match.Label}#{nearest}";
        }
    }

    /// <summary>
    /// Makes the choice for <paramref name="key"/> once per animation; outside an animation, every time.
    /// </summary>
    public static async Task<T?> Choose<T>(string key, Func<Task<T?>> choose) where T : class {
        var choices = Choices.Value;
        if (choices == null) {
            return await choose();
        }
        var choice = choices.GetOrAdd(key, _ => new Lazy<Task<object?>>(async () => await choose()));
        return (T?)await choice.Value;
    }
}
