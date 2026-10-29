using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AudioCentral.Providers.Spotify.Librespot;

/// <summary>
/// Reads the raw PCM librespot writes to its output pipe (a FIFO on Linux, a named pipe on Windows) and hands it to
/// the stream that is currently attached. Without an attached stream the audio is dropped.
/// </summary>
internal sealed class PcmPipe : IDisposable
{
    private const int ChunkSize = 16 * 1024;

    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _lock = new();
    private Channel<byte[]>? _sink;
    private Thread? _thread;

    private PcmPipe(string path, ILogger logger)
    {
        Path = path;
        _logger = logger;
    }

    /// <summary>The path librespot opens for writing.</summary>
    public string Path { get; }

    public static PcmPipe Create(string directory, string name, ILogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            return new PcmPipe($@"\\.\pipe\audiocentral-{name}", logger);
        }

        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "audio.pcm");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var mkfifo = Process.Start(new ProcessStartInfo("mkfifo", [path]) { UseShellExecute = false, RedirectStandardError = true })
            ?? throw new InvalidOperationException("Could not start mkfifo.");
        var error = mkfifo.StandardError.ReadToEnd();
        mkfifo.WaitForExit();
        if (mkfifo.ExitCode != 0)
        {
            throw new InvalidOperationException($"mkfifo failed for '{path}': {error.Trim()}");
        }

        return new PcmPipe(path, logger);
    }

    public void Start()
    {
        // Blocking reads on a dedicated thread: FIFOs do not support asynchronous I/O.
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = $"librespot-pipe {Path}" };
        _thread.Start();
    }

    /// <summary>Starts receiving audio; a previously attached reader is completed.</summary>
    public ChannelReader<byte[]> Attach()
    {
        // ~64 chunks of 16 KiB is about six seconds of CD audio; when full librespot waits, which paces it.
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
        lock (_lock)
        {
            _sink?.Writer.TryComplete();
            _sink = channel;
        }

        return channel.Reader;
    }

    public bool IsAttached(ChannelReader<byte[]> reader)
    {
        lock (_lock)
        {
            return _sink?.Reader == reader;
        }
    }

    public void Detach(ChannelReader<byte[]> reader)
    {
        lock (_lock)
        {
            if (_sink?.Reader == reader)
            {
                _sink.Writer.TryComplete();
                _sink = null;
            }
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        lock (_lock)
        {
            _sink?.Writer.TryComplete();
        }

        if (!OperatingSystem.IsWindows() && File.Exists(Path))
        {
            File.Delete(Path);
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[ChunkSize];
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                using var stream = Open();
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    Deliver(buffer.AsSpan(0, read).ToArray());
                }
            }
            catch (Exception ex) when (!_stopping.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Reading the librespot pipe failed, reopening");
                Thread.Sleep(500);
            }
            catch
            {
                return;
            }
        }
    }

    private Stream Open()
    {
        if (OperatingSystem.IsWindows())
        {
            var server = new NamedPipeServerStream(Path[@"\\.\pipe\".Length..], PipeDirection.In, 1);
            server.WaitForConnectionAsync(_stopping.Token).GetAwaiter().GetResult();
            return server;
        }

        // Blocks until librespot opens the FIFO for writing; returns end-of-file when it closes it.
        return new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
    }

    private void Deliver(byte[] chunk)
    {
        Channel<byte[]>? sink;
        lock (_lock)
        {
            sink = _sink;
        }

        if (sink is null)
        {
            return;
        }

        try
        {
            sink.Writer.WriteAsync(chunk, _stopping.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (ChannelClosedException)
        {
            // The stream ended or another one was attached meanwhile.
        }
    }
}
