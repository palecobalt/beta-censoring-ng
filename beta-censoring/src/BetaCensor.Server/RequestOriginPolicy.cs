namespace BetaCensor.Server;

/// <summary>
/// Decides which browser requests may use the server. Without it any web page the user opens could drive the server:
/// WebSocket connections and plain GET requests aren't covered by CORS.
/// </summary>
public class RequestOriginPolicy {
    private static readonly string[] ExtensionSchemes = { "chrome-extension", "moz-extension", "safari-web-extension" };
    private readonly HashSet<string> _origins;
    private readonly HashSet<string> _hosts;
    private readonly HashSet<string> _extensions;
    private readonly bool _anyOrigin;
    private readonly bool _anyHost;

    /// <param name="allowedOrigins">Web page origins allowed besides extensions and the server's own pages; "*" allows any.</param>
    /// <param name="allowedHosts">Host names the server may be addressed by, besides localhost, IP addresses and local names; "*" allows any.</param>
    /// <param name="allowedExtensions">Extension ids (or origins) allowed; empty allows every extension.</param>
    public RequestOriginPolicy(IEnumerable<string>? allowedOrigins = null, IEnumerable<string>? allowedHosts = null, IEnumerable<string>? allowedExtensions = null) {
        _origins = Clean(allowedOrigins, o => o.TrimEnd('/'));
        _hosts = Clean(allowedHosts, h => h);
        // an id, or an origin such as chrome-extension://id
        _extensions = Clean(allowedExtensions, e => e.TrimEnd('/').Split("://").Last());
        _anyOrigin = _origins.Contains("*");
        _anyHost = _hosts.Contains("*");
    }

    private static HashSet<string> Clean(IEnumerable<string>? values, Func<string, string> normalize) =>
        new((values ?? Enumerable.Empty<string>()).Select(v => normalize(v.Trim())).Where(v => v.Length > 0), StringComparer.OrdinalIgnoreCase);

    /// <param name="origin">The Origin header, if any. Browsers send it on WebSocket connections and POST requests.</param>
    /// <param name="requestScheme">The request's scheme (http).</param>
    /// <param name="requestHost">The Host header, with the port.</param>
    /// <param name="fetchSite">The Sec-Fetch-Site header, if any. Browsers send it to localhost and https servers.</param>
    /// <param name="referrer">The Referer header, if any.</param>
    public bool IsAllowed(string? origin, string requestScheme, string requestHost, string? fetchSite = null, string? referrer = null) {
        // a name that isn't this machine's could be a web page's own name pointed at this machine (DNS rebinding),
        // which makes the page's requests same-origin
        if (!IsKnownHost(requestHost)) {
            return false;
        }
        if (_anyOrigin) {
            return true;
        }
        if (!string.IsNullOrEmpty(origin)) {
            return IsAllowedOrigin(origin, requestScheme, requestHost);
        }
        // no Origin: a client outside a browser (the proxy, the video service, scripts), an extension's GET request
        // (Sec-Fetch-Site: none), the server's own pages (same-origin), or a GET from some web page (cross-site, same-site)
        if (string.IsNullOrEmpty(fetchSite) || fetchSite is "none" or "same-origin") {
            return true;
        }
        return Uri.TryCreate(referrer, UriKind.Absolute, out var page) && _origins.Contains(page.GetLeftPart(UriPartial.Authority));
    }

    private bool IsAllowedOrigin(string origin, string requestScheme, string requestHost) {
        if (_origins.Contains(origin.TrimEnd('/'))) {
            return true;
        }
        // also rejects "null" (sandboxed frames, file: pages)
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) {
            return false;
        }
        if (ExtensionSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)) {
            return _extensions.Count == 0 || _extensions.Contains(uri.Host);
        }
        // the server's own pages (status page, Swagger UI)
        return string.Equals(origin, $"{requestScheme}://{requestHost}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the Host header names this machine: localhost, an IP address, a name without dots (Docker service and
    /// machine names) or under .local, or a configured name. Public DNS names, which anyone can point here, don't.
    /// </summary>
    public bool IsKnownHost(string requestHost) {
        if (_anyHost) {
            return true;
        }
        if (!Uri.TryCreate($"http://{requestHost}", UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) {
            return false;
        }
        var host = uri.Host.TrimEnd('.');
        return uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            || !host.Contains('.')
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || _hosts.Contains(host);
    }
}
