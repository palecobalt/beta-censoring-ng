using BetaCensor.Core.Messaging;
using CensorCore;
using Xunit;

namespace BetaCensor.Server.Tests;

public class RequestImageLoaderTests {
    // 1x1 GIFs, one white and one black
    private const string White = "data:image/gif;base64,R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==";
    private const string Black = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==";
    private const string ErrorPage = "data:text/html;base64,PGh0bWw+Rm9yYmlkZGVuPC9odG1sPg==";

    private static byte[] Bytes(string dataUrl) => Convert.FromBase64String(dataUrl.Split(',')[1]);

    [Fact]
    public async Task PrefersTheDataUrl() {
        var request = new CensorImageRequest { ImageDataUrl = White, ImageUrl = Black };

        Assert.Equal(Bytes(White), await request.LoadBytes());
    }

    [Fact]
    public async Task UsesTheUrlWhenTheDataUrlIsNoImage() {
        // what the extension sends when its own fetch got an error page
        var request = new CensorImageRequest { ImageDataUrl = ErrorPage, ImageUrl = Black };

        Assert.Equal(Bytes(Black), await request.LoadBytes());
    }

    [Fact]
    public async Task RefusesLocalFilesAsTheUrl() {
        var request = new CensorImageRequest { ImageDataUrl = ErrorPage, ImageUrl = "file:///etc/hostname" };

        await Assert.ThrowsAsync<UnsupportedImageSourceException>(() => request.LoadBytes());
    }
}
