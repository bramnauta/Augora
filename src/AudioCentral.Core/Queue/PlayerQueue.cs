using AudioCentral.Contracts;
using AudioCentral.Core.Media;

namespace AudioCentral.Core.Queue;

public sealed record QueueItem(string Id, MediaItem Item)
{
    public static QueueItem Create(MediaItem item) => new(Guid.NewGuid().ToString("N"), item);
}

/// <summary>
/// The play queue of one player. Not thread-safe: <see cref="QueueManager"/> serialises access.
/// </summary>
public sealed class PlayerQueue(string playerId)
{
    private readonly List<QueueItem> _items = [];

    public string PlayerId { get; } = playerId;

    public IReadOnlyList<QueueItem> Items => _items;

    public int? CurrentIndex { get; private set; }

    public QueueItem? Current => CurrentIndex is { } index ? _items[index] : null;

    public bool Shuffle { get; private set; }

    public RepeatMode Repeat { get; set; }

    /// <summary>Applies <paramref name="option"/> and returns the index that should start playing, if any.</summary>
    public int? Enqueue(IReadOnlyList<MediaItem> items, QueueOption option)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var queueItems = items.Select(QueueItem.Create).ToList();
        switch (option)
        {
            case QueueOption.Replace:
                _items.Clear();
                _items.AddRange(queueItems);
                CurrentIndex = null;
                return 0;

            case QueueOption.Play:
            case QueueOption.Next:
                var insertAt = CurrentIndex is { } current ? current + 1 : _items.Count;
                _items.InsertRange(insertAt, queueItems);
                return option == QueueOption.Play || CurrentIndex is null ? insertAt : null;

            case QueueOption.Add:
                var start = _items.Count;
                _items.AddRange(queueItems);
                return CurrentIndex is null ? start : null;

            default:
                throw new ArgumentOutOfRangeException(nameof(option), option, null);
        }
    }

    public void SetCurrent(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _items.Count);
        CurrentIndex = index;
    }

    /// <summary>The index to play after the current item ends by itself, honouring repeat.</summary>
    public int? GetNextIndexAfterEnd() => Repeat == RepeatMode.One ? CurrentIndex : GetNextIndex();

    /// <summary>The index for an explicit "next" command.</summary>
    public int? GetNextIndex()
    {
        if (_items.Count == 0)
        {
            return null;
        }

        var next = (CurrentIndex ?? -1) + 1;
        if (next < _items.Count)
        {
            return next;
        }

        return Repeat == RepeatMode.All ? 0 : null;
    }

    public int? GetPreviousIndex()
    {
        if (CurrentIndex is not { } current)
        {
            return _items.Count > 0 ? 0 : null;
        }

        if (current > 0)
        {
            return current - 1;
        }

        return Repeat == RepeatMode.All ? _items.Count - 1 : 0;
    }

    /// <summary>Enabling shuffle randomises the items after the current one; disabling keeps the order.</summary>
    public void SetShuffle(bool shuffle)
    {
        Shuffle = shuffle;
        if (!shuffle)
        {
            return;
        }

        var from = (CurrentIndex ?? -1) + 1;
        var remaining = _items.GetRange(from, _items.Count - from);
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(remaining));
        _items.RemoveRange(from, remaining.Count);
        _items.AddRange(remaining);
    }

    public bool Remove(string itemId)
    {
        var index = _items.FindIndex(i => i.Id == itemId);
        if (index < 0)
        {
            return false;
        }

        _items.RemoveAt(index);
        if (CurrentIndex is { } current)
        {
            if (index < current)
            {
                CurrentIndex = current - 1;
            }
            else if (index == current)
            {
                CurrentIndex = null;
            }
        }

        return true;
    }

    public void Clear()
    {
        _items.Clear();
        CurrentIndex = null;
    }

    public QueueDto ToDto() =>
        new(PlayerId, _items.Select(i => new QueueItemDto(i.Id, i.Item.ToDto())).ToList(), CurrentIndex, Shuffle, Repeat);

    public static PlayerQueue FromDto(QueueDto dto)
    {
        var queue = new PlayerQueue(dto.PlayerId) { Repeat = dto.Repeat, Shuffle = dto.Shuffle };
        foreach (var item in dto.Items)
        {
            if (MediaItemRef.TryParse(item.Item.Uri, out _))
            {
                queue._items.Add(new QueueItem(item.Id, MediaItem.FromDto(item.Item)));
            }
        }

        if (dto.CurrentIndex is { } index && index >= 0 && index < queue._items.Count)
        {
            queue.CurrentIndex = index;
        }

        return queue;
    }
}
