using System.Net;
using System.Net.Sockets;
using Xunit;

namespace CensorCore.Tests;

public class ImageSourceTests {
    // 1x1 transparent GIF
    private const string Pixel = "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

    [Fact]
    public async Task ReadsDataUris() {
        var bytes = await ImageSharpHandler.LoadBytes("data:image/gif;base64," + Pixel);

        Assert.Equal(Convert.FromBase64String(Pixel), bytes);
    }

    [Theory]
    [InlineData("/etc/hostname")]
    [InlineData("file:///etc/hostname")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("images/photo.jpg")]
    [InlineData("moz-extension://0f6c9b5e/images/error.jpg")]
    [InlineData("ftp://example.com/photo.jpg")]
    public async Task RefusesEverythingButDataAndHttp(string source) {
        await Assert.ThrowsAsync<UnsupportedImageSourceException>(() => ImageSharpHandler.LoadBytes(source));
    }

    [Fact]
    public async Task ImageHandlerRefusesLocalFilesByDefault() {
        var path = Path.GetTempFileName();
        try {
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(Pixel));

            await Assert.ThrowsAsync<UnsupportedImageSourceException>(() => new ImageSharpHandler().LoadImage(path));
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadsLocalFilesOnlyWhenAllowed() {
        var path = Path.GetTempFileName();
        try {
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(Pixel));

            Assert.Equal(Convert.FromBase64String(Pixel), await ImageSharpHandler.LoadBytes(path, allowLocalFiles: true));
            Assert.Equal(Convert.FromBase64String(Pixel), await ImageSharpHandler.LoadBytes(new Uri(path).AbsoluteUri, allowLocalFiles: true));
            var image = await new ImageSharpHandler { AllowLocalFiles = true }.LoadImage(path);
            Assert.Equal(1, image.SourceImage.Width);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void ErrorNamesOnlyTheScheme() {
        var error = new UnsupportedImageSourceException("file:///home/someone/private/photo.jpg");

        Assert.Contains("file:", error.Message);
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task TreatsProtocolRelativeUrlsAsHttps() {
        // nothing listens on port 1: a download is attempted rather than the source being refused
        await Assert.ThrowsAsync<HttpRequestException>(() => ImageSharpHandler.LoadBytes("//localhost:1/photo.jpg"));
    }

    [Theory]
    [InlineData("https://i.pximg.net/img-master/img/2024/01/01/00/00/00/1_p0.jpg", "https://www.pixiv.net/")]
    [InlineData("https://images.example.com:8443/a/b.jpg?x=1", "https://images.example.com:8443/")]
    public void SendsTheSiteAsReferrer(string image, string referrer) {
        Assert.Equal(new Uri(referrer), ImageSharpHandler.GetReferrer(new Uri(image)));
    }

    [Fact]
    public async Task DownloadsLikeABrowser() {
        // a server with hotlink protection: images only for requests with a browser User-Agent and a Referer
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () => {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer);
            var request = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            var allowed = request.Contains("User-Agent: Mozilla/5.0") && request.Contains($"Referer: http://127.0.0.1:{port}/");
            var body = allowed ? Convert.FromBase64String(Pixel) : Array.Empty<byte>();
            var head = allowed ? "HTTP/1.1 200 OK" : "HTTP/1.1 403 Forbidden";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"{head}\r\nContent-Type: image/gif\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(body);
            return request;
        });
        try {
            // %2B must reach the server as it is
            var bytes = await ImageSharpHandler.LoadBytes($"http://127.0.0.1:{port}/images/a%2Bb.gif");

            Assert.Equal(Convert.FromBase64String(Pixel), bytes);
            Assert.StartsWith("GET /images/a%2Bb.gif ", await served);
        } finally {
            listener.Stop();
        }
    }
}
