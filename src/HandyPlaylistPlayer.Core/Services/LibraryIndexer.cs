using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Extensions.Logging;
using System.IO.Abstractions;

namespace HandyPlaylistPlayer.Core.Services;

public class LibraryIndexer(
    IFileSystem fileSystem,
    ILibraryRootRepository rootRepo,
    IMediaFileRepository mediaRepo,
    ILogger<LibraryIndexer> logger) : ILibraryIndexer
{
    private static readonly HashSet<string> VideoExtensions =
        [".mp4", ".mkv", ".webm", ".avi", ".wmv", ".mov", ".m4v", ".flv"];

    private static readonly HashSet<string> ScriptExtensions = [".funscript"];

    public async Task ScanRootAsync(int rootId, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var roots = await rootRepo.GetAllAsync();
        var root = roots.FirstOrDefault(r => r.Id == rootId);
        if (root == null)
        {
            logger.LogWarning("Library root {Id} not found", rootId);
            return;
        }

        await ScanSingleRootAsync(root, progress, ct);
    }

    public async Task ScanAllAsync(IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var roots = await rootRepo.GetAllAsync();
        var enabledRoots = roots.Where(r => r.IsEnabled).ToList();

        if (enabledRoots.Count <= 1)
        {
            foreach (var root in enabledRoots)
            {
                ct.ThrowIfCancellationRequested();
                await ScanSingleRootAsync(root, progress, ct);
            }
            return;
        }

        // Parallel scan: up to 4 roots concurrently. File I/O runs in parallel but
        // DB writes are serialized via SQLite's built-in locking.
        logger.LogInformation("Parallel scanning {Count} library roots", enabledRoots.Count);
        await Parallel.ForEachAsync(enabledRoots,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (root, token) => await ScanSingleRootAsync(root, progress, token));
    }

    private async Task ScanSingleRootAsync(LibraryRoot root, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        logger.LogInformation("Scanning library root: {Path}", root.Path);
        var discoveredPaths = new HashSet<string>();
        int processedCount = 0;
        int errorCount = 0;

        try
        {
            if (!fileSystem.Directory.Exists(root.Path))
            {
                await rootRepo.UpdateStatusAsync(root.Id, "offline");
                logger.LogWarning("Library root offline: {Path}", root.Path);
                return;
            }

            List<(string Path, string Extension)> mediaFiles;
            try
            {
                mediaFiles = fileSystem.Directory.EnumerateFiles(root.Path, "*.*", SearchOption.AllDirectories)
                    .Select(f => (Path: f, Extension: fileSystem.Path.GetExtension(f).ToLowerInvariant()))
                    .Where(f => IsVideoFile(f.Extension) || IsScriptFile(f.Extension))
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to enumerate files in {Path}", root.Path);
                await rootRepo.UpdateStatusAsync(root.Id, "error");
                return;
            }

            int totalFiles = mediaFiles.Count;

            var errorFiles = new List<string>();
            const int batchSize = 50;
            var batch = new List<MediaItem>(batchSize);

            foreach (var (filePath, ext) in mediaFiles)
            {
                ct.ThrowIfCancellationRequested();

                // Always keep the path so files that error aren't deleted from DB
                discoveredPaths.Add(filePath);

                try
                {
                    var fileInfo = fileSystem.FileInfo.New(filePath);
                    batch.Add(new MediaItem
                    {
                        LibraryRootId = root.Id,
                        FullPath = filePath,
                        Filename = fileSystem.Path.GetFileNameWithoutExtension(filePath),
                        Extension = ext,
                        FileSize = fileInfo.Length,
                        ModifiedAt = fileInfo.LastWriteTimeUtc,
                        IsScript = IsScriptFile(ext)
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errorCount++;
                    errorFiles.Add(fileSystem.Path.GetFileName(filePath));
                    logger.LogWarning(ex, "Error reading file info: {Path}", filePath);
                }

                // Flush batch to DB periodically and report progress after commit
                if (batch.Count >= batchSize)
                {
                    await mediaRepo.UpsertBatchAsync(batch);
                    processedCount += batch.Count;
                    batch.Clear();
                    progress?.Report(new ScanProgress(totalFiles, processedCount + errorCount, errorCount, filePath, errorFiles));
                }
            }

            // Flush remaining
            if (batch.Count > 0)
            {
                await mediaRepo.UpsertBatchAsync(batch);
                processedCount += batch.Count;
                progress?.Report(new ScanProgress(totalFiles, processedCount + errorCount, errorCount, null, errorFiles));
            }

            await mediaRepo.DeleteByRootAsync(root.Id, discoveredPaths);

            await rootRepo.UpdateStatusAsync(root.Id, "online", DateTime.UtcNow);
            logger.LogInformation("Scan complete for {Path}: {Count} files, {Errors} errors",
                root.Path, processedCount, errorCount);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Scan cancelled for {Path}", root.Path);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scan failed for {Path}", root.Path);
            await rootRepo.UpdateStatusAsync(root.Id, "error");
        }
    }

    public static bool IsVideoFile(string extension) =>
        VideoExtensions.Contains(extension.ToLowerInvariant());

    public static bool IsScriptFile(string extension) =>
        ScriptExtensions.Contains(extension.ToLowerInvariant());
}
