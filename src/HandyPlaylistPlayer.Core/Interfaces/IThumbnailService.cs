namespace HandyPlaylistPlayer.Core.Interfaces;

public interface IThumbnailService : IDisposable
{
    Task<string?> GetOrGenerateThumbnailAsync(int mediaFileId, string videoPath, CancellationToken ct = default);

    /// <summary>Deletes the cached thumbnail for a single media file, if it exists.</summary>
    void DeleteThumbnail(int mediaFileId);

    /// <summary>
    /// Deletes any thumbnail files whose IDs are not in <paramref name="activeIds"/>.
    /// Call after a library reload to clean up thumbnails for removed items.
    /// </summary>
    void DeleteOrphanedThumbnails(IReadOnlyCollection<int> activeIds);
}
