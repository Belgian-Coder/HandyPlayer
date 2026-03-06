using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Settings.SaveSetting;

public class SaveSettingHandler(IAppSettingsRepository repo) : ICommandHandler<SaveSettingCommand, Unit>
{
    public async Task<Unit> HandleAsync(SaveSettingCommand command, CancellationToken ct = default)
    {
        await repo.SetAsync(command.Key, command.Value);
        return Unit.Value;
    }
}
