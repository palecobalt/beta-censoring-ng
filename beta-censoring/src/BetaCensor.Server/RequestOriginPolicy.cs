namespace BetaCensor.Server;

/// <summary>
/// Decides which browser origins may call the server. Without it any web page the user opens could drive the server
/// through /live: WebSocket connections aren't covered by CORS.
/// </summary>
public class RequestOriginPolicy {
    private static readonly string[] ExtensionSchemes = { "chrome-extension", "moz-extension", "safari-web-extension" };
    private readonly HashSet<string> _allowed;
    private readonly bool _allowAll;

    public RequestOriginPolicy(IEnumerable<string>? allowedOrigins) {
        var origins = (allowedOrigins ?? Enumerable.Empty<string>()).Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0).ToList();
        _allowAll = origins.Contains("*");
        _allowed = new HashSet<string>(origins, StringComparer.OrdinalIgnoreCase);
    }

    /// <param name="origin">The request's Origin header, if any.</param>
    /// <param name="requestScheme">The request's scheme (http).</param>
    /// <param name="requestHost">The request's Host header, with the port.</param>
    public bool IsAllowed(string? origin, string requestScheme, string requestHost) {
        // requests from outside a browser (the proxy, the video service, scripts) carry no Origin
        if (string.IsNullOrEmpty(origin) || _allowAll || _allowed.Contains(origin)) {
            return true;
        }
        // also rejects "null" (sandboxed frames, file: pages)
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) {
            return false;
        }
        if (ExtensionSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)) {
            return true;
        }
        // the server's own pages (status page, Swagger UI), opened as localhost or by IP address. Any other host name
        // could be DNS rebinding: a web page whose name has been pointed at this machine.
        return string.Equals(origin, $"{requestScheme}://{requestHost}", StringComparison.OrdinalIgnoreCase)
            && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6);
    }
}
