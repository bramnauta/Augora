using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AudioCentral.Core.Streaming;

/// <summary>
/// Serves a stream session to a player: relays upstream HTTP audio unchanged, serves files with range support
/// and runs producers (librespot + ffmpeg).
/// </summary>
public sealed class StreamRelay(IHttpClientFactory httpClientFactory, ILogger<StreamRelay> logger)
{
    private const int BufferSize = 64 * 1024;

    public Task ServeAsync(StreamSession session, HttpContext context) => session.Stream switch
    {
        ResolvedStream resolved => RelayAsync(session, resolved, context),
        FileStreamSource file => Results.File(file.Path, file.ContentType, enableRangeProcessing: true).ExecuteAsync(context),
        ProducedStreamSource produced => ProduceAsync(session, produced, context),
        var other => throw new NotSupportedException($"Stream source {other.GetType().Name} cannot be served."),
    };

    private async Task ProduceAsync(StreamSession session, ProducedStreamSource produced, HttpContext context)
    {
        var response = context.Response;
        response.ContentType = produced.ContentType;
        response.Headers.CacheControl = "no-cache, no-store";
        // Cast devices probe with HEAD; that must not start playback in the producer.
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        logger.LogInformation("Producing {ContentType} stream for player {Player}", produced.ContentType, session.PlayerId);
        try
        {
            await produced.WriteAsync(response.Body, context.RequestAborted);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Player {Player} closed the produced stream", session.PlayerId);
        }
    }

    private async Task RelayAsync(StreamSession session, ResolvedStream stream, HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
        // Without this Icecast/Shoutcast servers send plain audio without interleaved metadata blocks.
        request.Headers.TryAddWithoutValidation("Icy-MetaData", "0");
        if (!stream.IsLive && context.Request.Headers.Range is { Count: > 0 } range)
        {
            request.Headers.TryAddWithoutValidation("Range", range.ToString());
        }

        var client = httpClientFactory.CreateClient(StreamResolver.HttpClientName);
        using var upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!upstream.IsSuccessStatusCode)
        {
            logger.LogWarning("Upstream {Url} returned {Status} for player {Player}", stream.Url, (int)upstream.StatusCode, session.PlayerId);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        var response = context.Response;
        response.StatusCode = (int)upstream.StatusCode;
        response.ContentType = stream.ContentType;
        response.Headers.CacheControl = "no-cache, no-store";
        if (!stream.IsLive)
        {
            response.ContentLength = upstream.Content.Headers.ContentLength;
            if (upstream.Content.Headers.ContentRange is { } contentRange)
            {
                response.Headers.ContentRange = contentRange.ToString();
            }

            if (upstream.Headers.AcceptRanges.Count > 0)
            {
                response.Headers.AcceptRanges = string.Join(',', upstream.Headers.AcceptRanges);
            }
        }

        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        logger.LogInformation("Relaying {Url} to player {Player}", stream.Url, session.PlayerId);
        try
        {
            await using var source = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            await source.CopyToAsync(response.Body, BufferSize, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The player disconnected: normal for live streams.
        }
        catch (IOException ex) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Player {Player} closed the stream", session.PlayerId);
        }
    }
}
