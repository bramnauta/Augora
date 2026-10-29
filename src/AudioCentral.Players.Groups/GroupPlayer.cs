using AudioCentral.Contracts;
using AudioCentral.Core;
using AudioCentral.Core.Players;
using AudioCentral.Core.Streaming;
using Microsoft.Extensions.Logging;

namespace AudioCentral.Players.Groups;

/// <summary>
/// A player made of other players of any kind. The group's stream is read once and broadcast to every member;
/// commands go to all members. Members start together, but players buffer differently (Cast especially), so the
/// result is loosely synchronised rather than sample-accurate.
/// </summary>
internal sealed class GroupPlayer : IPlayer, IDisposable
{
    public const string IdPrefix = "group:";
    private static readonly TimeSpan EndWait = TimeSpan.FromSeconds(5);

    private readonly Func<string, IPlayer?> _resolve;
    private readonly StreamSessionManager _sessions;
    private readonly IStreamUrlProvider _urls;
    private readonly StreamRelay _relay;
    private readonly GroupMembership _membership;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private readonly HashSet<IPlayer> _subscribed = [];
    private readonly HashSet<string> _ended = new(StringComparer.OrdinalIgnoreCase);
    private PlayerStatus _status = PlayerStatus.Unavailable;
    private PlayMedia? _current;
    private BroadcastStream? _broadcast;
    private List<string> _playing = [];
    private int _generation;
    private bool _anyFinished;

    public GroupPlayer(
        SyncGroupDefinition definition,
        Func<string, IPlayer?> resolve,
        StreamSessionManager sessions,
        IStreamUrlProvider urls,
        StreamRelay relay,
        GroupMembership membership,
        ILogger logger)
    {
        Definition = definition;
        _resolve = resolve;
        _sessions = sessions;
        _urls = urls;
        _relay = relay;
        _membership = membership;
        _logger = logger;
        // Member state changes arrive from many providers; polling the aggregate keeps the group simple.
        _timer = new Timer(_ => UpdateStatus(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public static string PlayerIdFor(Guid groupId) => $"{IdPrefix}{groupId:N}";

    public SyncGroupDefinition Definition { get; set; }

    public string Id => PlayerIdFor(Definition.Id);

    public string Name => Definition.Name;

    public string ProviderId => GroupPlayerProvider.ProviderId;

    public PlayerKind Kind => PlayerKind.SyncGroup;

    public string? Model => null;

    public PlayerFeatures Features => PlayerFeatures.Volume | PlayerFeatures.Mute | PlayerFeatures.Pause;

    /// <summary>Recomputed on every read, so availability checks never see a stale aggregate.</summary>
    public PlayerStatus Status
    {
        get
        {
            UpdateStatus();
            return _status;
        }
    }

    public event EventHandler? StatusChanged;

    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

    private IEnumerable<IPlayer> Members => Definition.MemberIds.Select(_resolve).OfType<IPlayer>();

    public async Task PlayAsync(PlayMedia media, CancellationToken cancellationToken)
    {
        var members = Members.Where(m => m.Status.Available).ToList();
        if (members.Count == 0)
        {
            throw new AudioCentralException(ErrorCodes.PlayerUnavailable, $"No member of '{Name}' is available.", 409);
        }

        var session = _sessions.GetForPlayer(Id)
            ?? throw new InvalidOperationException($"Group {Id} has no stream session.");

        BroadcastStream? previous;
        BroadcastStream? broadcast = null;
        int generation;
        lock (_lock)
        {
            previous = _broadcast;
            if (session.Stream is not ResolvedStream { Direct: true })
            {
                broadcast = new BroadcastStream(session.Stream, _relay, _logger);
            }

            _broadcast = broadcast;
            _current = media;
            _playing = members.Select(m => m.Id).ToList();
            _ended.Clear();
            _anyFinished = false;
            generation = ++_generation;
            foreach (var member in members.Where(_subscribed.Add))
            {
                member.PlaybackEnded += OnMemberEnded;
            }
        }

        if (previous is not null)
        {
            await previous.DisposeAsync();
        }

        _logger.LogInformation("Group {Group} plays {Item} on {Count} members", Name, media.ItemUri, members.Count);
        var results = await Task.WhenAll(members.Select(async member =>
        {
            _membership.Join(member.Id, Id);
            try
            {
                await member.PlayAsync(MediaFor(member, media, broadcast), cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Member {Member} of group {Group} could not play", member.Name, Name);
                _membership.Leave(member.Id);
                return false;
            }
        }));

        if (!results.Any(r => r))
        {
            throw new AudioCentralException(ErrorCodes.PlaybackFailed, $"No member of '{Name}' could play.", 502);
        }

        lock (_lock)
        {
            if (generation == _generation)
            {
                _playing = members.Where((_, i) => results[i]).Select(m => m.Id).ToList();
            }
        }

        UpdateStatus();
    }

    public Task PauseAsync(CancellationToken cancellationToken) => ForPlaying(m => m.PauseAsync(cancellationToken));

    public Task ResumeAsync(CancellationToken cancellationToken) => ForPlaying(m => m.ResumeAsync(cancellationToken));

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        BroadcastStream? broadcast;
        lock (_lock)
        {
            broadcast = _broadcast;
            _broadcast = null;
            _current = null;
            _generation++;
        }

        await ForPlaying(m => m.StopAsync(cancellationToken));
        _membership.Disband(Id);
        if (broadcast is not null)
        {
            await broadcast.DisposeAsync();
        }

        UpdateStatus();
    }

    /// <summary>Scales every member's volume, so their balance stays as it was.</summary>
    public Task SetVolumeAsync(double level, CancellationToken cancellationToken)
    {
        var members = Members.Where(m => m.Status.Available).ToList();
        if (members.Count == 0)
        {
            return Task.CompletedTask;
        }

        var average = members.Average(m => m.Status.Volume);
        return Task.WhenAll(members.Select(m =>
            m.SetVolumeAsync(average < 0.01 ? level : Math.Clamp(m.Status.Volume * level / average, 0, 1), cancellationToken)));
    }

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken) =>
        Task.WhenAll(Members.Where(m => m.Status.Available).Select(m => m.SetMutedAsync(muted, cancellationToken)));

    public void Dispose()
    {
        _timer.Dispose();
        lock (_lock)
        {
            foreach (var member in _subscribed)
            {
                member.PlaybackEnded -= OnMemberEnded;
            }

            _subscribed.Clear();
        }

        _membership.Disband(Id);
        _ = _broadcast?.DisposeAsync().AsTask();
    }

    internal void UpdateStatus()
    {
        var members = Members.ToList();
        var available = members.Where(m => m.Status.Available).ToList();
        PlayMedia? current;
        List<string> playing;
        lock (_lock)
        {
            current = _current;
            playing = _playing;
        }

        var active = available.Where(m => playing.Contains(m.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        var state = current is null || active.Count == 0 ? PlaybackState.Idle
            : active.Any(m => m.Status.State == PlaybackState.Playing) ? PlaybackState.Playing
            : active.Any(m => m.Status.State == PlaybackState.Buffering) ? PlaybackState.Buffering
            : active.All(m => m.Status.State == PlaybackState.Paused) ? PlaybackState.Paused
            : PlaybackState.Idle;

        var leader = active.FirstOrDefault(m => m.Status.State is PlaybackState.Playing or PlaybackState.Paused);
        var nowPlaying = current is not null && state != PlaybackState.Idle
            ? new NowPlayingDto(current.ItemUri, current.Title, current.Subtitle, current.ImagePath ?? current.ImageUrl, current.IsLive,
                leader?.Status.NowPlaying?.Position, current.Duration)
            : null;

        var status = new PlayerStatus(
            available.Count > 0,
            state,
            available.Count > 0 ? Math.Round(available.Average(m => m.Status.Volume), 3) : 0,
            available.Count > 0 && available.All(m => m.Status.Muted),
            nowPlaying,
            null);

        if (status != _status)
        {
            _status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private PlayMedia MediaFor(IPlayer member, PlayMedia media, BroadcastStream? broadcast)
    {
        if (broadcast is null)
        {
            return media;
        }

        var session = _sessions.Create(member.Id, new ProducedStreamSource(media.ContentType, broadcast.CopyToAsync, media.IsLive));
        return media with { StreamUrl = _urls.GetStreamUrl(session), StreamPath = session.RelativePath };
    }

    private Task ForPlaying(Func<IPlayer, Task> command)
    {
        List<string> playing;
        lock (_lock)
        {
            playing = _playing;
        }

        return Task.WhenAll(Members.Where(m => m.Status.Available && playing.Contains(m.Id, StringComparer.OrdinalIgnoreCase)).Select(command));
    }

    /// <summary>When the first member finishes, wait briefly for the others, then let the group's queue advance.</summary>
    private void OnMemberEnded(object? sender, PlaybackEndedEventArgs e)
    {
        if (sender is not IPlayer member || _membership.GetGroup(member.Id) != Id)
        {
            return;
        }

        int generation;
        bool first;
        lock (_lock)
        {
            if (!_playing.Contains(member.Id, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            first = _ended.Count == 0;
            _ended.Add(member.Id);
            _anyFinished |= e.Reason == PlaybackEndReason.Finished;
            generation = _generation;
        }

        if (first)
        {
            _ = Task.Run(async () =>
            {
                var deadline = DateTimeOffset.UtcNow + EndWait;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    lock (_lock)
                    {
                        if (generation != _generation || _ended.Count >= _playing.Count)
                        {
                            break;
                        }
                    }

                    await Task.Delay(200);
                }

                bool finished;
                lock (_lock)
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    _generation++;
                    _current = null;
                    finished = _anyFinished;
                }

                UpdateStatus();
                PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs(finished ? PlaybackEndReason.Finished : PlaybackEndReason.Error));
            });
        }
    }
}
