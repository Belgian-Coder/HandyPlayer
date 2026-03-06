using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Settings.SaveSetting;

public record SaveSettingCommand(string Key, string Value) : ICommand;
