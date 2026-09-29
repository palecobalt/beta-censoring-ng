using System.Collections.Concurrent;

namespace CensorCore.Censoring;

/// <summary>
/// Random choices (stickers, captions) shared by the frames of one animation, so a censored area keeps the same
/// sticker or caption from frame to frame instead of flickering between them.
/// </summary>
public static class AnimationChoices {
    private static readonly AsyncLocal<ConcurrentDictionary<string, Lazy<Task<object?>>>?> Choices = new();

    /// <summary>
    /// Shares choices from now on in the calling async flow and the tasks it starts (the frames of one animation).
    /// </summary>
    public static void Begin() => Choices.Value = new();

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
