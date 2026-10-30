using Augora.Contracts;

namespace Augora.Client;

/// <summary>
/// What a platform has to supply to be an Augora player: the audio engine, and the lock screen / notification.
/// The protocol (registering, commands, state reports) is in <see cref="PlayerController"/>.
/// The events are raised on the thread that <see cref="Post"/> runs on, after the matching <see cref="Load"/>.
/// </summary>
public interface IPlayerBackend
{
    PlayerKind Kind { get; }

    /// <summary>The name this device gets in the player list.</summary>
    string DeviceName { get; }

    /// <summary>The position in the loaded stream; <c>null</c> when it is not known (nothing loaded, not prepared yet).</summary>
    double? PositionSeconds { get; }

    /// <summary>Runs <paramref name="action"/> on the thread the audio engine wants to be used from.</summary>
    void Post(Action action);

    /// <summary>The loaded stream is ready: <see cref="Start"/> it.</summary>
    event Action? Ready;

    /// <summary>The stream played to its end.</summary>
    event Action? Completed;

    /// <summary>The stream cannot be played; the argument is a description for the log.</summary>
    event Action<string>? Failed;

    /// <summary>
    /// The user used the notification, lock screen, headset or TV remote (or another app took the audio): a command
    /// (<c>pause</c>, <c>resume</c>, <c>next</c>, <c>previous</c>, <c>stop</c>) that goes through the server, so the queue follows.
    /// </summary>
    event Action<string>? CommandRequested;

    /// <summary>Starts preparing <paramref name="url"/>, replacing what was loaded. Raises <see cref="Ready"/> when done.</summary>
    void Load(Uri url);

    /// <summary>Starts or resumes playback, taking the audio focus.</summary>
    void Start();

    void Pause();

    void Seek(double seconds);

    /// <param name="level">0 (silent) to 1.</param>
    void SetVolume(double level);

    /// <summary>Unloads the stream and gives up the audio focus.</summary>
    void Release();

    /// <summary>Shows what plays on the lock screen / in the notification.</summary>
    void ShowNowPlaying(RemoteMediaDto? media, PlaybackState state);
}
