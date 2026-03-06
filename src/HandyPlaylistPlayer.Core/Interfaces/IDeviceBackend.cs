using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Interfaces;

public interface IDeviceBackend : IDisposable
{
    string Name { get; }
    bool IsStreamingMode { get; }
    DeviceConnectionState ConnectionState { get; }
    event EventHandler<DeviceConnectionState> ConnectionStateChanged;

    Task<bool> ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();

    Task SetupScriptAsync(FunscriptDocument script, CancellationToken ct = default);

    /// <summary>
    /// Pre-upload script to hosting service without configuring the device.
    /// Safe to call during active playback. Default implementation is no-op.
    /// </summary>
    Task PreUploadScriptAsync(FunscriptDocument script, CancellationToken ct = default) => Task.CompletedTask;
    Task StartPlaybackAsync(long startTimeMs, CancellationToken ct = default);
    Task StopPlaybackAsync(CancellationToken ct = default);
    Task SeekPlaybackAsync(long positionMs, CancellationToken ct = default);
    Task SendPositionAsync(int position, int durationMs, CancellationToken ct = default);
    Task ResyncAsync(long mediaTimeMs, CancellationToken ct = default);
    Task EmergencyStopAsync();
}
