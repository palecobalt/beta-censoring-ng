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
}
