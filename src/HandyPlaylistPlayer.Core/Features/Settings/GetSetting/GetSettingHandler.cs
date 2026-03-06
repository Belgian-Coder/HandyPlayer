using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Settings.GetSetting;

public class GetSettingHandler(IAppSettingsRepository repo) : IQueryHandler<GetSettingQuery, string?>
{
    public async Task<string?> HandleAsync(GetSettingQuery query, CancellationToken ct = default)
        => await repo.GetAsync(query.Key);
}
