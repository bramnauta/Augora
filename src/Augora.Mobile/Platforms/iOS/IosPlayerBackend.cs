using AVFoundation;
using Augora.Client;
using Augora.Contracts;
using CoreFoundation;
using CoreMedia;
using Foundation;
using MediaPlayer;
using UIKit;
using PlaybackState = Augora.Contracts.PlaybackState;

namespace Augora.Mobile;

/// <summary>
/// Plays the streams with <see cref="AVPlayer"/> and shows them on the lock screen and in Control Center, so playback
/// continues with the screen locked and the headset and lock screen buttons can control it.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Lives as long as the app; the player is released with Release.")]
internal sealed class IosPlayerBackend : IPlayerBackend
{
    private AVPlayer? _player;
    private AVPlayerItem? _item;
    private IDisposable? _statusObserver;
    private NSObject? _endObserver;
    private bool _readyRaised;
    private PlaybackState _state = PlaybackState.Idle;

    public IosPlayerBackend()
    {
        var commands = MPRemoteCommandCenter.Shared;
        Handle(commands.PlayCommand, "resume");
        Handle(commands.PauseCommand, "pause");
        Handle(commands.NextTrackCommand, "next");
        Handle(commands.PreviousTrackCommand, "previous");
        Handle(commands.StopCommand, "stop");
        Handle(commands.TogglePlayPauseCommand, null);

        // A call or Siri took the audio: pause and let the server know.
        AVAudioSession.Notifications.ObserveInterruption((_, e) =>
        {
            if (e.InterruptionType == AVAudioSessionInterruptionType.Began && _state == PlaybackState.Playing)
            {
                CommandRequested?.Invoke("pause");
            }
        });
    }

    public event Action? Ready;

    public event Action? Completed;

    public event Action<string>? Failed;

    public event Action<string>? CommandRequested;

    public PlayerKind Kind => PlayerKind.Phone;

    public string DeviceName => UIDevice.CurrentDevice.Name;

    public double? PositionSeconds =>
        _player is { } player && double.IsFinite(player.CurrentTime.Seconds) ? player.CurrentTime.Seconds : null;

    public void Post(Action action) => DispatchQueue.MainQueue.DispatchAsync(action);

    public void Load(Uri url)
    {
        Unload();

        var session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.Playback, (AVAudioSessionCategoryOptions)0, out _);
        session.SetActive(true, out _);

        var item = new AVPlayerItem(new NSUrl(url.AbsoluteUri));
        _readyRaised = false;
        _statusObserver = item.AddObserver("status", NSKeyValueObservingOptions.Initial | NSKeyValueObservingOptions.New, _ => OnStatusChanged(item));
        _endObserver = NSNotificationCenter.DefaultCenter.AddObserver(AVPlayerItem.DidPlayToEndTimeNotification, _ => Post(() => Completed?.Invoke()), item);
        _item = item;
        _player = new AVPlayer(item);
    }

    public void Start() => _player?.Play();

    public void Pause() => _player?.Pause();

    public void Seek(double seconds) => _player?.Seek(CMTime.FromSeconds(seconds, 1000));

    public void SetVolume(double level)
    {
        if (_player is not null)
        {
            _player.Volume = (float)level;
        }
    }

    public void Release()
    {
        Unload();
        AVAudioSession.SharedInstance().SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
    }

    public void ShowNowPlaying(RemoteMediaDto? media, PlaybackState state)
    {
        _state = state;
        if (media is null)
        {
            MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo();
            return;
        }

        var info = new MPNowPlayingInfo
        {
            Title = media.Title,
            Artist = media.Subtitle,
            ElapsedPlaybackTime = PositionSeconds ?? 0,
            PlaybackRate = state == PlaybackState.Playing ? 1 : 0,
        };
        if (media.DurationSeconds is { } duration)
        {
            info.PlaybackDuration = duration;
        }

        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = info;
    }

    private void OnStatusChanged(AVPlayerItem item)
    {
        if (item != _item)
        {
            return;
        }

        if (item.Status == AVPlayerItemStatus.ReadyToPlay && !_readyRaised)
        {
            _readyRaised = true;
            Post(() => Ready?.Invoke());
        }
        else if (item.Status == AVPlayerItemStatus.Failed)
        {
            var error = item.Error?.LocalizedDescription ?? "AVPlayerItem failed";
            Post(() => Failed?.Invoke(error));
        }
    }

    private void Unload()
    {
        _statusObserver?.Dispose();
        _statusObserver = null;
        if (_endObserver is not null)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(_endObserver);
            _endObserver = null;
        }

        _player?.Pause();
        _player?.Dispose();
        _player = null;
        _item = null;
    }

    /// <summary>Lock screen and headset buttons go through the server, like the Android notification.</summary>
    /// <param name="command">The command to send; <c>null</c> for play/pause, which depends on what plays now.</param>
    private void Handle(MPRemoteCommand remote, string? command)
    {
        remote.Enabled = true;
        remote.AddTarget(_ =>
        {
            CommandRequested?.Invoke(command ?? (_state == PlaybackState.Playing ? "pause" : "resume"));
            return MPRemoteCommandHandlerStatus.Success;
        });
    }
}
