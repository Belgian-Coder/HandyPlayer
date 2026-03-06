namespace HandyPlaylistPlayer.Core.Runtime;

public interface IEmergencyStopService
{
    event EventHandler EmergencyStopped;
    Task TriggerAsync();
}
