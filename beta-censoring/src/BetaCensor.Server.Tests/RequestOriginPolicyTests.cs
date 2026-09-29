using Xunit;

namespace BetaCensor.Server.Tests;

public class RequestOriginPolicyTests {
    private readonly RequestOriginPolicy _policy = new(null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chrome-extension://ddhcngdcbfjbnomcfmiljgcbmobofgib")]
    [InlineData("moz-extension://8a1c7c4e-3a58-4e4b-9d0f-2f7c1b1a6f10")]
    [InlineData("safari-web-extension://4B2B5E4A-1C1A-4F5B-9A1E-6C1D2E3F4A5B")]
    public void AllowsClientsAndExtensions(string? origin) {
        Assert.True(_policy.IsAllowed(origin, "http", "localhost:2382"));
    }

    [Theory]
    [InlineData("http://localhost:2382", "localhost:2382")]
    [InlineData("http://127.0.0.1:2382", "127.0.0.1:2382")]
    [InlineData("http://[::1]:2382", "[::1]:2382")]
    [InlineData("http://192.168.1.20:2382", "192.168.1.20:2382")]
    public void AllowsItsOwnPages(string origin, string host) {
        Assert.True(_policy.IsAllowed(origin, "http", host));
    }

    [Theory]
    // any other web page
    [InlineData("https://example.com", "localhost:2382")]
    [InlineData("http://localhost:3000", "localhost:2382")]
    // a web page whose name was pointed at this machine (DNS rebinding) looks same-origin
    [InlineData("http://attacker.example:2382", "attacker.example:2382")]
    [InlineData("http://localhost.attacker.example:2382", "localhost.attacker.example:2382")]
    // sandboxed frames and file: pages
    [InlineData("null", "localhost:2382")]
    public void RefusesWebPages(string origin, string host) {
        Assert.False(_policy.IsAllowed(origin, "http", host));
    }

    [Fact]
    public void AllowsConfiguredOrigins() {
        var policy = new RequestOriginPolicy(new[] { "https://gallery.example/" });

        Assert.True(policy.IsAllowed("https://gallery.example", "http", "localhost:2382"));
        Assert.False(policy.IsAllowed("https://other.example", "http", "localhost:2382"));
    }

    [Fact]
    public void StarAllowsEveryOrigin() {
        Assert.True(new RequestOriginPolicy(new[] { "*" }).IsAllowed("https://example.com", "http", "localhost:2382"));
    }

    [Theory]
    // GET requests carry no Origin. From an extension:
    [InlineData("localhost:2382", "none", null, true)]
    // from the server's own pages:
    [InlineData("localhost:2382", "same-origin", "http://localhost:2382/", true)]
    // from outside a browser (the proxy in Docker, scripts):
    [InlineData("beta-censoring:2382", null, null, true)]
    [InlineData("192.168.1.20:2382", null, null, true)]
    [InlineData("mediapc.local:2382", null, null, true)]
    // an <img> or fetch on some web page:
    [InlineData("localhost:2382", "cross-site", "https://example.com/", false)]
    [InlineData("localhost:2382", "same-site", "http://localhost:3000/", false)]
    // same-origin GET after DNS rebinding:
    [InlineData("attacker.example:2382", "same-origin", "http://attacker.example:2382/", false)]
    [InlineData("attacker.example:2382", null, null, false)]
    [InlineData("", null, null, false)]
    public void ChecksRequestsWithoutOrigin(string host, string? fetchSite, string? referrer, bool allowed) {
        Assert.Equal(allowed, _policy.IsAllowed(null, "http", host, fetchSite, referrer));
    }

    [Fact]
    public void AllowsConfiguredHostsAndPagesWithoutOrigin() {
        var policy = new RequestOriginPolicy(new[] { "https://gallery.example" }, new[] { "censor.example.org" });

        Assert.True(policy.IsAllowed(null, "http", "censor.example.org:2382"));
        Assert.True(policy.IsAllowed(null, "http", "localhost:2382", "cross-site", "https://gallery.example/album/1"));
        Assert.False(policy.IsAllowed(null, "http", "localhost:2382", "cross-site", "https://other.example/"));
    }

    [Fact]
    public void StarOriginStillChecksTheHost() {
        var policy = new RequestOriginPolicy(new[] { "*" });

        Assert.False(policy.IsAllowed("http://attacker.example:2382", "http", "attacker.example:2382"));
        Assert.True(new RequestOriginPolicy(new[] { "*" }, new[] { "*" }).IsAllowed("http://attacker.example:2382", "http", "attacker.example:2382"));
    }

    [Fact]
    public void LimitsExtensionsWhenConfigured() {
        var policy = new RequestOriginPolicy(allowedExtensions: new[] { "ddhcngdcbfjbnomcfmiljgcbmobofgib", "moz-extension://8a1c7c4e-3a58-4e4b-9d0f-2f7c1b1a6f10/" });

        Assert.True(policy.IsAllowed("chrome-extension://ddhcngdcbfjbnomcfmiljgcbmobofgib", "http", "localhost:2382"));
        Assert.True(policy.IsAllowed("moz-extension://8a1c7c4e-3a58-4e4b-9d0f-2f7c1b1a6f10", "http", "localhost:2382"));
        Assert.False(policy.IsAllowed("chrome-extension://aaaabbbbccccddddeeeeffffgggghhhh", "http", "localhost:2382"));
    }
}
