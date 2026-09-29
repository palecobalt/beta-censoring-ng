using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CensorCore.Web;

/// <summary>
/// Answers requests for images from unsupported sources (local files, browser-internal URLs) with 400 instead of 500.
/// </summary>
public class ImageSourceExceptionFilter : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is UnsupportedImageSourceException e) {
            context.Result = new BadRequestObjectResult(new ProblemDetails {
                Status = StatusCodes.Status400BadRequest,
                Title = "Unsupported image source",
                Detail = e.Message
            });
            context.ExceptionHandled = true;
        }
    }
}
