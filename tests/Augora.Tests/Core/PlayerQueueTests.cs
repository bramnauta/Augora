using Augora.Contracts;
using Augora.Core.Media;
using Augora.Core.Queue;

namespace Augora.Tests.Core;

public sealed class PlayerQueueTests
{
    private static MediaItem Item(string id) => new(new MediaItemRef("test", MediaType.Track, id), id, IsPlayable: true);

    private static List<string> Names(PlayerQueue queue) => queue.Items.Select(i => i.Item.Name).ToList();

    [Fact]
    public void Replace_clears_the_queue_and_starts_at_the_first_item()
    {
        var queue = new PlayerQueue("p");
        queue.Enqueue([Item("a"), Item("b")], QueueOption.Replace);
        queue.SetCurrent(1);

        var start = queue.Enqueue([Item("c")], QueueOption.Replace);

        Assert.Equal(0, start);
        Assert.Equal(["c"], Names(queue));
        Assert.Null(queue.CurrentIndex);
    }

    [Fact]
    public void Next_inserts_after_the_current_item_without_interrupting()
    {
        var queue = new PlayerQueue("p");
        queue.Enqueue([Item("a"), Item("b")], QueueOption.Replace);
        queue.SetCurrent(0);

        var start = queue.Enqueue([Item("n")], QueueOption.Next);

        Assert.Null(start);
        Assert.Equal(["a", "n", "b"], Names(queue));
    }

    [Fact]
    public void Play_inserts_after_the_current_item_and_plays_it()
    {
        var queue = new PlayerQueue("p");
        queue.Enqueue([Item("a"), Item("b")], QueueOption.Replace);
        queue.SetCurrent(0);

        Assert.Equal(1, queue.Enqueue([Item("n")], QueueOption.Play));
    }

    [Fact]
    public void Add_to_an_idle_queue_starts_playing_the_added_item()
    {
        var queue = new PlayerQueue("p");

        Assert.Equal(0, queue.Enqueue([Item("a")], QueueOption.Add));
    }

    [Theory]
    [InlineData(RepeatMode.Off, null)]
    [InlineData(RepeatMode.All, 0)]
    [InlineData(RepeatMode.One, 1)]
    public void Honours_repeat_at_the_end_of_the_queue(RepeatMode repeat, int? expected)
    {
        var queue = new PlayerQueue("p") { Repeat = repeat };
        queue.Enqueue([Item("a"), Item("b")], QueueOption.Replace);
        queue.SetCurrent(1);

        Assert.Equal(expected, queue.GetNextIndexAfterEnd());
    }

    [Fact]
    public void Removing_an_item_before_the_current_one_keeps_the_current_item()
    {
        var queue = new PlayerQueue("p");
        queue.Enqueue([Item("a"), Item("b"), Item("c")], QueueOption.Replace);
        queue.SetCurrent(2);

        queue.Remove(queue.Items[0].Id);

        Assert.Equal("c", queue.Current?.Item.Name);
    }

    [Fact]
    public void Shuffle_keeps_played_items_in_place()
    {
        var queue = new PlayerQueue("p");
        queue.Enqueue(Enumerable.Range(0, 20).Select(i => Item(i.ToString())).ToList(), QueueOption.Replace);
        queue.SetCurrent(4);

        queue.SetShuffle(true);

        Assert.Equal(["0", "1", "2", "3", "4"], Names(queue).Take(5));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => i.ToString()).Order(), Names(queue).Order());
    }

    [Fact]
    public void Survives_a_round_trip_through_its_dto()
    {
        var queue = new PlayerQueue("p") { Repeat = RepeatMode.All };
        queue.Enqueue([Item("a"), Item("b")], QueueOption.Replace);
        queue.SetCurrent(1);

        var restored = PlayerQueue.FromDto(queue.ToDto());

        Assert.Equal(queue.ToDto().Items, restored.ToDto().Items);
        Assert.Equal(1, restored.CurrentIndex);
        Assert.Equal(RepeatMode.All, restored.Repeat);
    }
}
