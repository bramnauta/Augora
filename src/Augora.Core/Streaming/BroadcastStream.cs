using Microsoft.Extensions.Logging;

namespace Augora.Core.Streaming;

/// <summary>
/// Reads a stream source once and serves it to several players (the members of a sync group). Every reader of a
/// track starts at the beginning; readers of a live stream start near the newest data. A group therefore needs one
/// upstream connection, and for Spotify one account.
/// </summary>
public sealed class BroadcastStream : IAsyncDisposable
{
    private const int LiveWindowBytes = 1024 * 1024;
    private const int LivePrerollBytes = 128 * 1024;
    private const long MaxBufferedBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);

    private readonly StreamSource _source;
    private readonly StreamRelay _relay;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly List<byte[]> _chunks = [];
    private readonly Dictionary<int, long> _readerPositions = [];
    private readonly CancellationTokenSource _producerCancellation = new();
    private TaskCompletionSource _dataAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _firstChunk;
    private long _bufferedBytes;
    private bool _completed;
    private Exception? _error;
    private Task? _producer;
    private int _nextReaderId;
    private DateTimeOffset _lastReaderLeft = DateTimeOffset.UtcNow;

    public BroadcastStream(StreamSource source, StreamRelay relay, ILogger logger)
    {
        _source = source;
        _relay = relay;
        _logger = logger;
    }

    public bool IsLive => _source.IsLive;

    /// <summary>Copies the broadcast to <paramref name="output"/> until the source ends or the reader goes away.</summary>
    public async Task CopyToAsync(Stream output, CancellationToken cancellationToken)
    {
        int readerId;
        long index;
        lock (_lock)
        {
            // The producer outlives this reader: other members keep listening.
            _producer ??= Task.Run(ProduceAsync, CancellationToken.None);
            readerId = _nextReaderId++;
            index = IsLive ? StartOfLiveWindow() : _firstChunk;
            _readerPositions[readerId] = index;
        }

        try
        {
            while (true)
            {
                byte[]? chunk = null;
                Task wait;
                lock (_lock)
                {
                    index = Math.Max(index, _firstChunk);
                    if (index - _firstChunk < _chunks.Count)
                    {
                        chunk = _chunks[(int)(index - _firstChunk)];
                    }
                    else if (_completed)
                    {
                        if (_error is not null && index == 0)
                        {
                            throw new IOException("The stream source failed.", _error);
                        }

                        return;
                    }

                    wait = _dataAvailable.Task;
                }

                if (chunk is null)
                {
                    await wait.WaitAsync(cancellationToken);
                    continue;
                }

                await output.WriteAsync(chunk, cancellationToken);
                index++;
                lock (_lock)
                {
                    _readerPositions[readerId] = index;
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _readerPositions.Remove(readerId);
                if (_readerPositions.Count == 0)
                {
                    _lastReaderLeft = DateTimeOffset.UtcNow;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _producerCancellation.CancelAsync();
        if (_producer is not null)
        {
            await _producer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _producerCancellation.Dispose();
    }

    private long StartOfLiveWindow()
    {
        long bytes = 0;
        var start = _firstChunk + _chunks.Count;
        for (var i = _chunks.Count - 1; i >= 0 && bytes < LivePrerollBytes; i--)
        {
            bytes += _chunks[i].Length;
            start--;
        }

        return start;
    }

    private async Task ProduceAsync()
    {
        using var watchdog = new Timer(_ => StopWhenAbandoned(), null, IdleTimeout, IdleTimeout);
        try
        {
            await using var sink = new SinkStream(this);
            await _relay.WriteToAsync(_source, sink, _producerCancellation.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Group stream source failed");
            _error = ex;
        }
        catch (OperationCanceledException)
        {
            // Every member stopped listening.
        }
        finally
        {
            lock (_lock)
            {
                _completed = true;
                Signal();
            }
        }
    }

    /// <summary>A live stream nobody listens to anymore must not keep its upstream (or Spotify account) busy.</summary>
    private void StopWhenAbandoned()
    {
        lock (_lock)
        {
            if (_readerPositions.Count == 0 && DateTimeOffset.UtcNow - _lastReaderLeft > IdleTimeout && !_completed)
            {
                _producerCancellation.Cancel();
            }
        }
    }

    private void Append(byte[] chunk)
    {
        lock (_lock)
        {
            _chunks.Add(chunk);
            _bufferedBytes += chunk.Length;

            // Drop what every reader has passed, once the buffer is large (tracks) or beyond the live window.
            var limit = IsLive ? LiveWindowBytes : MaxBufferedBytes;
            var slowest = _readerPositions.Count == 0 ? (IsLive ? long.MaxValue : _firstChunk) : _readerPositions.Values.Min();
            while (_bufferedBytes > limit && _chunks.Count > 1 && (IsLive || _firstChunk < slowest))
            {
                _bufferedBytes -= _chunks[0].Length;
                _chunks.RemoveAt(0);
                _firstChunk++;
            }

            Signal();
        }
    }

    private void Signal()
    {
        var previous = _dataAvailable;
        _dataAvailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    /// <summary>The stream the source writes into; every write becomes a chunk.</summary>
    private sealed class SinkStream(BroadcastStream owner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => owner.Append(buffer.AsSpan(offset, count).ToArray());

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            owner.Append(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
