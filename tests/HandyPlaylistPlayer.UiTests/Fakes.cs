using System.Collections.Concurrent;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Media.Mpv;

namespace HandyPlaylistPlayer.UiTests;

/// <summary>Stands in for mpv: records what the app asks it to do and lets tests move the clock.</summary>
public sealed class FakeVideoPlayer : IVideoPlayer
{
    private long _positionMs;
    private PlaybackState _state = PlaybackState.Stopped;

    public string? LoadedPath { get; private set; }
    public ConcurrentQueue<string> Calls { get; } = new();

    public long PositionMs => Interlocked.Read(ref _positionMs);
    public long DurationMs { get; set; } = 60_000;
    public double Volume { get; set; } = 100;
    public PlaybackState State => _state;
    public string DetectedHwDecoder => "fake";

    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;
    public event EventHandler<PositionChangedEventArgs>? PositionChanged;
    public event EventHandler? MediaEnded;

    public Task LoadAsync(string filePath)
    {
        Calls.Enqueue($"Load {filePath}");
        LoadedPath = filePath;
        Interlocked.Exchange(ref _positionMs, 0);
        SetState(PlaybackState.Paused);
        return Task.CompletedTask;
    }

    public Task PlayAsync() { Calls.Enqueue("Play"); SetState(PlaybackState.Playing); return Task.CompletedTask; }
    public Task PauseAsync() { Calls.Enqueue("Pause"); SetState(PlaybackState.Paused); return Task.CompletedTask; }
    public Task StopAsync() { Calls.Enqueue("Stop"); SetState(PlaybackState.Stopped); return Task.CompletedTask; }

    public Task SeekAsync(long positionMs)
    {
        Calls.Enqueue($"Seek {positionMs}");
        AdvanceTo(positionMs);
        return Task.CompletedTask;
    }

    /// <summary>Moves the media clock, as mpv's time-pos observer would.</summary>
    public void AdvanceTo(long positionMs)
    {
        Interlocked.Exchange(ref _positionMs, positionMs);
        PositionChanged?.Invoke(this, new PositionChangedEventArgs(positionMs));
    }

    public void End() => MediaEnded?.Invoke(this, EventArgs.Empty);

    private void SetState(PlaybackState state)
    {
        _state = state;
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(state));
    }

    public void SetLiveProperty(string name, string value) => Calls.Enqueue($"Set {name}={value}");
    public void UpdateScale(string scale) { }
    public void UpdateShadersLive(GlslShaderPreset newPreset) { }
    public void UpdateVolumeMax(int newMax) { }
    public void SetPitchCorrection(bool enabled) { }
    public Task ReinitVoAsync(string optionName, string optionValue) => Task.CompletedTask;
    public List<MpvTrack> GetTracks() => [];
    public void SelectAudioTrack(int id) { }
    public void SelectSubtitleTrack(int id) { }
    public void DisableSubtitles() { }
    public void Dispose() { }
}

/// <summary>A connected streaming device (Intiface-style) that records every command.</summary>
public sealed class FakeDevice : IDeviceBackend
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<(int Position, int DurationMs)> Positions { get; } = new();

    public string Name => "Fake device";
    public bool IsStreamingMode => true;
    public DeviceConnectionState ConnectionState => DeviceConnectionState.Connected;
#pragma warning disable CS0067 // never raised: the fake is always connected
    public event EventHandler<DeviceConnectionState>? ConnectionStateChanged;
#pragma warning restore CS0067

    public Task<bool> ConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task DisconnectAsync() => Task.CompletedTask;
    public Task SetupScriptAsync(FunscriptDocument script, CancellationToken ct = default) { Calls.Enqueue("SetupScript"); return Task.CompletedTask; }
    public Task StartPlaybackAsync(long startTimeMs, CancellationToken ct = default) { Calls.Enqueue("Start"); return Task.CompletedTask; }
    public Task StopPlaybackAsync(CancellationToken ct = default) { Calls.Enqueue("Stop"); return Task.CompletedTask; }
    public Task SeekPlaybackAsync(long positionMs, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResyncAsync(long mediaTimeMs, CancellationToken ct = default) => Task.CompletedTask;
    public Task EmergencyStopAsync() { Calls.Enqueue("EmergencyStop"); return Task.CompletedTask; }

    public Task SendPositionAsync(int position, int durationMs, CancellationToken ct = default)
    {
        Positions.Enqueue((position, durationMs));
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

/// <summary>No thumbnails (the real generator spins up its own mpv instance).</summary>
public sealed class NullThumbnailService : IThumbnailService
{
    public Task<string?> GetOrGenerateThumbnailAsync(int mediaFileId, string videoPath, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    public void DeleteThumbnail(int mediaFileId) { }
    public void DeleteOrphanedThumbnails(IReadOnlyCollection<int> activeIds) { }
    public void Dispose() { }
}
