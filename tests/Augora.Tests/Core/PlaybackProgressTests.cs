using System.Threading.Channels;
using Augora.Contracts;
using Augora.Core.Events;
using Augora.Core.Media;
using Augora.Core.Players;
using Augora.Core.Providers;
using Augora.Core.Queue;
using Augora.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Augora.Tests.Core;

/// <summary>Items that remember where each user stopped (podcast episodes): saving the position and resuming there.</summary>
public sealed class PlaybackProgressTests
{
    private readonly ProgressProvider _provider = new();
    private readonly FakePlayer _player = new();
    private readonly QueueManager _queues;
    private readonly PlaybackProgressTracker _tracker;
    private readonly PlayerManager _players;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PlaybackProgressTests()
    {
        var sessions = new StreamSessionManager(TimeProvider.System);
        var library = new LibraryService([_provider], NullLogger<LibraryService>.Instance);
        _players = new PlayerManager([new FakePlayerProvider(_player)], new NoEvents(), sessions, NullLogger<PlayerManager>.Instance);
        _queues = new QueueManager(
            _players,
            library,
            new StreamResolver(new HttpClient(), NullLogger<StreamResolver>.Instance),
            sessions,
            new FakeUrls(),
            new MemoryQueueStore(),
            new NoEvents(),
            new FixedUser("alice"),
            new GroupMembership(),
            NullLogger<QueueManager>.Instance);
        _tracker = new PlaybackProgressTracker(_players, _queues, library, NullLogger<PlaybackProgressTracker>.Instance);
        _tracker.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task An_unfinished_item_continues_where_the_user_stopped()
    {
        _provider.Resume = TimeSpan.FromMinutes(12);

        await _queues.PlayAsync(_player.Id, "fake:episode:1", QueueOption.Replace, Ct);

        Assert.Equal(TimeSpan.FromMinutes(12), _player.SeekedTo);
        Assert.Equal("alice", _provider.ResumeUser);
    }

    [Fact]
    public async Task An_item_without_progress_starts_at_the_beginning()
    {
        await _queues.PlayAsync(_player.Id, "fake:episode:1", QueueOption.Replace, Ct);

        Assert.Null(_player.SeekedTo);
    }

    [Fact]
    public async Task The_position_is_saved_on_pause_and_when_playback_stops()
    {
        await _queues.PlayAsync(_player.Id, "fake:episode:1", QueueOption.Replace, Ct);

        _player.SetStatus(PlaybackState.Paused, TimeSpan.FromMinutes(7));
        var paused = await _provider.NextSaveAsync();
        Assert.Equal(("fake:episode:1", "alice", TimeSpan.FromMinutes(7), false), (paused.Item.ToString(), paused.UserId, paused.Position, paused.Ended));

        _player.SetStatus(PlaybackState.Playing, TimeSpan.FromMinutes(8));
        _player.SetStatus(PlaybackState.Idle, null);
        var stopped = await _provider.NextSaveAsync();
        Assert.Equal(TimeSpan.FromMinutes(8), stopped.Position);
    }

    [Fact]
    public async Task Finishing_an_item_marks_it_as_ended()
    {
        await _queues.PlayAsync(_player.Id, "fake:episode:1", QueueOption.Replace, Ct);
        _player.SetStatus(PlaybackState.Playing, TimeSpan.FromMinutes(29));
        Assert.False((await _provider.NextSaveAsync()).Ended);

        _player.Finish();

        var saved = await _provider.NextSaveAsync();
        Assert.True(saved.Ended);
    }

    [Fact]
    public async Task The_start_of_an_item_is_not_saved_so_the_resume_point_is_not_overwritten()
    {
        await _queues.PlayAsync(_player.Id, "fake:episode:1", QueueOption.Replace, Ct);

        _player.SetStatus(PlaybackState.Paused, TimeSpan.FromSeconds(1));

        Assert.Empty(_provider.Saved);
    }

    private sealed record Saved(MediaItemRef Item, string UserId, TimeSpan Position, bool Ended);

    private sealed class ProgressProvider : IMusicProvider, IPlaybackProgressProvider
    {
        private readonly List<Saved> _saved = [];
        private readonly Channel<Saved> _signal = Channel.CreateUnbounded<Saved>();

        public TimeSpan? Resume { get; set; }

        public string? ResumeUser { get; private set; }

        public IReadOnlyList<Saved> Saved
        {
            get
            {
                lock (_saved)
                {
                    return [.. _saved];
                }
            }
        }

        public string Id => "fake";

        public string Name => "Fake";

        public string Type => "fake";

        public bool TracksProgress(MediaItemRef item) => item.Type == MediaType.Episode;

        public Task<TimeSpan?> GetResumePositionAsync(MediaItemRef item, string userId, CancellationToken cancellationToken)
        {
            ResumeUser = userId;
            return Task.FromResult(Resume);
        }

        public Task SaveProgressAsync(MediaItemRef item, string userId, TimeSpan position, TimeSpan? duration, bool ended, CancellationToken cancellationToken)
        {
            var saved = new Saved(item, userId, position, ended);
            lock (_saved)
            {
                _saved.Add(saved);
            }

            _signal.Writer.TryWrite(saved);
            return Task.CompletedTask;
        }

        /// <summary>Saves happen in the background: waits for the next one.</summary>
        public async Task<Saved> NextSaveAsync() => await _signal.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);

        public Task<BrowseResult> BrowseAsync(MediaItemRef? folder, CancellationToken cancellationToken) => Task.FromResult(new BrowseResult(Name, []));

        public Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MediaItem>>([]);

        public Task<MediaItem?> GetItemAsync(MediaItemRef item, CancellationToken cancellationToken) =>
            Task.FromResult<MediaItem?>(new MediaItem(item, $"Episode {item.Id}", IsPlayable: true, Duration: TimeSpan.FromMinutes(30)));

        public Task<StreamSource> ResolveStreamAsync(MediaItemRef item, PlaybackContext context, CancellationToken cancellationToken) =>
            Task.FromResult<StreamSource>(new ResolvedStream(new Uri("http://upstream/audio.mp3"), "audio/mpeg", false, Direct: true));
    }

    private sealed class FakePlayer : IPlayer
    {
        public string Id => "fake:player";

        public string Name => "Fake player";

        public string ProviderId => "fake";

        public PlayerKind Kind => PlayerKind.Speaker;

        public string? Model => null;

        public PlayerFeatures Features => PlayerFeatures.Seek;

        public PlayerStatus Status { get; private set; } = new(true, PlaybackState.Idle, 1, false, null, null);

        public TimeSpan? SeekedTo { get; private set; }

        private PlayMedia? _media;

        public event EventHandler? StatusChanged;

        public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

        public void SetStatus(PlaybackState state, TimeSpan? position)
        {
            var nowPlaying = state == PlaybackState.Idle || _media is null
                ? null
                : new NowPlayingDto(_media.ItemUri, _media.Title, null, null, false, position, _media.Duration);
            Status = Status with { State = state, NowPlaying = nowPlaying };
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Finish() => PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs(PlaybackEndReason.Finished));

        public Task PlayAsync(PlayMedia media, CancellationToken cancellationToken)
        {
            _media = media;
            SetStatus(PlaybackState.Playing, TimeSpan.Zero);
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetVolumeAsync(double level, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
        {
            SeekedTo = position;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlayerProvider(IPlayer player) : IPlayerProvider
    {
        public string Id => "fake";

        public string Name => "Fake";

        public IReadOnlyCollection<IPlayer> Players => [player];

        public event EventHandler<IPlayer>? PlayerAdded
        {
            add { }
            remove { }
        }

        public event EventHandler<IPlayer>? PlayerRemoved
        {
            add { }
            remove { }
        }
    }

    private sealed class FakeUrls : IStreamUrlProvider
    {
        public Uri GetStreamUrl(StreamSession session) => GetAbsoluteUrl(session.RelativePath);

        public Uri GetAbsoluteUrl(string relativePath) => new(new Uri("http://server/"), relativePath.TrimStart('/'));
    }

    private sealed class MemoryQueueStore : IQueueStore
    {
        private readonly Dictionary<string, QueueDto> _queues = [];

        public Task<QueueDto?> LoadAsync(string playerId, CancellationToken cancellationToken) => Task.FromResult(_queues.GetValueOrDefault(playerId));

        public Task SaveAsync(QueueDto queue, CancellationToken cancellationToken)
        {
            _queues[queue.PlayerId] = queue;
            return Task.CompletedTask;
        }
    }

    private sealed class NoEvents : IEventPublisher
    {
        public Task PlayerUpdatedAsync(PlayerDto player) => Task.CompletedTask;

        public Task PlayerRemovedAsync(string playerId) => Task.CompletedTask;

        public Task QueueUpdatedAsync(QueueDto queue) => Task.CompletedTask;

        public Task PodcastEpisodeAddedAsync(string userId, PodcastEpisodeAddedDto episode) => Task.CompletedTask;
    }

    private sealed class FixedUser(string userId) : ICurrentUser
    {
        public string? UserId => userId;
    }
}
