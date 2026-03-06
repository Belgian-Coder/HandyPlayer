using HandyPlaylistPlayer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Core.Services;

/// <summary>
/// Watches library root folders for file changes and triggers incremental re-indexing.
/// Falls back gracefully on network shares where FileSystemWatcher is unreliable.
/// </summary>
public class LibraryWatcher : IDisposable
{
    private readonly ILibraryRootRepository _rootRepo;
    private readonly ILibraryIndexer _indexer;
    private readonly ILogger<LibraryWatcher> _logger;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _lock = new();
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    public event EventHandler? LibraryChanged;

    public LibraryWatcher(
        ILibraryRootRepository rootRepo,
        ILibraryIndexer indexer,
        ILogger<LibraryWatcher> logger)
    {
        _rootRepo = rootRepo;
        _indexer = indexer;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        StopWatchers();

        var roots = await _rootRepo.GetAllAsync();
        foreach (var root in roots.Where(r => r.IsEnabled))
        {
            try
            {
                if (!Directory.Exists(root.Path))
                {
                    _logger.LogWarning("Skipping watcher for offline root: {Path}", root.Path);
                    continue;
                }

                var watcher = new FileSystemWatcher(root.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                watcher.Created += OnFileChanged;
                watcher.Deleted += OnFileChanged;
                watcher.Renamed += OnFileRenamed;
                watcher.Error += OnWatcherError;

                lock (_lock) _watchers.Add(watcher);
                _logger.LogInformation("Watching library root: {Path}", root.Path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create watcher for {Path} (network share?)", root.Path);
            }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
        if (!LibraryIndexer.IsVideoFile(ext) && !LibraryIndexer.IsScriptFile(ext))
            return;

        DebouncedRescan();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        var extOld = Path.GetExtension(e.OldFullPath).ToLowerInvariant();
        var extNew = Path.GetExtension(e.FullPath).ToLowerInvariant();
        if (!LibraryIndexer.IsVideoFile(extOld) && !LibraryIndexer.IsScriptFile(extOld) &&
            !LibraryIndexer.IsVideoFile(extNew) && !LibraryIndexer.IsScriptFile(extNew))
            return;

        DebouncedRescan();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "FileSystemWatcher error — may need manual rescan");
    }

    private void DebouncedRescan()
    {
        CancellationToken ct;
        lock (_lock)
        {
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            ct = _debounceCts.Token;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000, ct); // 2s debounce
                _logger.LogInformation("File changes detected, running incremental scan");
                await _indexer.ScanAllAsync(ct: ct);
                LibraryChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) { /* superseded by newer change */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background rescan failed");
            }
        }, ct);
    }

    private void StopWatchers()
    {
        lock (_lock)
        {
            foreach (var w in _watchers)
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            _watchers.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        StopWatchers();
    }
}
