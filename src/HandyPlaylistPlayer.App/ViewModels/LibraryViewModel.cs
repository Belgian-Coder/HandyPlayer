using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandyPlaylistPlayer.Core.Dispatching;
using IDispatcher = HandyPlaylistPlayer.Core.Dispatching.IDispatcher;
using HandyPlaylistPlayer.Core.Features.Library.AddLibraryRoot;
using HandyPlaylistPlayer.Core.Features.Library.DeleteMediaFiles;
using HandyPlaylistPlayer.Core.Features.Library.FindScriptForVideo;
using HandyPlaylistPlayer.Core.Features.Library.GetLibraryItems;
using HandyPlaylistPlayer.Core.Features.Library.GetLibraryRoots;
using HandyPlaylistPlayer.Core.Features.Library.RemoveLibraryRoot;
using HandyPlaylistPlayer.Core.Features.Library.ScanLibrary;
using HandyPlaylistPlayer.Core.Features.Library.ToggleWatched;
using HandyPlaylistPlayer.Core.Features.Library.FindDuplicates;
using HandyPlaylistPlayer.Core.Features.Library.GetMissingFiles;
using HandyPlaylistPlayer.Core.Features.Library.RelocateMediaFile;
using HandyPlaylistPlayer.Core.Features.Library.UpdatePairingOffset;
using HandyPlaylistPlayer.Core.Features.Playlists.AddPlaylistItem;
using HandyPlaylistPlayer.Core.Features.Playlists.GetAllPlaylists;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Runtime;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Services;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.App.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private readonly IDispatcher _dispatcher;
    private readonly IPairingRepository _pairingRepo;
    private readonly IPlaylistRepository _playlistRepo;
    private readonly IMediaFileRepository _mediaFileRepo;
    private readonly IFunscriptParser _scriptParser;
    private readonly IQueueService _queue;
    private readonly QueuePanelViewModel _queuePanel;
    private readonly IThumbnailService _thumbnailService;
    private readonly ILogger<LibraryViewModel> _logger;
    private CancellationTokenSource? _thumbnailCts;     // grid-view Bitmap loading
    private CancellationTokenSource? _bgThumbnailCts;   // background .jpg file generation

    [ObservableProperty] private string _statusText = "Library";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanStatus = string.Empty;
    [ObservableProperty] private LibraryItemViewModel? _selectedItem;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _selectAllChecked;
    [ObservableProperty] private FunscriptDocument? _previewScript;
    [ObservableProperty] private bool _hasPreviewScript;
    [ObservableProperty] private string _lastSyncText = "";
    [ObservableProperty] private bool _isGridView;
    [ObservableProperty] private bool _isDuplicateScan;

    private static readonly double[] _cardWidthPresets = [120, 150, 180, 240, 300];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GrowCardsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShrinkCardsCommand))]
    private int _cardSizeStep = 3;
    [ObservableProperty] private string _duplicateStatus = string.Empty;
    [ObservableProperty] private bool _showDuplicates;

    public ObservableCollection<DuplicateGroupViewModel> DuplicateGroups { get; } = [];

    [ObservableProperty] private bool   _showMissingFiles;
    [ObservableProperty] private int    _missingCount;

    public ObservableCollection<MissingFileViewModel> MissingFiles { get; } = [];

    // Sorting state
    private string _sortColumn = "Name";
    private bool _sortAscending = true;

    public Func<string, Task<bool>>? ConfirmAction { get; set; }
    public Func<Task<string?>>? PickScriptAction { get; set; }
    public Func<Task<string?>>? PickVideoFileAction { get; set; }
    public Func<string, string, Task<string?>>? PromptAction { get; set; }
    public Func<Task>? OnScanCompleted { get; set; }

    internal IDispatcher Dispatcher => _dispatcher;

    public ObservableCollection<LibraryItemViewModel> Items { get; } = [];
    public ObservableCollection<LibraryRoot> Roots { get; } = [];
    public ObservableCollection<Playlist> Playlists { get; } = [];

    // Sort indicator properties for column headers
    public string SortIndicator_Name => GetSortIndicator("Name");
    public string SortIndicator_Watched => GetSortIndicator("Watched");
    public string SortIndicator_Script => GetSortIndicator("Script");
    public string SortIndicator_Duration => GetSortIndicator("Duration");
    public string SortIndicator_Type => GetSortIndicator("Type");
    public string SortIndicator_Folder => GetSortIndicator("Folder");
    public string SortIndicator_DateAdded => GetSortIndicator("Date Added");

    private string GetSortIndicator(string column)
    {
        if (_sortColumn != column) return column;
        return _sortAscending ? $"{column} \u25B2" : $"{column} \u25BC";
    }

    public LibraryViewModel(
        IDispatcher dispatcher,
        IPairingRepository pairingRepo,
        IPlaylistRepository playlistRepo,
        IMediaFileRepository mediaFileRepo,
        IFunscriptParser scriptParser,
        IThumbnailService thumbnailService,
        IQueueService queue,
        QueuePanelViewModel queuePanel,
        ILogger<LibraryViewModel> logger)
    {
        _dispatcher = dispatcher;
        _pairingRepo = pairingRepo;
        _playlistRepo = playlistRepo;
        _mediaFileRepo = mediaFileRepo;
        _scriptParser = scriptParser;
        _thumbnailService = thumbnailService;
        _queue = queue;
        _queuePanel = queuePanel;
        _logger = logger;

        _ = LoadAsync();
    }

    public async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private async Task Load() => await LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            // Run all DB queries on thread pool to keep UI responsive
            var videos = await Task.Run(() => _dispatcher.QueryAsync(new GetLibraryItemsQuery()));
            var roots = await Task.Run(() => _dispatcher.QueryAsync(new GetLibraryRootsQuery()));
            var pairings = await Task.Run(() => _pairingRepo.GetAllPairingsAsync());
            var playlists = await Task.Run(() => _playlistRepo.GetAllAsync());

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Items.Clear();
                foreach (var video in videos)
                {
                    pairings.TryGetValue(video.Id, out var pairing);
                    var vm = new LibraryItemViewModel(video)
                    {
                        IsPaired = pairing != null,
                        PairingConfidence = pairing?.Confidence ?? 0,
                        IsManualPairing = pairing?.IsManual ?? false,
                        IsWatched = video.WatchedAt != null
                    };
                    Items.Add(vm);
                }

                // Snapshot the full list so search filtering works after reload
                _allItems = [.. Items];

                // New items are always unselected — keep the checkbox in sync
                SelectAllChecked = false;

                Roots.Clear();
                foreach (var root in roots)
                    Roots.Add(root);

                Playlists.Clear();
                foreach (var p in playlists)
                    Playlists.Add(p);

                StatusText = $"Library - {Items.Count} items";
                UpdateLastSyncText(roots);
                ApplySorting();
                UpdateEmpty();
            });

            // Re-apply search filter if active
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    OnSearchTextChanged(SearchText));
            }

            // Clean up thumbnail files for items no longer in the library
            var activeIds = Items
                .Where(i => !i.MediaItem.IsScript)
                .Select(i => i.MediaItem.Id)
                .ToHashSet();
            _ = Task.Run(() => _thumbnailService.DeleteOrphanedThumbnails(activeIds));

            // Always generate .jpg files in the background so they are ready
            // before the user opens grid view (startup, scan, add folder)
            _bgThumbnailCts?.Cancel();
            _bgThumbnailCts?.Dispose();
            _bgThumbnailCts = new CancellationTokenSource();
            _ = EnsureThumbnailFilesAsync(_bgThumbnailCts.Token);

            // If grid view is already active, also (re)load Bitmaps into items
            if (IsGridView)
            {
                _thumbnailCts?.Cancel();
                _thumbnailCts?.Dispose();
                _thumbnailCts = new CancellationTokenSource();
                _ = GenerateThumbnailsAsync(_thumbnailCts.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load library");
            StatusText = "Error loading library";
        }
    }

    [RelayCommand]
    private async Task ScanAll()
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanStatus = "Scanning...";

        try
        {
            IReadOnlyList<string>? lastErrorFiles = null;
            var progress = new Progress<ScanProgress>(p =>
            {
                lastErrorFiles = p.ErrorFiles;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    ScanStatus = p.Errors > 0
                        ? $"Scanned {p.ProcessedFiles}/{p.TotalFiles} ({p.Errors} errors)"
                        : $"Scanned {p.ProcessedFiles}/{p.TotalFiles}");
            });

            // Run scan on thread pool to keep UI responsive
            await Task.Run(() => _dispatcher.SendAsync(new ScanLibraryCommand(progress)));
            await LoadAsync();
            if (OnScanCompleted != null) await OnScanCompleted();

            if (lastErrorFiles is { Count: > 0 })
            {
                var fileList = string.Join(", ", lastErrorFiles.Take(5));
                if (lastErrorFiles.Count > 5)
                    fileList += $" (+{lastErrorFiles.Count - 5} more)";
                ScanStatus = $"Done — {lastErrorFiles.Count} files skipped (locked/inaccessible): {fileList}";
            }
            else
            {
                ScanStatus = "Scan complete";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed");
            ScanStatus = "Scan failed";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task ScanRoot(int rootId)
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanStatus = "Scanning...";

        try
        {
            IReadOnlyList<string>? lastErrorFiles = null;
            var progress = new Progress<ScanProgress>(p =>
            {
                lastErrorFiles = p.ErrorFiles;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    ScanStatus = p.Errors > 0
                        ? $"Scanned {p.ProcessedFiles}/{p.TotalFiles} ({p.Errors} errors)"
                        : $"Scanned {p.ProcessedFiles}/{p.TotalFiles}");
            });

            await Task.Run(() => _dispatcher.SendAsync(new ScanLibraryRootCommand(rootId, progress)));
            await LoadAsync();
            if (OnScanCompleted != null) await OnScanCompleted();

            if (lastErrorFiles is { Count: > 0 })
            {
                var fileList = string.Join(", ", lastErrorFiles.Take(5));
                if (lastErrorFiles.Count > 5)
                    fileList += $" (+{lastErrorFiles.Count - 5} more)";
                ScanStatus = $"Done — {lastErrorFiles.Count} files skipped (locked/inaccessible): {fileList}";
            }
            else
            {
                ScanStatus = "Scan complete";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan of root {RootId} failed", rootId);
            ScanStatus = "Scan failed";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task AddRoot(Window window)
    {
        try
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Library Folder",
                AllowMultiple = false
            });
            if (folders.Count > 0)
            {
                var rootId = await _dispatcher.SendAsync(new AddLibraryRootCommand(folders[0].Path.LocalPath));
                await ScanRoot(rootId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add library root");
        }
    }

    [RelayCommand]
    private async Task RemoveRoot(LibraryRoot root)
    {
        await _dispatcher.SendAsync(new RemoveLibraryRootCommand(root.Id));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task PlayItem(LibraryItemViewModel item)
    {
        // Look up paired script for this video
        MediaItem? script = null;
        if (item.IsPaired)
        {
            try { script = await _dispatcher.QueryAsync(new FindScriptForVideoQuery(item.MediaItem.Id)); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to find script for video"); }
        }

        var queueItem = new QueueItem { Video = item.MediaItem, Script = script };
        _queue.Clear();
        _queue.Enqueue([queueItem]);
        _queue.Next();
    }

    [RelayCommand]
    private async Task DeleteItem(LibraryItemViewModel? item)
    {
        if (item == null) return;

        var message = item.IsPaired
            ? $"Delete '{item.DisplayName}' and its paired script from disk?\n\nThis cannot be undone."
            : $"Delete '{item.DisplayName}' from disk?\n\nThis cannot be undone.";

        if (ConfirmAction != null && !await ConfirmAction(message))
            return;

        try
        {
            var itemsToDelete = new List<MediaItem> { item.MediaItem };

            if (item.IsPaired)
            {
                var script = await _dispatcher.QueryAsync(new FindScriptForVideoQuery(item.MediaItem.Id));
                if (script != null)
                    itemsToDelete.Add(script);
            }

            await _dispatcher.SendAsync(new DeleteMediaFilesCommand(itemsToDelete));

            // Only remove from UI if the file was actually deleted
            if (!File.Exists(item.MediaItem.FullPath))
            {
                Items.Remove(item);
                if (_allItems.Count > 0)
                    _allItems.Remove(item);
                StatusText = $"Library - {Items.Count} items";
                UpdateEmpty();
                _thumbnailService.DeleteThumbnail(item.MediaItem.Id);
            }
            else
            {
                StatusText = "Could not delete — file may be in use";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete item");
        }
    }

    [RelayCommand]
    private async Task MarkWatched(LibraryItemViewModel? item)
    {
        if (item == null) return;
        try
        {
            await _dispatcher.SendAsync(new ToggleWatchedCommand(item.MediaItem.Id, true));
            item.IsWatched = true;
            _queuePanel.MarkWatched(item.MediaItem.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark as watched");
        }
    }

    [RelayCommand]
    private async Task AddToQueue(LibraryItemViewModel? item)
    {
        if (item == null) return;
        MediaItem? script = null;
        if (item.IsPaired)
        {
            try { script = await _dispatcher.QueryAsync(new FindScriptForVideoQuery(item.MediaItem.Id)); }
            catch (Exception ex) { _logger.LogDebug(ex, "Pairing lookup failed"); }
        }
        _queue.Enqueue([new QueueItem { Video = item.MediaItem, Script = script }]);
        StatusText = $"Added '{item.DisplayName}' to queue";
    }

    [RelayCommand]
    private async Task PlayNextItem(LibraryItemViewModel? item)
    {
        if (item == null) return;
        MediaItem? script = null;
        if (item.IsPaired)
        {
            try { script = await _dispatcher.QueryAsync(new FindScriptForVideoQuery(item.MediaItem.Id)); }
            catch (Exception ex) { _logger.LogDebug(ex, "Pairing lookup failed"); }
        }
        _queue.EnqueueNext(new QueueItem { Video = item.MediaItem, Script = script });
        StatusText = $"'{item.DisplayName}' will play next";
    }

    [RelayCommand]
    private async Task CreatePlaylistAndAdd()
    {
        var itemsToAdd = Items.Where(i => i.IsSelected).ToList();
        if (itemsToAdd.Count == 0 && SelectedItem != null)
            itemsToAdd = [SelectedItem];
        if (itemsToAdd.Count == 0) return;

        try
        {
            var defaultName = $"Playlist ({DateTime.Now:MMM d HH:mm})";
            var playlistName = PromptAction != null
                ? await PromptAction("Enter playlist name:", defaultName)
                : defaultName;
            if (string.IsNullOrWhiteSpace(playlistName)) return;
            var playlistId = await Task.Run(() => _playlistRepo.CreateAsync(playlistName));
            var ids = itemsToAdd.Select(i => i.MediaItem.Id).ToList();
            await Task.Run(() => _playlistRepo.AddItemsBatchAsync(playlistId, ids));

            // Refresh playlists so the new one appears in future menus
            var playlists = await Task.Run(() => _playlistRepo.GetAllAsync());
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Playlists.Clear();
                foreach (var p in playlists) Playlists.Add(p);
            });

            StatusText = $"Created '{playlistName}' with {ids.Count} items";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create playlist");
            StatusText = "Failed to create playlist";
        }
    }

    [RelayCommand]
    private async Task AddToPlaylist(Playlist? playlist)
    {
        if (playlist == null) return;

        // Use selected items if any, otherwise fall back to the single selected item
        var itemsToAdd = Items.Where(i => i.IsSelected).ToList();
        if (itemsToAdd.Count == 0 && SelectedItem != null)
            itemsToAdd = [SelectedItem];
        if (itemsToAdd.Count == 0) return;

        try
        {
            var ids = itemsToAdd.Select(i => i.MediaItem.Id).ToList();
            var added = await Task.Run(() => _playlistRepo.AddItemsBatchAsync(playlist.Id, ids));
            var skipped = itemsToAdd.Count - added;
            StatusText = skipped > 0
                ? $"Added {added} to '{playlist.Name}' ({skipped} already in playlist)"
                : $"Added {added} items to '{playlist.Name}'";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add to playlist");
            StatusText = "Failed to add to playlist";
        }
    }

    [RelayCommand]
    private async Task MarkUnwatched(LibraryItemViewModel? item)
    {
        if (item == null) return;
        try
        {
            await _dispatcher.SendAsync(new ToggleWatchedCommand(item.MediaItem.Id, false));
            item.IsWatched = false;
            _queuePanel.MarkUnwatched(item.MediaItem.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark as unwatched");
        }
    }

    [RelayCommand]
    private async Task PairWithScript(LibraryItemViewModel? item)
    {
        if (item == null || PickScriptAction == null) return;
        try
        {
            var scriptPath = await PickScriptAction();
            if (string.IsNullOrEmpty(scriptPath)) return;

            // Find the script in media_files by path
            var scriptFile = await Task.Run(() => _mediaFileRepo.GetByPathAsync(scriptPath));
            if (scriptFile == null)
            {
                StatusText = "Script not in library — scan first or add the folder";
                return;
            }

            await _pairingRepo.UpsertAsync(item.MediaItem.Id, scriptFile.Id, isManual: true, confidence: 1.0);
            item.IsPaired = true;
            item.PairingConfidence = 1.0;
            item.IsManualPairing = true;
            StatusText = $"Manually paired '{item.DisplayName}' with '{Path.GetFileName(scriptPath)}'";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pair with script");
            StatusText = "Failed to pair with script";
        }
    }

    [RelayCommand]
    private async Task FindDuplicates()
    {
        if (IsDuplicateScan) return;
        IsDuplicateScan = true;
        DuplicateStatus = "Scanning for duplicates...";
        ShowDuplicates = true;

        try
        {
            var groups = await Task.Run(() => _dispatcher.QueryAsync(new FindDuplicatesQuery()));

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                DuplicateGroups.Clear();
                foreach (var g in groups)
                {
                    DuplicateGroups.Add(new DuplicateGroupViewModel
                    {
                        FileSize = g.FileSize,
                        Items = g.Items.Select(i => new LibraryItemViewModel(i)
                        {
                            IsWatched = i.WatchedAt != null
                        }).ToList()
                    });
                }

                DuplicateStatus = groups.Count > 0
                    ? $"Found {groups.Count} duplicate groups ({groups.Sum(g => g.Items.Count)} files)"
                    : "No duplicates found";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Duplicate scan failed");
            DuplicateStatus = "Duplicate scan failed";
        }
        finally
        {
            IsDuplicateScan = false;
        }
    }

    [RelayCommand]
    private void CloseDuplicates()
    {
        ShowDuplicates = false;
        DuplicateGroups.Clear();
        DuplicateStatus = string.Empty;
    }

    [RelayCommand]
    private async Task ShowMissingFilesPanel()
    {
        var missing = await _dispatcher.QueryAsync(new GetMissingFilesQuery());
        MissingFiles.Clear();
        foreach (var item in missing)
            MissingFiles.Add(new MissingFileViewModel(item, this));
        MissingCount = MissingFiles.Count;
        ShowMissingFiles = MissingFiles.Count > 0;
    }

    [RelayCommand]
    private void CloseMissingFiles() { ShowMissingFiles = false; }

    [RelayCommand]
    private async Task SetScriptOffset(LibraryItemViewModel? item)
    {
        if (item == null || !item.IsPaired) return;
        var pairing = await _dispatcher.QueryAsync(new FindScriptForVideoQuery(item.MediaItem.Id));
        if (pairing == null) return;
        var current = "0";
        var input = PromptAction != null ? await PromptAction.Invoke(
            "Set Script Offset", $"Offset in ms for this video's script (current: {current}):") : null;
        if (input == null) return;
        if (!int.TryParse(input, out var offsetMs)) return;
        await _dispatcher.SendAsync(
            new UpdatePairingOffsetCommand(item.MediaItem.Id, pairing.Id, offsetMs));
    }

    partial void OnSelectAllCheckedChanged(bool value)
    {
        if (value)
        {
            foreach (var item in Items)
                item.IsSelected = true;
        }
        else
        {
            foreach (var item in _allItems)
                item.IsSelected = false;
        }
        UpdateSelectionState();
    }

    [RelayCommand]
    private void DeselectAll()
    {
        // Deselect all items including those filtered out by search
        foreach (var item in _allItems)
            item.IsSelected = false;
        foreach (var item in Items)
            item.IsSelected = false;
        SelectAllChecked = false;
        UpdateSelectionState();
    }

    [RelayCommand]
    private async Task AddSelectedToQueue()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        var queueItems = new List<QueueItem>();
        foreach (var item in selected)
        {
            MediaItem? script = null;
            if (item.IsPaired)
            {
                try { script = await _dispatcher.QueryAsync(new FindScriptForVideoQuery(item.MediaItem.Id)); }
                catch (Exception ex) { _logger.LogDebug(ex, "Script lookup failed"); }
            }
            queueItems.Add(new QueueItem { Video = item.MediaItem, Script = script });
        }
        _queue.Enqueue(queueItems);
        StatusText = $"Added {selected.Count} items to queue";
    }

    [RelayCommand]
    private async Task MarkSelectedWatched()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        foreach (var item in selected)
        {
            try
            {
                await _dispatcher.SendAsync(new ToggleWatchedCommand(item.MediaItem.Id, true));
                item.IsWatched = true;
                _queuePanel.MarkWatched(item.MediaItem.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark {Name} as watched", item.DisplayName);
            }
        }
        StatusText = $"Marked {selected.Count} items as watched";
    }

    [RelayCommand]
    private async Task MarkSelectedUnwatched()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        foreach (var item in selected)
        {
            try
            {
                await _dispatcher.SendAsync(new ToggleWatchedCommand(item.MediaItem.Id, false));
                item.IsWatched = false;
                _queuePanel.MarkUnwatched(item.MediaItem.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark {Name} as unwatched", item.DisplayName);
            }
        }
        StatusText = $"Marked {selected.Count} items as unwatched";
    }

    [RelayCommand]
    private async Task DeleteSelected()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        if (ConfirmAction != null && !await ConfirmAction($"Delete {selected.Count} items from disk?\n\nThis cannot be undone."))
            return;

        try
        {
            var itemsToDelete = new List<MediaItem>();
            foreach (var item in selected)
            {
                itemsToDelete.Add(item.MediaItem);
                if (item.IsPaired)
                {
                    try
                    {
                        var script = await _dispatcher.QueryAsync(new FindScriptForVideoQuery(item.MediaItem.Id));
                        if (script != null) itemsToDelete.Add(script);
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Script lookup failed"); }
                }
            }

            await _dispatcher.SendAsync(new DeleteMediaFilesCommand(itemsToDelete));

            var deleted = selected.Where(i => !File.Exists(i.MediaItem.FullPath)).ToList();
            foreach (var item in deleted)
            {
                Items.Remove(item);
                _allItems.Remove(item);
                _thumbnailService.DeleteThumbnail(item.MediaItem.Id);
            }

            StatusText = $"Deleted {deleted.Count} items";
            UpdateEmpty();
            UpdateSelectionState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete selected items");
        }
    }

    [RelayCommand]
    private async Task AddSelectedToPlaylist(Playlist? playlist)
    {
        if (playlist == null) return;
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        try
        {
            var ids = selected.Select(i => i.MediaItem.Id).ToList();
            var added = await Task.Run(() => _playlistRepo.AddItemsBatchAsync(playlist.Id, ids));
            var skipped = selected.Count - added;
            StatusText = skipped > 0
                ? $"Added {added} to '{playlist.Name}' ({skipped} already in playlist)"
                : $"Added {added} items to '{playlist.Name}'";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add selected to playlist");
        }
    }

    public void OnItemSelectionChanged()
    {
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        var count = Items.Count(i => i.IsSelected);
        SelectedCount = count;
        HasSelection = count > 0;
    }

    [RelayCommand]
    private void SortBy(string column)
    {
        if (_sortColumn == column)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        NotifySortIndicators();
        ApplySorting();
    }

    private void NotifySortIndicators()
    {
        OnPropertyChanged(nameof(SortIndicator_Name));
        OnPropertyChanged(nameof(SortIndicator_Watched));
        OnPropertyChanged(nameof(SortIndicator_Script));
        OnPropertyChanged(nameof(SortIndicator_Duration));
        OnPropertyChanged(nameof(SortIndicator_Type));
        OnPropertyChanged(nameof(SortIndicator_Folder));
        OnPropertyChanged(nameof(SortIndicator_DateAdded));
    }

    private void ApplySorting()
    {
        var sorted = _sortColumn switch
        {
            "Name" => _sortAscending
                ? Items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                : Items.OrderByDescending(i => i.DisplayName, StringComparer.OrdinalIgnoreCase),
            "Watched" => _sortAscending
                ? Items.OrderBy(i => i.IsWatched)
                : Items.OrderByDescending(i => i.IsWatched),
            "Script" => _sortAscending
                ? Items.OrderBy(i => i.IsPaired)
                : Items.OrderByDescending(i => i.IsPaired),
            "Duration" => _sortAscending
                ? Items.OrderBy(i => i.MediaItem.DurationMs ?? 0)
                : Items.OrderByDescending(i => i.MediaItem.DurationMs ?? 0),
            "Type" => _sortAscending
                ? Items.OrderBy(i => i.Extension, StringComparer.OrdinalIgnoreCase)
                : Items.OrderByDescending(i => i.Extension, StringComparer.OrdinalIgnoreCase),
            "Folder" => _sortAscending
                ? Items.OrderBy(i => i.Folder, StringComparer.OrdinalIgnoreCase)
                : Items.OrderByDescending(i => i.Folder, StringComparer.OrdinalIgnoreCase),
            "Date Added" => _sortAscending
                ? Items.OrderBy(i => i.MediaItem.CreatedAt)
                : Items.OrderByDescending(i => i.MediaItem.CreatedAt),
            _ => Items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
        };

        var sortedList = sorted.ToList();
        Items.Clear();
        foreach (var item in sortedList)
            Items.Add(item);
    }

    private void UpdateEmpty()
    {
        IsEmpty = Items.Count == 0;
    }

    private void UpdateLastSyncText(List<LibraryRoot> roots)
    {
        var mostRecent = roots
            .Where(r => r.LastScan.HasValue)
            .OrderByDescending(r => r.LastScan!.Value)
            .FirstOrDefault();

        if (mostRecent?.LastScan is { } lastScan)
        {
            var localTime = lastScan.Kind == DateTimeKind.Utc
                ? lastScan.ToLocalTime()
                : lastScan;
            var elapsed = DateTime.Now - localTime;
            LastSyncText = elapsed.TotalMinutes < 1 ? "Synced just now"
                : elapsed.TotalMinutes < 60 ? $"Synced {(int)elapsed.TotalMinutes}m ago"
                : elapsed.TotalHours < 24 ? $"Synced {(int)elapsed.TotalHours}h ago"
                : $"Synced {localTime:g}";
        }
        else
        {
            LastSyncText = "Never synced";
        }
    }

    private List<LibraryItemViewModel> _allItems = [];

    partial void OnSelectedItemChanged(LibraryItemViewModel? value)
    {
        _ = LoadPreviewScriptAsync(value);
    }

    private async Task LoadPreviewScriptAsync(LibraryItemViewModel? item)
    {
        if (item == null || !item.IsPaired)
        {
            PreviewScript = null;
            HasPreviewScript = false;
            return;
        }

        try
        {
            var script = await _dispatcher.QueryAsync(new FindScriptForVideoQuery(item.MediaItem.Id));
            if (script != null && File.Exists(script.FullPath))
            {
                var doc = await Task.Run(() => _scriptParser.ParseFileAsync(script.FullPath));
                PreviewScript = doc;
                HasPreviewScript = doc.Actions.Count > 1;
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load preview script");
        }

        PreviewScript = null;
        HasPreviewScript = false;
    }

    partial void OnIsGridViewChanged(bool value)
    {
        // Cancel only the Bitmap-loading task; background file generation keeps running
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;

        if (value)
        {
            _thumbnailCts = new CancellationTokenSource();
            _ = GenerateThumbnailsAsync(_thumbnailCts.Token);
        }
    }

    /// <summary>
    /// Generates .jpg thumbnail files for all non-script items without loading
    /// them as Bitmaps. Runs in the background regardless of grid view state so
    /// thumbnails are ready on disk before the user opens grid view.
    /// </summary>
    private async Task EnsureThumbnailFilesAsync(CancellationToken ct)
    {
        var items = Items.ToList().Where(i => !i.MediaItem.IsScript).ToList();
        try
        {
            await Parallel.ForEachAsync(items,
                new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
                async (item, token) =>
                {
                    try
                    {
                        await _thumbnailService.GetOrGenerateThumbnailAsync(
                            item.MediaItem.Id, item.MediaItem.FullPath, token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Background thumbnail generation failed for {Id}", item.MediaItem.Id);
                    }
                });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "Background thumbnail generation loop failed"); }
    }

    private async Task GenerateThumbnailsAsync(CancellationToken ct)
    {
        var items = Items.ToList()
            .Where(i => i.Thumbnail == null && !i.MediaItem.IsScript)
            .ToList();
        try
        {
            await Parallel.ForEachAsync(items,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (item, token) =>
                {
                    try
                    {
                        var path = await _thumbnailService.GetOrGenerateThumbnailAsync(
                            item.MediaItem.Id, item.MediaItem.FullPath, token);

                        if (path != null && !token.IsCancellationRequested)
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                try { item.Thumbnail = new Avalonia.Media.Imaging.Bitmap(path); }
                                catch { /* file may have been deleted */ }
                            });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Thumbnail generation failed for {Id}", item.MediaItem.Id);
                    }
                });
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand(CanExecute = nameof(CanGrowCards))]
    private void GrowCards() => CardSizeStep++;
    private bool CanGrowCards() => CardSizeStep < 5;

    [RelayCommand(CanExecute = nameof(CanShrinkCards))]
    private void ShrinkCards() => CardSizeStep--;
    private bool CanShrinkCards() => CardSizeStep > 1;

    partial void OnCardSizeStepChanged(int value)
    {
        var w = _cardWidthPresets[value - 1];
        var h = w * 2.0 / 3.0;
        foreach (var item in Items)
        {
            item.CardWidth  = w;
            item.CardHeight = h;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_allItems.Count == 0)
            _allItems = [.. Items];

        Items.Clear();
        var filtered = string.IsNullOrWhiteSpace(value)
            ? _allItems
            : _allItems.Where(i =>
                i.DisplayName.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                i.Folder.Contains(value, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var item in filtered)
            Items.Add(item);

        StatusText = $"Library - {Items.Count} items";
        ApplySorting();
        UpdateEmpty();
    }
}

public partial class LibraryItemViewModel : ObservableObject
{
    public MediaItem MediaItem { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairingStatus))]
    private bool _isPaired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairingStatus))]
    private double _pairingConfidence;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairingStatus))]
    private bool _isManualPairing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchedStatus))]
    private bool _isWatched;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _thumbnail;
    [ObservableProperty] private double _cardWidth  = 180;
    [ObservableProperty] private double _cardHeight = 120;

    public string DisplayName => MediaItem.Filename;
    public string Folder => Path.GetDirectoryName(MediaItem.FullPath) ?? "";
    public string Extension => MediaItem.Extension;
    public string DateAddedDisplay => MediaItem.CreatedAt.ToString("yyyy-MM-dd");

    public string PairingStatus
    {
        get
        {
            if (!IsPaired) return "No script";
            if (IsManualPairing) return "Manual";
            return PairingConfidence switch
            {
                >= 1.0 => "100%",
                >= 0.9 => "90%",
                >= 0.7 => "70%",
                _ => $"{PairingConfidence:P0}"
            };
        }
    }

    public string WatchedStatus
    {
        get
        {
            if (!IsWatched || MediaItem.WatchedAt == null) return "";
            var local = MediaItem.WatchedAt.Value.Kind == DateTimeKind.Utc
                ? MediaItem.WatchedAt.Value.ToLocalTime()
                : MediaItem.WatchedAt.Value;
            var elapsed = DateTime.Now - local;
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
            if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d ago";
            return local.ToString("MMM d");
        }
    }

    public string DurationDisplay
    {
        get
        {
            if (MediaItem.DurationMs is not > 0) return "";
            var ts = TimeSpan.FromMilliseconds(MediaItem.DurationMs.Value);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }

    public LibraryItemViewModel(MediaItem mediaItem)
    {
        MediaItem = mediaItem;
    }
}

public class DuplicateGroupViewModel
{
    public long FileSize { get; init; }
    public List<LibraryItemViewModel> Items { get; init; } = [];

    public string SizeDisplay
    {
        get
        {
            if (FileSize < 1024) return $"{FileSize} B";
            if (FileSize < 1024 * 1024) return $"{FileSize / 1024.0:F1} KB";
            if (FileSize < 1024L * 1024 * 1024) return $"{FileSize / (1024.0 * 1024):F1} MB";
            return $"{FileSize / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}

public partial class MissingFileViewModel : ObservableObject
{
    public MediaItem MediaItem { get; }
    private readonly LibraryViewModel _parent;

    public string DisplayName => MediaItem.Filename;
    public string FullPath    => MediaItem.FullPath;

    public MissingFileViewModel(MediaItem item, LibraryViewModel parent)
    {
        MediaItem = item;
        _parent   = parent;
    }

    [RelayCommand]
    private async Task Relocate()
    {
        var file = _parent.PickVideoFileAction != null ? await _parent.PickVideoFileAction.Invoke() : null;
        if (file == null) return;
        await _parent.Dispatcher.SendAsync(new RelocateMediaFileCommand(MediaItem.Id, file));
        _parent.MissingFiles.Remove(this);
        _parent.MissingCount = _parent.MissingFiles.Count;
    }

    [RelayCommand]
    private async Task Remove()
    {
        await _parent.Dispatcher.SendAsync(new DeleteMediaFilesCommand([MediaItem]));
        _parent.MissingFiles.Remove(this);
        _parent.MissingCount = _parent.MissingFiles.Count;
    }
}
