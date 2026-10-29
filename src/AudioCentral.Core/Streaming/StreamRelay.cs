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

    /// <summary>Writes the complete audio of <paramref name="source"/> to <paramref name="output"/> (used for group broadcasts).</summary>
    public async Task WriteToAsync(StreamSource source, Stream output, CancellationToken cancellationToken)
    {
        switch (source)
        {
            case ResolvedStream resolved:
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, resolved.Url);
                request.Headers.TryAddWithoutValidation("Icy-MetaData", "0");
                using var upstream = await httpClientFactory.CreateClient(StreamResolver.HttpClientName)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                upstream.EnsureSuccessStatusCode();
                await using var body = await upstream.Content.ReadAsStreamAsync(cancellationToken);
                await body.CopyToAsync(output, BufferSize, cancellationToken);
                break;
            }

            case FileStreamSource file:
            {
                await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
                await stream.CopyToAsync(output, BufferSize, cancellationToken);
                break;
            }

            case ProducedStreamSource produced:
                await produced.WriteAsync(output, cancellationToken);
                break;

            default:
                throw new NotSupportedException($"Stream source {source.GetType().Name} cannot be broadcast.");
        }
    }

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
        var started = System.Diagnostics.Stopwatch.StartNew();
        var body = new FirstWriteStream(response.Body, () =>
            logger.LogInformation("First audio to player {Player} after {Milliseconds} ms", session.PlayerId, started.ElapsedMilliseconds));
        try
        {
            await produced.WriteAsync(body, context.RequestAborted);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Player {Player} closed the produced stream", session.PlayerId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or AudioCentralException)
        {
            // Ending the response tells the player the item failed; its queue moves on or stops.
            logger.LogWarning("Producing the stream for player {Player} failed: {Message}", session.PlayerId, ex.Message);
            if (!response.HasStarted)
            {
                response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    /// <summary>Passes writes through and reports the first one: how long a producer took to deliver audio.</summary>
    private sealed class FirstWriteStream(Stream inner, Action onFirstWrite) : Stream
    {
        private Action? _onFirstWrite = onFirstWrite;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Report(count);
            inner.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Report(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        private void Report(int count)
        {
            if (count > 0 && Interlocked.Exchange(ref _onFirstWrite, null) is { } report)
            {
                report();
            }
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
