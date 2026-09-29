using CensorCore;
using CensorCore.Censoring;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BetaCensor.Core.Messaging;

public class CensorImageRequestHandler : IRequestHandler<CensorImageRequest, CensorImageResponse>
{
    private readonly AIService _ai;
    private readonly ICensoringProvider _censor;
    // private readonly MatchOptions? _matchOptions;
    private readonly ILogger<CensorImageRequestHandler> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public CensorImageRequestHandler(AIService aiService, ICensoringProvider censoringProvider, ILogger<CensorImageRequestHandler> logger, IServiceScopeFactory scopeFactory)
    => (_logger, _ai, _censor, _scopeFactory) = (logger, aiService, censoringProvider, scopeFactory);

    public async Task<CensorImageResponse> Handle(CensorImageRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Processing censoring request: {request.RequestId}");
        // var imageUrl = request.ImageDataUrl ?? request.ImageUrl;
        if (!string.IsNullOrWhiteSpace(request.ImageDataUrl) || !string.IsNullOrWhiteSpace(request.ImageUrl)) {
            var timer = new System.Diagnostics.Stopwatch();
            try {
                using var scope = _scopeFactory.CreateScope();
                var matchOptions = scope.ServiceProvider.GetService<MatchOptions>();
                IResultParser? parser = null;
                if (request.CensorOptions.Any()) {
                    parser = new StaticResultsParser(request.CensorOptions);
                }
                var data = await request.LoadBytes();
                if (scope.ServiceProvider.GetService<AnimatedImageCensor>() is { } animated) {
                    timer.Start();
                    if (await animated.CensorAnimated(data, matchOptions, parser) is { } animation) {
                        timer.Stop();
                        _logger.LogInformation($"Censoring completed in {timer.Elapsed.TotalSeconds}s ({request.RequestId}:{animation.MimeType}, animated)");
                        return new CensorImageResponse {
                            RequestId = request.RequestId,
                            CensoredImage = animation,
                            CensoringMetadata = new CensoringSession(timer.Elapsed)
                        };
                    }
                    timer.Reset();
                }
                var result = await _ai.RunModel(data, matchOptions);
                if (result != null) {
                    timer.Start();
                    var censored = await this._censor.CensorImage(result, parser);
                    timer.Stop();
                    _logger.LogInformation($"Censoring completed in {timer.Elapsed.TotalSeconds}s ({request.RequestId}:{censored.MimeType})");
                    return new CensorImageResponse {
                        RequestId = request.RequestId,
                        CensoredImage = censored,
                        ImageResult = result,
                        CensoringMetadata = new CensoringSession(timer.Elapsed)
                    };
                } else {
                    return MessageResponse.GetError<CensorImageResponse>(request.RequestId, "AI model failed to process requested image!");
                }
            } catch (Exception e) {
                return MessageResponse.GetError<CensorImageResponse>(request.RequestId, $"Error encountered while censoring this image: {e.ToString()} ({e.Message})");
            }
        } else {
            return MessageResponse.GetError<CensorImageResponse>(request.RequestId, "Could not determine image URL from request!");
        }
    }
}
