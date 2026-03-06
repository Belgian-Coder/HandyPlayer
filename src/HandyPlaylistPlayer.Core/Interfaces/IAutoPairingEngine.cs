using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Interfaces;

public interface IAutoPairingEngine
{
    Task RunPairingAsync(CancellationToken ct = default);
    Task<MediaItem?> FindScriptForVideoAsync(int videoFileId, CancellationToken ct = default);
}
