using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Augora.Core.Streaming;

/// <summary>
/// Serves a stream session to a player: relays upstream HTTP audio unchanged, serves files with range support
/// and runs producers (librespot + ffmpeg).
/// </summary>
public sealed class StreamRelay(IHttpClientFactory httpClientFactory, StreamSessionManager sessions, ILogger<StreamRelay> logger)
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

        ByteRange? range = produced.IsSeekable ? GetRange(context, produced.Length!.Value) : null;
        if (range is { Satisfiable: false })
        {
            response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            response.Headers.ContentRange = $"bytes */{produced.Length}";
            return;
        }

        if (range is { } r)
        {
            response.Headers.AcceptRanges = "bytes";
            response.ContentLength = r.Count;
            if (r.Partial)
            {
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = $"bytes {r.Start}-{r.Start + r.Count - 1}/{produced.Length}";
            }
        }

        // Cast devices probe with HEAD; that must not start playback in the producer.
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        // Demuxers read the last bytes of a seekable file for tags: answer without starting (and so restarting) the producer.
        if (range is { Start: > 0 } tail && tail.Start >= produced.Length!.Value - TailBytes)
        {
            await response.Body.WriteAsync(new byte[tail.Count], context.RequestAborted);
            return;
        }

        logger.LogInformation("Producing {ContentType} stream for player {Player} from byte {Start}", produced.ContentType, session.PlayerId, range?.Start ?? 0);
        var started = System.Diagnostics.Stopwatch.StartNew();
        Stream body = new FirstWriteStream(response.Body, () =>
            logger.LogInformation("First audio to player {Player} after {Milliseconds} ms", session.PlayerId, started.ElapsedMilliseconds));
        using var complete = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        RangeStream? ranged = null;
        if (range is { } seek)
        {
            // A start close to the beginning (players reopening after reading the header) produces from the start and
            // skips those bytes; further on the producer starts at the matching time.
            var skip = seek.Start < RestartThreshold ? seek.Start : 0;
            body = ranged = new RangeStream(body, skip, seek.Count, complete.Cancel);
        }

        try
        {
            if (range is { Start: >= RestartThreshold } from)
            {
                await produced.WriteFromAsync!(body, from.Start, complete.Token);
            }
            else
            {
                await produced.WriteAsync(body, complete.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Player {Player} closed the produced stream", session.PlayerId);
            return;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && complete.IsCancellationRequested)
        {
            // The announced length is reached: the producer ran a little longer than estimated.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or AugoraException)
        {
            // Ending the response tells the player the item failed; its queue moves on or stops.
            logger.LogWarning("Producing the stream for player {Player} failed: {Message}", session.PlayerId, ex.Message);
            if (!response.HasStarted)
            {
                response.StatusCode = StatusCodes.Status502BadGateway;
                response.ContentLength = null;
                response.Headers.ContentRange = default;
            }

            return;
        }

        // The producer ended a little before the announced length: fill it up, or the player sees a broken response.
        if (ranged is { Remaining: > 0 } shortBy && !context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Padding the stream for player {Player} with {Bytes} bytes", session.PlayerId, shortBy.Remaining);
            await response.Body.WriteAsync(new byte[shortBy.Remaining], context.RequestAborted);
        }
    }

    /// <summary>
    /// Producers of seekable streams restart for a range further in than this (about 6 s at 320 kbit/s); closer to the
    /// start they produce from the beginning and the bytes before the range are skipped.
    /// </summary>
    private const long RestartThreshold = 256 * 1024;

    /// <summary>A range in the last bytes of a seekable produced stream is a tag probe, answered with zeros.</summary>
    private const long TailBytes = 64 * 1024;

    private readonly record struct ByteRange(long Start, long Count, bool Partial, bool Satisfiable = true);

    /// <summary>The (first) requested range of a stream of <paramref name="length"/> bytes; the whole stream without a Range header.</summary>
    private static ByteRange GetRange(HttpContext context, long length)
    {
        var header = context.Request.GetTypedHeaders().Range;
        if (header is not { Unit.Value: "bytes", Ranges.Count: > 0 })
        {
            return new ByteRange(0, length, Partial: false);
        }

        var first = header.Ranges.First();
        long start;
        long end;
        if (first.From is { } from)
        {
            start = from;
            end = first.To is { } to ? Math.Min(to, length - 1) : length - 1;
        }
        else
        {
            // Suffix range: the last N bytes.
            start = Math.Max(0, length - (first.To ?? 0));
            end = length - 1;
        }

        return start >= length || end < start
            ? new ByteRange(0, 0, Partial: true, Satisfiable: false)
            : new ByteRange(start, end - start + 1, Partial: true);
    }

    /// <summary>
    /// Skips the first bytes a producer writes and passes on exactly <c>count</c> bytes; reaching that calls
    /// <c>onComplete</c>, which stops the producer.
    /// </summary>
    private sealed class RangeStream(Stream inner, long skip, long count, Action onComplete) : Stream
    {
        private long _skip = skip;

        public long Remaining { get; private set; } = count;

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

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_skip > 0)
            {
                var skipped = (int)Math.Min(_skip, buffer.Length);
                _skip -= skipped;
                buffer = buffer[skipped..];
            }

            if (buffer.IsEmpty || Remaining == 0)
            {
                return;
            }

            var part = buffer[..(int)Math.Min(Remaining, buffer.Length)];
            await inner.WriteAsync(part, cancellationToken);
            Remaining -= part.Length;
            if (Remaining == 0)
            {
                onComplete();
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
        // Radio: ask Icecast/Shoutcast for the interleaved metadata (what plays now); it is taken out before the player
        // gets the audio. Other streams get plain audio.
        request.Headers.TryAddWithoutValidation(IcyMetadata.RequestHeader, stream.IsLive ? "1" : "0");
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

        var metaInterval = stream.IsLive && upstream.Headers.TryGetValues(IcyMetadata.IntervalHeader, out var values)
            && int.TryParse(values.FirstOrDefault(), out var interval) && interval > 0
                ? interval
                : (int?)null;
        logger.LogInformation("Relaying {Url} to player {Player}{Metadata}", stream.Url, session.PlayerId, metaInterval is null ? "" : " with stream titles");
        try
        {
            await using var source = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            if (metaInterval is { } every)
            {
                await IcyMetadata.CopyAudioAsync(source, response.Body, every, title =>
                {
                    logger.LogDebug("Player {Player} now plays {Title}", session.PlayerId, title);
                    sessions.SetStreamTitle(session, title);
                }, cancellationToken);
            }
            else
            {
                await source.CopyToAsync(response.Body, BufferSize, cancellationToken);
            }
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
