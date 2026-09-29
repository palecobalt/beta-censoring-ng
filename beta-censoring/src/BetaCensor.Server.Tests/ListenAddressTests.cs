using Xunit;

namespace BetaCensor.Server.Tests;

public class ListenAddressTests {
    [Theory]
    [InlineData(null, "http://localhost:2382", true)]
    [InlineData("localhost", "http://localhost:2382", true)]
    [InlineData("127.0.0.1", "http://127.0.0.1:2382", true)]
    [InlineData("::1", "http://[::1]:2382", true)]
    [InlineData("*", "http://*:2382", false)]
    [InlineData("0.0.0.0", "http://*:2382", false)]
    [InlineData("192.168.1.20", "http://192.168.1.20:2382", false)]
    public void BuildsTheListenUrl(string? address, string url, bool localOnly) {
        var options = new ServerOptions { ListenAddress = address! };

        Assert.Equal(url, options.GetListenUrl());
        Assert.Equal(localOnly, options.IsLocalOnly());
    }

    [Fact]
    public void DefaultsToThisComputerOnly() {
        var options = new ServerOptions();

        Assert.Equal("http://localhost:2382", options.GetListenUrl());
        Assert.True(options.IsLocalOnly());
    }
}
