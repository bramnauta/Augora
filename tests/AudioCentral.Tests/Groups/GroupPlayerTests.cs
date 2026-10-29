using AudioCentral.Contracts;
using AudioCentral.Core.Players;
using AudioCentral.Core.Streaming;
using AudioCentral.Players.Groups;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioCentral.Tests.Groups;

public sealed class GroupPlayerTests : IDisposable
{
    private readonly StreamSessionManager _sessions = new(TimeProvider.System);
    private readonly GroupMembership _membership = new();
    private readonly FakePlayer _kitchen = new("cast:kitchen");
    private readonly FakePlayer _phone = new("remote:phone");
    private readonly GroupPlayer _group;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public GroupPlayerTests()
    {
        var players = new[] { _kitchen, _phone }.ToDictionary(p => p.Id, p => (IPlayer)p);
        _group = new GroupPlayer(
            new SyncGroupDefinition(Guid.NewGuid(), "Downstairs", [_kitchen.Id, _phone.Id]),
            id => players.GetValueOrDefault(id),
            _sessions,
            new FakeUrls(),
            BroadcastStreamTests.Relay(),
            _membership,
            NullLogger.Instance);
    }

    public void Dispose() => _group.Dispose();

    [Fact]
    public async Task Members_get_their_own_session_on_one_shared_source()
    {
        var source = new ProducedStreamSource("audio/mpeg", (output, ct) => output.WriteAsync(new byte[] { 1, 2, 3 }, ct).AsTask());
        _sessions.Create(_group.Id, source);

        await _group.PlayAsync(Media(), Ct);

        Assert.NotNull(_kitchen.Played);
        Assert.NotNull(_phone.Played);
        Assert.NotEqual(_kitchen.Played!.StreamPath, _phone.Played!.StreamPath);
        Assert.Equal(_group.Id, _membership.GetGroup(_kitchen.Id));

        foreach (var member in new[] { _kitchen, _phone })
        {
            var session = Assert.IsType<ProducedStreamSource>(_sessions.GetForPlayer(member.Id)!.Stream);
            using var output = new MemoryStream();
            await session.WriteAsync(output, Ct);
            Assert.Equal([1, 2, 3], output.ToArray());
        }
    }

    [Fact]
    public async Task The_group_ends_once_after_its_members_ended()
    {
        _sessions.Create(_group.Id, new ProducedStreamSource("audio/mpeg", (_, _) => Task.CompletedTask));
        await _group.PlayAsync(Media(), Ct);
        var ended = 0;
        _group.PlaybackEnded += (_, _) => Interlocked.Increment(ref ended);

        _kitchen.End();
        await Task.Delay(400, Ct);
        Assert.Equal(0, ended);

        _phone.End();
        await Task.Delay(600, Ct);
        Assert.Equal(1, ended);
    }

    [Fact]
    public async Task Volume_is_scaled_so_the_balance_stays()
    {
        _kitchen.Volume = 0.8;
        _phone.Volume = 0.4;

        await _group.SetVolumeAsync(0.3, Ct);

        Assert.Equal(0.4, _kitchen.Volume, 3);
        Assert.Equal(0.2, _phone.Volume, 3);
    }

    private static PlayMedia Media() =>
        new(new Uri("http://server/api/stream/group"), "/api/stream/group", "audio/mpeg", false, "test:track:1", "Song", null, null, null);

    private sealed class FakeUrls : IStreamUrlProvider
    {
        public Uri GetStreamUrl(StreamSession session) => GetAbsoluteUrl(session.RelativePath);

        public Uri GetAbsoluteUrl(string relativePath) => new(new Uri("http://server/"), relativePath.TrimStart('/'));
    }

    private sealed class FakePlayer(string id) : IPlayer
    {
        public string Id { get; } = id;

        public string Name => Id;

        public string ProviderId => "fake";

        public PlayerKind Kind => PlayerKind.Speaker;

        public string? Model => null;

        public PlayerFeatures Features => PlayerFeatures.Volume;

        public double Volume { get; set; } = 0.5;

        public PlayMedia? Played { get; private set; }

        public PlayerStatus Status => new(true, Played is null ? PlaybackState.Idle : PlaybackState.Playing, Volume, false, null, null);

        public event EventHandler? StatusChanged;

        public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

        public void End() => PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs(PlaybackEndReason.Finished));

        public Task PlayAsync(PlayMedia media, CancellationToken cancellationToken)
        {
            Played = media;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetVolumeAsync(double level, CancellationToken cancellationToken)
        {
            Volume = level;
            return Task.CompletedTask;
        }

        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
