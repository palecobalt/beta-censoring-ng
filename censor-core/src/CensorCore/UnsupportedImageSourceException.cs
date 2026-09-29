namespace CensorCore;

/// <summary>
/// An image was requested from somewhere other than a data: URI or an http(s) URL, such as a local file or a
/// browser-internal URL (moz-extension:, blob:).
/// </summary>
public class UnsupportedImageSourceException : ArgumentException
{
    public UnsupportedImageSourceException(string source)
        : base($"Unsupported image source ({Describe(source)}): only data: URIs and http(s) URLs are accepted") { }

    // the scheme is enough to act on; the rest of the source may be long or private
    private static string Describe(string source) =>
        !source.StartsWith('/') && Uri.TryCreate(source, UriKind.Absolute, out var uri) ? uri.Scheme + ":" : "relative URL or path";
}
