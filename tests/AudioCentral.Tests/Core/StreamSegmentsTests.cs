using AudioCentral.Core.Players;

namespace AudioCentral.Tests.Core;

public sealed class StreamSegmentsTests
{
    private static PlayMedia Media(string title) =>
        new(new Uri("http://server/api/stream/x"), "/api/stream/x", "audio/mpeg", false, $"test:track:{title}", title, null, null, null, Continuous: true);

    [Fact]
    public void The_item_at_a_stream_position_and_the_position_in_it_are_found()
    {
        var segments = new StreamSegments();
        segments.Reset(Media("one"));
        segments.Add(Media("two"), TimeSpan.FromSeconds(183));
        segments.Add(Media("three"), TimeSpan.FromSeconds(400));

        Assert.Equal(("one", TimeSpan.FromSeconds(10)), Describe(segments.At(TimeSpan.FromSeconds(10))));
        Assert.Equal(("two", TimeSpan.FromSeconds(17)), Describe(segments.At(TimeSpan.FromSeconds(200))));
        Assert.Equal(("three", TimeSpan.Zero), Describe(segments.At(TimeSpan.FromSeconds(400))));
        Assert.Equal(TimeSpan.FromSeconds(400), segments.NextOffsetAfter(TimeSpan.FromSeconds(200)));
    }

    [Fact]
    public void A_new_stream_forgets_the_old_items()
    {
        var segments = new StreamSegments();
        segments.Reset(Media("one"));
        segments.Add(Media("two"), TimeSpan.FromSeconds(10));
        segments.Reset(Media("new"));

        Assert.Equal("new", segments.At(TimeSpan.FromSeconds(60))!.Value.Media.Title);
    }

    private static (string, TimeSpan) Describe((PlayMedia Media, TimeSpan Position)? segment) => (segment!.Value.Media.Title, segment.Value.Position);
}
