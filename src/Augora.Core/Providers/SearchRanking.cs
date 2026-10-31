using System.Globalization;
using System.Text;
using Augora.Core.Media;

namespace Augora.Core.Providers;

/// <summary>
/// Scores how well a search result matches the query, so the best match comes first whichever provider it is from:
/// "NPO 3FM" finds the radio station, "Yellow Submarine" the Spotify track.
/// </summary>
public static class SearchRanking
{
    private const int Exact = 100;
    private const int Prefix = 80;
    private const int Contains = 60;
    private const int AllWords = 40;

    /// <summary>Higher is a better match; 0 when the name hardly resembles the query.</summary>
    public static int Score(string query, MediaItem item)
    {
        var q = Compact(query);
        var name = Compact(item.Name);
        if (q.Length == 0 || name.Length == 0)
        {
            return 0;
        }

        if (name == q)
        {
            return Exact;
        }

        if (name.StartsWith(q, StringComparison.Ordinal))
        {
            return Prefix;
        }

        if (name.Contains(q, StringComparison.Ordinal))
        {
            return Contains;
        }

        // "beatles yellow submarine": every word of the query is somewhere in the name or the subtitle (the artist).
        var haystack = name + Compact(item.Subtitle ?? "");
        var words = query.Split([' ', '-', ',', '.', '\''], StringSplitOptions.RemoveEmptyEntries).Select(Compact).Where(w => w.Length > 0).ToList();
        var found = words.Count(haystack.Contains);
        return words.Count == 0 ? 0 : AllWords * found / words.Count;
    }

    /// <summary>Lowercase letters and digits only, without accents: "NPO 3 FM" and "npo3fm" compare equal.</summary>
    private static string Compact(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
