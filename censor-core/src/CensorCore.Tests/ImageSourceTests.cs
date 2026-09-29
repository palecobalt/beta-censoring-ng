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
}
