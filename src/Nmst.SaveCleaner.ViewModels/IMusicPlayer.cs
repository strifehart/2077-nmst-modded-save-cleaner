namespace Nmst.SaveCleaner.ViewModels;

/// <summary>Background music. The UI framework supplies the implementation (audio output differs per platform).</summary>
public interface IMusicPlayer : IDisposable
{
    /// <summary>False when no audio output is available (e.g. an unsupported platform); the music controls hide.</summary>
    bool IsAvailable { get; }

    /// <summary>Title of the track playing, or "" when stopped.</summary>
    string NowPlaying { get; }

    event Action? NowPlayingChanged;

    /// <summary>0..1</summary>
    double Volume { set; }

    /// <summary>Seconds into the current track (0 when stopped).</summary>
    double Position { get; }

    /// <summary>Length of the current track in seconds (0 when stopped).</summary>
    double Duration { get; }

    bool IsPaused { get; }

    /// <summary>Starts the shuffled playlist with a fade-in (no-op if already playing).</summary>
    void Play();

    /// <summary>Fades out and releases the audio device.</summary>
    void Stop();

    /// <summary>Fades the current track out, then fades the next one in.</summary>
    void Next();

    /// <summary>Restarts the current track, or goes to the previous one within its first 3 seconds.</summary>
    void Previous();

    void TogglePause();

    void Seek(double seconds);
}
