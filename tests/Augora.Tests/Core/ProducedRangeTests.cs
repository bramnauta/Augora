using Augora.Core.Streaming;
using Augora.Tests.Groups;
using Microsoft.AspNetCore.Http;

namespace Augora.Tests.Core;

/// <summary>Produced streams of known length are served with byte ranges, so players can seek in them.</summary>
public sealed class ProducedRangeTests
{
    private const long Length = 1_000_000;

    private readonly List<long> _starts = [];

    [Fact]
    public async Task Without_a_range_the_whole_item_is_announced_and_produced()
    {
        var (context, body) = Request(null);

        await Serve(context, produced: 400_000);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges);
        Assert.Equal(Length, context.Response.ContentLength);
        // The producer ended early: the rest is padded, so the response has the announced length.
        Assert.Equal(Length, body.Length);
        Assert.Equal([0L], _starts);
    }

    [Fact]
    public async Task A_range_further_in_starts_the_producer_there()
    {
        var (context, body) = Request("bytes=500000-");

        await Serve(context, produced: 2_000_000);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes 500000-999999/1000000", context.Response.Headers.ContentRange);
        // A producer running longer than estimated is cut off at the announced length.
        Assert.Equal(500_000, body.Length);
        Assert.Equal([500_000L], _starts);
    }

    [Fact]
    public async Task A_range_near_the_start_produces_from_the_beginning_and_skips()
    {
        var (context, body) = Request("bytes=1000-");

        await Serve(context, produced: Length);

        Assert.Equal(Length - 1000, body.Length);
        Assert.Equal((byte)(1000 % 251), body.ToArray()[0]);
        Assert.Equal([0L], _starts);
    }

    [Fact]
    public async Task A_tag_probe_at_the_end_does_not_start_the_producer()
    {
        var (context, body) = Request("bytes=999872-");

        await Serve(context, produced: Length);

        Assert.Equal(128, body.Length);
        Assert.Empty(_starts);
    }

    [Fact]
    public async Task A_range_past_the_end_is_refused()
    {
        var (context, _) = Request("bytes=2000000-");

        await Serve(context, produced: Length);

        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, context.Response.StatusCode);
    }

    private static (DefaultHttpContext Context, MemoryStream Body) Request(string? range)
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Method = HttpMethods.Get;
        if (range is not null)
        {
            context.Request.Headers.Range = range;
        }

        return (context, body);
    }

    private Task Serve(HttpContext context, long produced)
    {
        async Task Write(Stream output, long start, CancellationToken ct)
        {
            _starts.Add(start);
            var chunk = new byte[16 * 1024];
            for (long written = 0; written < produced; written += chunk.Length)
            {
                for (var i = 0; i < chunk.Length; i++)
                {
                    chunk[i] = (byte)((start + written + i) % 251);
                }

                await output.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, produced - written)), ct);
            }
        }

        var source = new ProducedStreamSource("audio/mpeg", (output, ct) => Write(output, 0, ct), Length: Length, WriteFromAsync: Write);
        var session = new StreamSession("token", "player", source, DateTimeOffset.UtcNow);
        return BroadcastStreamTests.Relay().ServeAsync(session, context);
    }
}
