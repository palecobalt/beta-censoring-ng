using CensorCore.Censoring;

namespace CensorCore.Web;

public class DetectImageRequestBody : CensorImageRequestBody {
    /// <summary>
    /// Applies the host's result transformers (censor scaling and box merging) to the matches, as censoring would.
    /// </summary>
    public bool Transform {get;set;} = true;
}

public class DetectImagesRequestBody {
    /// <summary>
    /// The images to run the model on, as data URLs (or image URLs).
    /// </summary>
    public List<string> ImageDataUrls {get;set;} = new List<string>();
    public Dictionary<string, ImageCensorOptions> CensorOptions {get;set;} = new Dictionary<string, ImageCensorOptions>();

    /// <inheritdoc cref="DetectImageRequestBody.Transform"/>
    public bool Transform {get;set;} = true;
}
