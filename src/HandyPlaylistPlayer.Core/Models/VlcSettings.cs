namespace HandyPlaylistPlayer.Core.Models;

/// <summary>
/// VLC engine settings loaded at startup. Changes require app restart.
/// </summary>
public record VlcSettings(
    bool HardwareDecode = true,
    int FileCacheMs = 300,
    bool FastDecode = true,
    bool SkipLoopFilter = true);
