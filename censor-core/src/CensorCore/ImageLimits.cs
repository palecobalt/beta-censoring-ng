using SixLabors.ImageSharp;

namespace CensorCore;

/// <summary>
/// Limits on decoded image size. A small file can decode to gigabytes (a 400 KB PNG of 20000x20000 pixels takes
/// 1.6 GB), which anyone who can get an image censored could use to exhaust the server's memory.
/// </summary>
public static class ImageLimits {
    /// <summary>Largest still image, in pixels (4 bytes each once decoded). Phone cameras reach about 200 million.</summary>
    public static long MaxPixels { get; set; } = 250_000_000;

    /// <summary>Largest animation censored frame by frame, in pixels over all frames; larger ones are censored as a still image.</summary>
    public static long MaxAnimationPixels { get; set; } = 250_000_000;

    /// <summary>
    /// Reads the image's size without decoding it, and throws <see cref="ImageTooLargeException"/> over the limit.
    /// </summary>
    public static ImageInfo Check(byte[] contents) {
        var info = Image.Identify(contents);
        if ((long)info.Width * info.Height > MaxPixels) {
            throw new ImageTooLargeException(info.Width, info.Height);
        }
        return info;
    }
}

public class ImageTooLargeException : Exception {
    public ImageTooLargeException(int width, int height)
        : base($"The image is too large to censor ({width}x{height}, the limit is {ImageLimits.MaxPixels / 1_000_000} million pixels)") { }
}
