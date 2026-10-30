using Augora.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Augora.Tests.Groups;

public sealed class BroadcastStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static StreamRelay Relay() => new(new NoHttpClientFactory(), new StreamSessionManager(TimeProvider.System), NullLogger<StreamRelay>.Instance);

    [Fact]
    public async Task Every_reader_of_a_track_gets_all_of_it_and_the_source_is_read_once()
    {
        var produced = 0;
        var source = new ProducedStreamSource("audio/mpeg", async (output, ct) =>
        {
            Interlocked.Increment(ref produced);
            for (byte i = 0; i < 5; i++)
            {
                await output.WriteAsync(new[] { i, i }, ct);
                await Task.Delay(20, ct);
            }
        });
        await using var broadcast = new BroadcastStream(source, Relay(), NullLogger.Instance);

        using var first = new MemoryStream();
        using var second = new MemoryStream();
        await Task.WhenAll(broadcast.CopyToAsync(first, Ct), broadcast.CopyToAsync(second, Ct));
        using var late = new MemoryStream();
        await broadcast.CopyToAsync(late, Ct);

        byte[] expected = [0, 0, 1, 1, 2, 2, 3, 3, 4, 4];
        Assert.Equal(expected, first.ToArray());
        Assert.Equal(expected, second.ToArray());
        Assert.Equal(expected, late.ToArray());
        Assert.Equal(1, produced);
    }

    [Fact]
    public async Task A_late_reader_of_a_live_stream_starts_near_the_newest_data()
    {
        var release = new TaskCompletionSource();
        var source = new ProducedStreamSource("audio/mpeg", async (output, ct) =>
        {
            // 4 MiB "already played", then wait so the stream is still live when the second reader joins.
            for (var i = 0; i < 64; i++)
            {
                await output.WriteAsync(new byte[64 * 1024], ct);
            }

            await release.Task.WaitAsync(ct);
        }, IsLive: true);
        await using var broadcast = new BroadcastStream(source, Relay(), NullLogger.Instance);

        using var early = new CountingStream();
        var earlyTask = broadcast.CopyToAsync(early, Ct);
        await Task.Delay(300, Ct);
        using var late = new CountingStream();
        var lateTask = broadcast.CopyToAsync(late, Ct);
        await Task.Delay(300, Ct);
        release.SetResult();
        await Task.WhenAll(earlyTask, lateTask);

        Assert.True(late.Written < early.Written, $"late {late.Written} early {early.Written}");
        Assert.True(late.Written <= 1024 * 1024, $"late reader got {late.Written} bytes");
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No HTTP in this test.");
    }

    private sealed class CountingStream : MemoryStream
    {
        public long Written { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Written += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }
}
