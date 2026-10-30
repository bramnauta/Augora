using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace Augora.Core.Streaming;

/// <summary>
/// Shoutcast/Icecast in-stream metadata: with <c>Icy-MetaData: 1</c> the server puts a metadata block after every
/// <c>icy-metaint</c> bytes of audio. The blocks are taken out, so players get plain audio, and their
/// <c>StreamTitle</c> (usually "Artist - Title") is reported.
/// </summary>
public static partial class IcyMetadata
{
    public const string RequestHeader = "Icy-MetaData";
    public const string IntervalHeader = "icy-metaint";

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>Copies the audio of <paramref name="source"/> to <paramref name="destination"/> without the metadata blocks.</summary>
    /// <param name="onTitle">Called with every new stream title (empty when the station clears it).</param>
    public static async Task CopyAudioAsync(Stream source, Stream destination, int interval, Action<string> onTitle, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(interval, 255 * 16));
        string? last = null;
        try
        {
            while (true)
            {
                // Audio up to the next block.
                var remaining = interval;
                while (remaining > 0)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), cancellationToken);
                    if (read == 0)
                    {
                        return;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    remaining -= read;
                }

                // One length byte (in units of 16 bytes), then the block; most are empty.
                var length = source.ReadByte();
                if (length < 0)
                {
                    return;
                }

                if (length == 0)
                {
                    continue;
                }

                await source.ReadExactlyAsync(buffer.AsMemory(0, length * 16), cancellationToken);
                if (ParseStreamTitle(buffer.AsSpan(0, length * 16)) is { } title && title != last)
                {
                    last = title;
                    onTitle(title);
                }
            }
        }
        catch (EndOfStreamException)
        {
            // The station closed the stream in the middle of a block.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The <c>StreamTitle</c> of a metadata block, or <c>null</c> when it has none.</summary>
    public static string? ParseStreamTitle(ReadOnlySpan<byte> block)
    {
        var end = block.IndexOf((byte)0);
        var bytes = end < 0 ? block : block[..end];
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Older stations send Latin-1.
            text = Encoding.Latin1.GetString(bytes);
        }

        var match = StreamTitlePattern().Match(text);
        return match.Success ? match.Groups["title"].Value.Trim() : null;
    }

    /// <summary>Splits "Artist - Title"; without a separator it is all title.</summary>
    public static (string? Artist, string Title) SplitTitle(string streamTitle)
    {
        var separator = streamTitle.IndexOf(" - ", StringComparison.Ordinal);
        return separator > 0
            ? (streamTitle[..separator].Trim(), streamTitle[(separator + 3)..].Trim())
            : (null, streamTitle);
    }

    // The value ends at the quote before the next ';' (titles may contain quotes themselves).
    [GeneratedRegex("StreamTitle='(?<title>.*?)'(?:;|$)", RegexOptions.Singleline)]
    private static partial Regex StreamTitlePattern();
}
