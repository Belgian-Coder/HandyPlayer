using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Runtime;

public interface IPlaybackCoordinator
{
    PlaybackState State { get; }
    long CurrentPositionMs { get; }
    long DurationMs { get; }
    DeviceConnectionState DeviceState { get; }
    TransformSettings TransformSettings { get; set; }
    bool IsStreamingMode { get; }
    FunscriptDocument? CurrentScript { get; }

    event EventHandler<PlaybackState> PlaybackStateChanged;
    event EventHandler<long> PositionChanged;
    event EventHandler<DeviceConnectionState> DeviceStateChanged;
    event EventHandler? MediaEnded;

    Task LoadAsync(string videoPath, string? scriptPath, CancellationToken ct = default);

    /// <summary>
    /// Pre-parse and pre-upload the next script in the background.
    /// Safe to call during active playback — does not touch current playback state.
    /// </summary>
    Task PrepareNextAsync(string? scriptPath, CancellationToken ct = default);
    Task LoadPatternAsync(FunscriptDocument pattern, CancellationToken ct = default);
    Task PlayAsync();
    Task PauseAsync();
    Task StopAsync();
    Task EmergencyStopAsync();
    Task SeekAsync(long positionMs);
    Task ConnectDeviceAsync(CancellationToken ct = default);
    Task DisconnectDeviceAsync();
    void SetDeviceBackend(IDeviceBackend backend);
    void SetMediaPlayer(IMediaPlayer mediaPlayer);
}
