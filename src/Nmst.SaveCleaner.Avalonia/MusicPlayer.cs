using System.Text.Json;
using Avalonia.Platform;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Nmst.SaveCleaner.Core;
using Nmst.SaveCleaner.ViewModels;

namespace Nmst.SaveCleaner.Avalonia;

/// <summary>
/// Plays the bundled synthwave tracks (Assets/Music, see tools/music) one at a time in shuffled order. Tracks never
/// overlap: each plays out its own outro to silence, a short gap follows, and the next fades in. Forward/back fade
/// the current track out first. Windows only for now (NAudio's WaveOut); elsewhere <see cref="IsAvailable"/> is false.
/// </summary>
public sealed class MusicPlayer : IMusicPlayer
{
    private const string Folder = "avares://NMSTSaveCleaner/Assets/Music/";
    private const int FadeInMs = 2000, SwitchFadeOutMs = 1500, GapMs = 1000;
    private const double RestartThreshold = 3;   // back within this many seconds goes to the previous track
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    private sealed record Track(string Id, string Title, string File, double Duration, double OutroStart);

    /// <summary>One playing track. Reads and seeks share a lock because the audio thread pulls while the UI seeks.</summary>
    private sealed class Voice : ISampleProvider
    {
        private readonly object _gate = new();
        private readonly VorbisWaveReader _reader;
        private readonly ISampleProvider _src;
        private long _samples;

        public Voice(Track track, byte[] data)
        {
            Track = track;
            _reader = new VorbisWaveReader(new MemoryStream(data, writable: false));
            _src = _reader.WaveFormat.SampleRate == Format.SampleRate ? _reader : new WdlResamplingSampleProvider(_reader, Format.SampleRate);
            Fader = new FadeInOutSampleProvider(this, initiallySilent: true);
        }

        public Track Track { get; }
        public FadeInOutSampleProvider Fader { get; }
        public WaveFormat WaveFormat => _src.WaveFormat;
        public double Seconds => Interlocked.Read(ref _samples) / (double)(Format.SampleRate * Format.Channels);
        public bool Finished { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                int n = _src.Read(buffer, offset, count);
                Interlocked.Add(ref _samples, n);
                if (n < count) Finished = true;   // the mixer drops an input after its first short read
                return n;
            }
        }

        public void Seek(double seconds)
        {
            lock (_gate)
            {
                seconds = Math.Clamp(seconds, 0, _reader.TotalTime.TotalSeconds - 0.1);
                _reader.CurrentTime = TimeSpan.FromSeconds(seconds);
                Interlocked.Exchange(ref _samples, (long)(seconds * Format.SampleRate) * Format.Channels);
            }
        }

        public void Dispose() { lock (_gate) _reader.Dispose(); }
    }

    private readonly object _lock = new();
    private readonly List<Track> _tracks = [];
    private readonly Dictionary<string, byte[]> _data = [];
    private readonly List<int> _bag = [];
    private readonly List<int> _history = [];
    private readonly Random _rng = new();
    private WaveOutEvent? _out;
    private MixingSampleProvider? _mixer;
    private VolumeSampleProvider? _master;
    private Voice? _current;
    private Voice? _leaving;          // the track fading out after forward/back
    private int _currentIndex = -1;
    private int _pendingIndex = -1;   // what to start once _startAt passes
    private DateTime _startAt = DateTime.MaxValue;
    private Timer? _timer;
    private double _volume = AppSettings.DefaultMusicVolume;
    private string _nowPlaying = "";

    public MusicPlayer()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var s = AssetLoader.Open(new Uri(Folder + "tracks.json"));
            var list = JsonSerializer.Deserialize<List<Track>>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            foreach (var t in list)
            {
                using var a = AssetLoader.Open(new Uri(Folder + t.File));
                using var ms = new MemoryStream();
                a.CopyTo(ms);
                _data[t.Id] = ms.ToArray();
                _tracks.Add(t);
            }
        }
        catch { _tracks.Clear(); }
    }

    private bool _broken;   // the audio device failed; stay quiet for the rest of the session

    public bool IsAvailable => OperatingSystem.IsWindows() && _tracks.Count > 0 && !_broken;
    public string NowPlaying => _nowPlaying;
    public event Action? NowPlayingChanged;

    public double Position { get { lock (_lock) return _current?.Seconds ?? 0; } }
    public double Duration { get { lock (_lock) return _current?.Track.Duration ?? 0; } }
    public bool IsPaused { get { lock (_lock) return _out?.PlaybackState == PlaybackState.Paused; } }

    // perceived loudness is roughly logarithmic; squaring the slider value makes the low end usable
    public double Volume
    {
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            lock (_lock) if (_master is not null) _master.Volume = (float)(_volume * _volume);
        }
    }

    public void Play()
    {
        if (!IsAvailable) return;
        lock (_lock)
        {
            if (_out is not null) return;
            try
            {
                _mixer = new MixingSampleProvider(Format) { ReadFully = true };
                _master = new VolumeSampleProvider(_mixer) { Volume = (float)(_volume * _volume) };
                _out = new WaveOutEvent { DesiredLatency = 200 };
                _out.PlaybackStopped += OnPlaybackStopped;
                _out.Init(_master);
                _out.Play();
                StartTrack(NextFromBag());
                _timer = new Timer(_ => Tick(), null, 100, 100);
            }
            catch
            {
                // no usable audio device (e.g. Remote Desktop without sound): give up on music, not on the app
                _broken = true;
                _timer?.Dispose(); _timer = null;
                try { _out?.Dispose(); } catch (Exception) { }
                _out = null; _current = null;
                SetNowPlaying("");
                throw;
            }
        }
    }

    public void Stop()
    {
        WaveOutEvent? output;
        Voice? cur, leaving;
        lock (_lock)
        {
            if (_out is null) return;
            output = _out; cur = _current; leaving = _leaving;
            if (output.PlaybackState == PlaybackState.Paused) output.Play();
            cur?.Fader.BeginFadeOut(1200);
            _timer?.Dispose(); _timer = null;
            _out = null; _current = null; _leaving = null; _pendingIndex = -1;
            SetNowPlaying("");
        }
        // let the fade finish, then release the device
        Task.Delay(1300).ContinueWith(_ =>
        {
            output.Stop();
            output.Dispose();
            cur?.Dispose();
            leaving?.Dispose();
        });
    }

    public void Next()
    {
        lock (_lock) SwitchTo(NextFromBag());
    }

    public void Previous()
    {
        lock (_lock)
        {
            if (_out is null) return;
            int target = _currentIndex;
            if (_current is not null && _current.Seconds < RestartThreshold && _history.Count > 1)
            {
                _history.RemoveAt(_history.Count - 1);      // drop the current one
                target = _history[^1];
                _history.RemoveAt(_history.Count - 1);      // StartTrack adds it back
            }
            else if (_history.Count > 0) _history.RemoveAt(_history.Count - 1);
            SwitchTo(target);
        }
    }

    public void TogglePause()
    {
        lock (_lock)
        {
            if (_out is null) return;
            if (_out.PlaybackState == PlaybackState.Paused) _out.Play(); else _out.Pause();
        }
    }

    public void Seek(double seconds)
    {
        lock (_lock) _current?.Seek(seconds);
    }

    /// <summary>Fades the current track out, then (after the fade) fades <paramref name="index"/> in.</summary>
    private void SwitchTo(int index)
    {
        if (_out is null) return;
        if (_out.PlaybackState == PlaybackState.Paused) _out.Play();
        if (_current is not null)
        {
            _leaving?.Dispose();
            if (_leaving is not null) _mixer!.RemoveMixerInput(_leaving.Fader);
            _leaving = _current;
            _leaving.Fader.BeginFadeOut(SwitchFadeOutMs);
            _current = null;
        }
        _pendingIndex = index;
        _startAt = DateTime.Now.AddMilliseconds(SwitchFadeOutMs + 100);
        SetNowPlaying(_tracks[index].Title);
    }

    /// <summary>The device went away mid-song (headset unplugged…): stop quietly.</summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        lock (_lock)
        {
            _broken = true;
            _timer?.Dispose(); _timer = null;
            _out = null; _current = null; _leaving = null;
        }
        SetNowPlaying("");
    }

    // runs on a thread-pool timer: an exception escaping here would end the whole process
    private void Tick()
    {
        try { TickCore(); }
        catch (Exception) { lock (_lock) { _pendingIndex = -1; } }
    }

    private void TickCore()
    {
        lock (_lock)
        {
            if (_out is null || _out.PlaybackState == PlaybackState.Paused) return;

            // a track that played to its end (its outro already faded to silence): short gap, then the next one
            if (_current is { Finished: true } && _pendingIndex < 0)
            {
                _mixer!.RemoveMixerInput(_current.Fader);
                _current.Dispose();
                _current = null;
                _pendingIndex = NextFromBag();
                _startAt = DateTime.Now.AddMilliseconds(GapMs);
            }

            if (_pendingIndex >= 0 && DateTime.Now >= _startAt)
            {
                if (_leaving is not null) { _mixer!.RemoveMixerInput(_leaving.Fader); _leaving.Dispose(); _leaving = null; }
                int i = _pendingIndex;
                _pendingIndex = -1;
                StartTrack(i);
            }
        }
    }

    private void StartTrack(int index)
    {
        var v = new Voice(_tracks[index], _data[_tracks[index].Id]);
        v.Fader.BeginFadeIn(FadeInMs);
        _current = v;
        _currentIndex = index;
        _history.Add(index);
        if (_history.Count > 50) _history.RemoveAt(0);
        _mixer!.AddMixerInput(v.Fader);
        SetNowPlaying(v.Track.Title);
    }

    /// <summary>Shuffle bag: every track once per round, never the same track twice in a row.</summary>
    private int NextFromBag()
    {
        if (_bag.Count == 0)
        {
            _bag.AddRange(Enumerable.Range(0, _tracks.Count).OrderBy(_ => _rng.Next()));
            if (_bag.Count > 1 && _bag[0] == _currentIndex) (_bag[0], _bag[1]) = (_bag[1], _bag[0]);
        }
        int i = _bag[0];
        _bag.RemoveAt(0);
        return i;
    }

    private void SetNowPlaying(string title)
    {
        _nowPlaying = title;
        NowPlayingChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose(); _timer = null;
            _out?.Stop(); _out?.Dispose(); _out = null;
            _current?.Dispose(); _leaving?.Dispose();
            _current = null; _leaving = null;
        }
    }
}
