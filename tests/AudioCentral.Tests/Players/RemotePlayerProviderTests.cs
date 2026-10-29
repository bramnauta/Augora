using AudioCentral.Contracts;
using AudioCentral.Core.Players;
using AudioCentral.Players.Remote;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioCentral.Tests.Players;

public sealed class RemotePlayerProviderTests
{
    private sealed class RecordingChannel : IRemotePlayerChannel
    {
        public List<(string Connection, RemotePlayerCommandDto Command)> Commands { get; } = [];

        public List<string> Released { get; } = [];

        public Task SendCommandAsync(string connectionId, RemotePlayerCommandDto command, CancellationToken cancellationToken)
        {
            Commands.Add((connectionId, command));
            return Task.CompletedTask;
        }

        public Task SendReleasedAsync(string connectionId, CancellationToken cancellationToken)
        {
            Released.Add(connectionId);
            return Task.CompletedTask;
        }
    }

    private static readonly RegisterPlayerRequest Chrome = new("Device-1", "Chrome (Windows)", PlayerKind.Browser);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_connection_for_the_same_device_takes_over_the_player()
    {
        var channel = new RecordingChannel();
        var provider = new RemotePlayerProvider(channel, NullLogger<RemotePlayerProvider>.Instance);

        var first = await provider.RegisterAsync("conn-1", Chrome, Ct);
        var second = await provider.RegisterAsync("conn-2", Chrome, Ct);

        Assert.Equal("remote:device-1", first);
        Assert.Equal(first, second);
        Assert.Single(provider.Players);
        Assert.Equal(["conn-1"], channel.Released);

        // The old connection closing must not make the player unavailable.
        provider.Unregister("conn-1");
        Assert.True(provider.Players.Single().Status.Available);
    }

    [Fact]
    public async Task Commands_go_to_the_connection_and_use_the_relative_stream_path()
    {
        var channel = new RecordingChannel();
        var provider = new RemotePlayerProvider(channel, NullLogger<RemotePlayerProvider>.Instance);
        await provider.RegisterAsync("conn-1", Chrome, Ct);
        var player = provider.Players.Single();

        await player.PlayAsync(
            new PlayMedia(new Uri("http://192.168.1.10:4727/api/stream/abc"), "/api/stream/abc", "audio/mpeg", true, "tunein:radio:s1", "Radio", null, null, null),
            Ct);

        var (connection, command) = Assert.Single(channel.Commands);
        Assert.Equal("conn-1", connection);
        Assert.Equal("/api/stream/abc", command.Media!.StreamUrl);
    }

    [Fact]
    public async Task Reported_end_of_playback_is_raised_once_and_disconnect_makes_it_unavailable()
    {
        var provider = new RemotePlayerProvider(new RecordingChannel(), NullLogger<RemotePlayerProvider>.Instance);
        await provider.RegisterAsync("conn-1", Chrome, Ct);
        var player = provider.Players.Single();
        var ended = new List<PlaybackEndReason>();
        player.PlaybackEnded += (_, e) => ended.Add(e.Reason);

        provider.ReportState("conn-1", new RemotePlayerStateDto(PlaybackState.Idle, 0.5, false, 120, RemotePlaybackEvent.Finished));
        provider.ReportState("other-connection", new RemotePlayerStateDto(PlaybackState.Idle, 0.5, false, 120, RemotePlaybackEvent.Finished));

        Assert.Equal([PlaybackEndReason.Finished], ended);
        Assert.Equal(0.5, player.Status.Volume);

        provider.Unregister("conn-1");
        Assert.False(player.Status.Available);
    }
}
