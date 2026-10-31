using Augora.Contracts;
using Augora.Core;
using Augora.Core.Events;
using Augora.Core.Media;
using Augora.Core.Players;
using Augora.Core.Providers;
using Augora.Core.Queue;
using Augora.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Augora.Tests.Core;

/// <summary>Announcements (TTS) on a player: the music continues afterwards and the volume returns.</summary>
public sealed class AnnounceTests
{
    private const string AnnouncementUrl = "http://ha.local:8123/api/tts_proxy/hello.mp3";

    private readonly FakePlayer _player = new();
    private readonly QueueManager _queues;
    private readonly GroupMembership _membership = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AnnounceTests()
    {
        var sessions = new StreamSessionManager(TimeProvider.System);
        var players = new PlayerManager([new FakePlayerProvider(_player)], new NoEvents(), sessions, NullLogger<PlayerManager>.Instance);
        _queues = new QueueManager(
            players,
            new LibraryService([new FakeMusicProvider()], NullLogger<LibraryService>.Instance),
            new StreamResolver(new HttpClient(), NullLogger<StreamResolver>.Instance),
            sessions,
            new FakeUrls(),
            new MemoryQueueStore(),
            new NoEvents(),
            new NoUser(),
            _membership,
            NullLogger<QueueManager>.Instance);
    }

    [Fact]
    public async Task The_music_continues_where_it_was_after_the_announcement()
    {
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct, following: ["fake:track:2"]);
        _player.Position = TimeSpan.FromSeconds(42);

        await _queues.AnnounceAsync(_player.Id, new AnnounceRequest(AnnouncementUrl), Ct);

        Assert.Equal(["fake:track:1", "augora:announcement", "fake:track:1"], _player.Played.Select(p => p.ItemUri));
        Assert.Equal(AnnouncementUrl, _player.Played[1].StreamUrl.ToString());
        Assert.Equal(TimeSpan.FromSeconds(42), _player.Played[2].StartPosition);
        Assert.Equal(0, (await _queues.GetQueueAsync(_player.Id, Ct)).CurrentIndex);
    }

    [Fact]
    public async Task The_announcement_plays_at_the_given_volume_and_the_volume_returns()
    {
        _player.Volume = 0.3;
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct);

        await _queues.AnnounceAsync(_player.Id, new AnnounceRequest(AnnouncementUrl, Volume: 0.8), Ct);

        Assert.Equal([0.8, 0.3], _player.VolumeChanges);
        Assert.Equal(0.8, _player.VolumeDuringAnnouncement);
    }

    [Fact]
    public async Task Without_a_volume_the_volume_is_left_alone()
    {
        await _queues.AnnounceAsync(_player.Id, new AnnounceRequest(AnnouncementUrl), Ct);

        Assert.Empty(_player.VolumeChanges);
    }

    [Fact]
    public async Task An_idle_player_stays_idle_afterwards()
    {
        _player.State = PlaybackState.Idle;

        await _queues.AnnounceAsync(_player.Id, new AnnounceRequest(AnnouncementUrl), Ct);

        Assert.Equal(["augora:announcement"], _player.Played.Select(p => p.ItemUri));
    }

    [Fact]
    public async Task Resume_can_be_turned_off()
    {
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct);

        await _queues.AnnounceAsync(_player.Id, new AnnounceRequest(AnnouncementUrl, Resume: false), Ct);

        Assert.Equal(["fake:track:1", "augora:announcement"], _player.Played.Select(p => p.ItemUri));
    }

    [Fact]
    public async Task The_queue_does_not_advance_when_the_announcement_ends()
    {
        await _queues.PlayAsync(_player.Id, "fake:track:1", QueueOption.Replace, Ct, following: ["fake:track:2"]);

        await _queues.AnnounceAsync(_player.Id, new AnnounceRequest(AnnouncementUrl, Resume: false), Ct);
        await Task.Delay(100, Ct);

        Assert.Equal(0, (await _queues.GetQueueAsync(_player.Id, Ct)).CurrentIndex);
        Assert.DoesNotContain(_player.Played, p => p.ItemUri == "fake:track:2");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path.mp3")]
    [InlineData("file:///etc/passwd")]
    public async Task Only_absolute_http_urls_are_announced(string url)
    {
        var ex = await Assert.ThrowsAsync<AugoraException>(() => _queues.AnnounceAsync(_player.Id, new AnnounceRequest(url), Ct));

        Assert.Equal(400, ex.StatusCode);
        Assert.Empty(_player.Played);
    }

    [Fact]
    public async Task A_member_of_a_playing_group_cannot_be_announced_on()
    {
        _membership.Join(_player.Id, "group:1");

        var ex = await Assert.ThrowsAsync<AugoraException>(() => _queues.AnnounceAsync(_player.Id, new AnnounceRequest(AnnouncementUrl), Ct));

        Assert.Equal(409, ex.StatusCode);
    }

    private sealed class FakeMusicProvider : IMusicProvider
    {
        public string Id => "fake";

        public string Name => "Fake";

        public string Type => "fake";

        public Task<BrowseResult> BrowseAsync(MediaItemRef? folder, CancellationToken cancellationToken) => Task.FromResult(new BrowseResult(Name, []));

        public Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MediaItem>>([]);

        public Task<MediaItem?> GetItemAsync(MediaItemRef item, CancellationToken cancellationToken) =>
            Task.FromResult<MediaItem?>(new MediaItem(item, $"Track {item.Id}", IsPlayable: true, Duration: TimeSpan.FromMinutes(3)));

        public Task<StreamSource> ResolveStreamAsync(MediaItemRef item, PlaybackContext context, CancellationToken cancellationToken) =>
            Task.FromResult<StreamSource>(new ProducedStreamSource("audio/mpeg", (_, _) => Task.CompletedTask));
    }

    private sealed class FakePlayer : IPlayer
    {
        public string Id => "fake:player";

        public string Name => "Fake player";

        public string ProviderId => "fake";

        public PlayerKind Kind => PlayerKind.Speaker;

        public string? Model => null;

        public PlayerFeatures Features => PlayerFeatures.Volume;

        public PlaybackState State { get; set; } = PlaybackState.Playing;

        public double Volume { get; set; } = 0.5;

        public TimeSpan Position { get; set; }

        public List<PlayMedia> Played { get; } = [];

        public List<double> VolumeChanges { get; } = [];

        public double? VolumeDuringAnnouncement { get; private set; }

        public PlayerStatus Status => new(true, State, Volume, false,
            Played.LastOrDefault(p => p.ItemUri != "augora:announcement") is { } media
                ? new NowPlayingDto(media.ItemUri, media.Title, null, null, false, Position, media.Duration)
                : null,
            null);

        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

        public Task PlayAsync(PlayMedia media, CancellationToken cancellationToken)
        {
            Played.Add(media);
            if (media.ItemUri == "augora:announcement")
            {
                VolumeDuringAnnouncement = Volume;
                PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs(PlaybackEndReason.Finished));
            }

            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetVolumeAsync(double level, CancellationToken cancellationToken)
        {
            Volume = level;
            VolumeChanges.Add(level);
            return Task.CompletedTask;
        }

        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) => Task.CompletedTask;
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

    private sealed class NoUser : ICurrentUser
    {
        public string? UserId => null;
    }
}
