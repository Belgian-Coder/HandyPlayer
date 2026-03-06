using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Core.Runtime;

public class EmergencyStopService(
    IPlaybackCoordinator coordinator,
    ILogger<EmergencyStopService> logger) : IEmergencyStopService
{
    public event EventHandler? EmergencyStopped;

    public async Task TriggerAsync()
    {
        logger.LogWarning("EMERGENCY STOP triggered");
        try
        {
            await coordinator.EmergencyStopAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during emergency stop");
        }
        EmergencyStopped?.Invoke(this, EventArgs.Empty);
    }
}
