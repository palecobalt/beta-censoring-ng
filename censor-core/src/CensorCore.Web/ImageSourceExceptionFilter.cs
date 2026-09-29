using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CensorCore.Web;

/// <summary>
/// Answers requests for images from unsupported sources (local files, browser-internal URLs) with 400, and images
/// that couldn't be downloaded with 502, instead of 500.
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
        } else if (context.Exception is HttpRequestException or TaskCanceledException { InnerException: TimeoutException }) {
            context.Result = new ObjectResult(new ProblemDetails {
                Status = StatusCodes.Status502BadGateway,
                Title = "Could not download the image",
                Detail = context.Exception.Message
            }) { StatusCode = StatusCodes.Status502BadGateway };
            context.ExceptionHandled = true;
        }
    }
}
