using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Augora.Providers.LocalFiles;

/// <summary>One audio file with its tags.</summary>
/// <param name="Key"><c>{folder name}/{path relative to the folder, with forward slashes}</c>.</param>
internal sealed record LocalTrack(
    string Key,
    string Title,
    string? Artist,
    string? AlbumArtist,
    string? Album,
    uint Disc,
    uint Track,
    uint Year,
    double DurationSeconds,
    bool HasEmbeddedPicture,
    long Size,
    DateTime LastWriteUtc)
{
    [JsonIgnore]
    public string Folder => Key[..Key.IndexOf('/', StringComparison.Ordinal)];

    [JsonIgnore]
    public string RelativePath => Key[(Key.IndexOf('/', StringComparison.Ordinal) + 1)..];

    [JsonIgnore]
    public string EffectiveArtist => AlbumArtist ?? Artist ?? "";

    [JsonIgnore]
    public string? AlbumKey => Album is null ? null : LocalLibrary.Hash($"{EffectiveArtist.ToUpperInvariant()}|{Album.ToUpperInvariant()}");

    [JsonIgnore]
    public string ArtworkToken => LocalLibrary.Hash(Key);
}

/// <summary>An immutable snapshot of the scanned library, replaced as a whole after every scan.</summary>
internal sealed class LocalLibrary
{
    public static readonly FrozenSet<string> Extensions =
        new[] { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly LocalLibrary Empty = new([]);

    public LocalLibrary(IReadOnlyList<LocalTrack> tracks)
    {
        Tracks = tracks;
        ByKey = tracks.ToFrozenDictionary(t => t.Key, StringComparer.Ordinal);
        ByArtworkToken = tracks.ToFrozenDictionary(t => t.ArtworkToken, StringComparer.Ordinal);
        Albums = tracks.Where(t => t.AlbumKey is not null)
            .GroupBy(t => t.AlbumKey!)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<LocalTrack>)g.OrderBy(t => t.Disc).ThenBy(t => t.Track).ThenBy(t => t.Key, StringComparer.Ordinal).ToList());
        Artists = tracks.Where(t => t.EffectiveArtist.Length > 0)
            .GroupBy(t => t.EffectiveArtist, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<LocalTrack>)g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<LocalTrack> Tracks { get; }

    public FrozenDictionary<string, LocalTrack> ByKey { get; }

    public FrozenDictionary<string, LocalTrack> ByArtworkToken { get; }

    public FrozenDictionary<string, IReadOnlyList<LocalTrack>> Albums { get; }

    public FrozenDictionary<string, IReadOnlyList<LocalTrack>> Artists { get; }

    /// <summary>Tracks whose title, artist, album or file name contain every word of the query.</summary>
    public IEnumerable<LocalTrack> Search(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return [];
        }

        return Tracks.Where(t =>
        {
            var text = $"{t.Title} {t.Artist} {t.AlbumArtist} {t.Album} {Path.GetFileNameWithoutExtension(t.RelativePath)}";
            return words.All(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));
        });
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}
