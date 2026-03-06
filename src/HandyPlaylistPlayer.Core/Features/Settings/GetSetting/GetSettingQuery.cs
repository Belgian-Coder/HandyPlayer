using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Settings.GetSetting;

public record GetSettingQuery(string Key) : IQuery<string?>;
