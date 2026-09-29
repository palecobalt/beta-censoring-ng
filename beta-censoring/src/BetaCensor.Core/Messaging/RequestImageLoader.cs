using CensorCore;
using SixLabors.ImageSharp;

namespace BetaCensor.Core.Messaging;

public static class RequestImageLoader {
    /// <summary>
    /// Reads the request's image: its data URL if that holds an image, otherwise its image URL.
    /// </summary>
    public static async Task<byte[]> LoadBytes(this CensorImageRequest request) {
        var hasUrl = !string.IsNullOrWhiteSpace(request.ImageUrl);
        if (!string.IsNullOrWhiteSpace(request.ImageDataUrl)) {
            try {
                var data = await ImageSharpHandler.LoadBytes(request.ImageDataUrl);
                // the extension sends whatever it fetched, which can be an error page instead of the image
                if (!hasUrl || IsImage(data)) {
                    return data;
                }
            } catch when (hasUrl) {
                // try the URL instead
            }
        }
        return await ImageSharpHandler.LoadBytes(System.Web.HttpUtility.UrlDecode(request.ImageUrl!));
    }

    private static bool IsImage(byte[] data) {
        try {
            Image.DetectFormat(data);
            return true;
        } catch (UnknownImageFormatException) {
            return false;
        }
    }
}
