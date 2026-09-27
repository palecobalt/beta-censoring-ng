using CensorCore.Censoring;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;


namespace CensorCore.Web;
[ApiController]
[Route("[controller]")]
public class CensoringController : ControllerBase
{
    private readonly AIService _ai;
    private readonly ICensoringProvider _censor;

    public CensoringController(AIService aiService, ICensoringProvider censoringProvider)
    {
        this._ai = aiService;
        this._censor = censoringProvider;
    }

    [HttpGet("info")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetInfo([FromServices]IImageHandler imageHandler, [FromServices]IEnumerable<CensorCore.Censoring.ICensorTypeProvider> types, [FromServices]GlobalCensorOptions? options = null) {
        return Ok(new {
            version = CoreManager.GetCoreVersion(),
            imageHandler = imageHandler.GetType().Name,
            provider = _censor.GetType().Name,
            types = types.Select(t => t.GetType().Name.Replace("Provider", string.Empty)).ToArray(),
            options = options,
        });
    }



    [HttpPost("censorImage")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CensoredImage>> CensorImage([FromBody]CensorImageRequestBody requestBody, [FromQuery] bool returnEncoded = false) {
        var imageUrl = requestBody.ImageDataUrl ?? requestBody.ImageUrl;
        if (!string.IsNullOrWhiteSpace(imageUrl)) {
            // use the host's configured match options (if it registers any) instead of the built-in defaults
            var matchOptions = HttpContext.RequestServices.GetService(typeof(MatchOptions)) as MatchOptions;
            IResultParser? parser = null;
            if (requestBody.CensorOptions != null && requestBody.CensorOptions.Any()) {
                parser = new StaticResultsParser(requestBody.CensorOptions);
            }
            var imageData = await ImageSharpHandler.LoadBytes(imageUrl);
            // animated GIFs are censored frame by frame when the host registers support for them
            var animated = HttpContext.RequestServices.GetService(typeof(AnimatedImageCensor)) as AnimatedImageCensor;
            var censored = animated == null ? null : await animated.CensorAnimatedGif(imageData, matchOptions, parser);
            if (censored == null) {
                var result = await this._ai.RunModel(imageData, matchOptions);
                if (result == null) {
                    return UnprocessableEntity();
                }
                censored = await this._censor.CensorImage(result, parser);
            }
            if (returnEncoded) {
                return Ok(new { imageUrl = censored.ImageDataUrl, imageType = censored.MimeType});
            }
            return File(censored.ImageContents, censored.MimeType);
        } else {
            return BadRequest();
        }
    }

    /// <summary>
    /// Runs the model on an image and returns the matches (label, confidence and box) without censoring it.
    /// </summary>
    [HttpPost("detect")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DetectionResult>> Detect([FromBody]DetectImageRequestBody requestBody) {
        var imageUrl = requestBody.ImageDataUrl ?? requestBody.ImageUrl;
        if (string.IsNullOrWhiteSpace(imageUrl)) {
            return BadRequest();
        }
        try {
            var imageData = await ImageSharpHandler.LoadBytes(imageUrl);
            var result = await GetDetector().Detect(imageData, GetMatchOptions(), GetParser(requestBody.CensorOptions), requestBody.Transform);
            return result == null ? UnprocessableEntity() : Ok(result);
        } catch (SixLabors.ImageSharp.ImageFormatException) {
            return UnprocessableEntity();
        }
    }

    /// <summary>
    /// Runs the model on several images (such as video frames) and returns the matches for each, in the same order.
    /// </summary>
    [HttpPost("detectBatch")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DetectionResult[]>> DetectBatch([FromBody]DetectImagesRequestBody requestBody) {
        if (!requestBody.ImageDataUrls.Any() || requestBody.ImageDataUrls.Any(string.IsNullOrWhiteSpace)) {
            return BadRequest();
        }
        try {
            var images = new List<byte[]>();
            foreach (var imageUrl in requestBody.ImageDataUrls) {
                images.Add(await ImageSharpHandler.LoadBytes(imageUrl));
            }
            var results = await GetDetector().DetectMany(images, GetMatchOptions(), GetParser(requestBody.CensorOptions), requestBody.Transform);
            return results.Any(r => r == null) ? UnprocessableEntity() : Ok(results);
        } catch (SixLabors.ImageSharp.ImageFormatException) {
            return UnprocessableEntity();
        }
    }

    // use the host's configured match options, censor options and result transformers (if it registers any)
    private MatchDetector GetDetector() =>
        new(_ai, HttpContext.RequestServices.GetService<GlobalCensorOptions>(), HttpContext.RequestServices.GetServices<IResultsTransformer>());

    private MatchOptions? GetMatchOptions() => HttpContext.RequestServices.GetService<MatchOptions>();

    private static IResultParser? GetParser(Dictionary<string, ImageCensorOptions>? censorOptions) =>
        censorOptions != null && censorOptions.Any() ? new StaticResultsParser(censorOptions) : null;

    [HttpGet("getCensored")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetCensoredImage([FromQuery] string url) {
        if (!string.IsNullOrWhiteSpace(url)) {
            var result = await this._ai.RunModel(url);
            if (result != null) {
                var defaults = new Dictionary<string, ImageCensorOptions> {
                    ["EXPOSED_BREAST_F"] = new ImageCensorOptions("pixelate") { Level = 10 },
                    ["FACE_F"] = new ImageCensorOptions("blur") { Level = 10 },
                    ["EXPOSED_GENITALIA_F"] = new ImageCensorOptions("blackbars") { Level = 15 },
                    ["EXPOSED_BUTTOCKS"] = new ImageCensorOptions("pixelate") { Level = 14 }
                };
                var censored = await this._censor.CensorImage(result, new StaticResultsParser(defaults));
                return File(censored.ImageContents, censored.MimeType);
            } else {
                return UnprocessableEntity();
            }
        } else {
            return BadRequest();
        }
    }
}
