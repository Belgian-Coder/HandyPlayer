using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Devices.Intiface;

public class IntifaceDeviceClient(ILogger<IntifaceDeviceClient> logger) : IDeviceBackend
{
    public string Name => "Intiface Central";
    public bool IsStreamingMode => true;
    public DeviceConnectionState ConnectionState { get; private set; } = DeviceConnectionState.Disconnected;
    public event EventHandler<DeviceConnectionState>? ConnectionStateChanged;

    public string WebSocketUrl { get; set; } = "ws://localhost:12345";

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        // Full Buttplug.NET implementation in Phase 4
        logger.LogInformation("Intiface connect requested to {Url}", WebSocketUrl);
        SetConnectionState(DeviceConnectionState.Connecting);

        try
        {
            // Placeholder - will be implemented with Buttplug client
            await Task.Delay(100, ct);
            SetConnectionState(DeviceConnectionState.Connected);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to Intiface");
            SetConnectionState(DeviceConnectionState.Error);
            return false;
        }
    }

    public Task DisconnectAsync()
    {
        SetConnectionState(DeviceConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task SetupScriptAsync(FunscriptDocument script, CancellationToken ct = default)
    {
        // Intiface mode doesn't pre-load scripts - it streams positions in real-time
        return Task.CompletedTask;
    }

    public Task StartPlaybackAsync(long startTimeMs, CancellationToken ct = default)
    {
        // Streaming tick engine handles this
        return Task.CompletedTask;
    }

    public Task StopPlaybackAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task SeekPlaybackAsync(long positionMs, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public async Task SendPositionAsync(int position, int durationMs, CancellationToken ct = default)
    {
        // Will send LinearCmd via Buttplug in Phase 4
        logger.LogTrace("SendPosition: pos={Position}, duration={Duration}ms", position, durationMs);
        await Task.CompletedTask;
    }

    public Task ResyncAsync(long mediaTimeMs, CancellationToken ct = default) => Task.CompletedTask;

    public async Task EmergencyStopAsync()
    {
        logger.LogWarning("Intiface emergency stop");
        // Will send StopAllDevices via Buttplug in Phase 4
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        // Will dispose Buttplug client in Phase 4
    }

    private void SetConnectionState(DeviceConnectionState state)
    {
        ConnectionState = state;
        ConnectionStateChanged?.Invoke(this, state);
    }
}
