using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Interfaces;

public interface IPairingRepository
{
    Task<Pairing?> GetForVideoAsync(int videoFileId);
    Task<HashSet<int>> GetPairedVideoIdsAsync();
    Task<Dictionary<int, Pairing>> GetAllPairingsAsync();
    Task UpsertAsync(int videoFileId, int scriptFileId, bool isManual, double confidence = 1.0);
    Task ClearAutoPairingsAsync();
    Task DeleteForVideoAsync(int videoFileId);
    Task UpdateOffsetAsync(int videoFileId, int scriptFileId, int offsetMs);
}
