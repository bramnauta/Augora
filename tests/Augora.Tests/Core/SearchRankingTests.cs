using Augora.Contracts;
using Augora.Core.Media;
using Augora.Core.Providers;

namespace Augora.Tests.Core;

public class SearchRankingTests
{
    private static MediaItem Item(string name, string? subtitle = null) =>
        new(new MediaItemRef("p", MediaType.Track, name), name, subtitle, IsPlayable: true);

    private static readonly string[] Names = ["NPO 3FM", "NPO 3FM Serious Radio", "Het beste van NPO 3FM", "Radio 538"];

    [Fact]
    public void Exact_beats_prefix_beats_contains_beats_unrelated()
    {
        var scores = Names
            .Select(n => SearchRanking.Score("NPO 3FM", Item(n))).ToArray();

        Assert.Equal(scores.OrderByDescending(s => s), scores);
        Assert.Equal(scores.Length, scores.Distinct().Count());
    }

    [Fact]
    public void Spacing_case_and_accents_are_ignored() =>
        Assert.Equal(100, SearchRanking.Score("npo 3 fm", Item("NPO 3FM")));

    [Fact]
    public void Query_words_found_in_the_subtitle_count() =>
        Assert.True(SearchRanking.Score("beatles yellow submarine", Item("Yellow Submarine - Remastered", "The Beatles · Revolver")) > 0);
}
