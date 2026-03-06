using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandyPlaylistPlayer.Core;
using HandyPlaylistPlayer.Core.Dispatching;
using IDispatcher = HandyPlaylistPlayer.Core.Dispatching.IDispatcher;
using HandyPlaylistPlayer.Core.Features.Playlists.CreatePlaylist;
using HandyPlaylistPlayer.Core.Features.Playlists.DeletePlaylist;
using HandyPlaylistPlayer.Core.Features.Playlists.GetAllPlaylists;
using HandyPlaylistPlayer.Core.Features.Library.FindScriptForVideo;
using HandyPlaylistPlayer.Core.Features.Library.GetLibraryItems;
using HandyPlaylistPlayer.Core.Features.Playlists.GetPlaylistItems;
using HandyPlaylistPlayer.Core.Features.Playlists.RemovePlaylistItem;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Runtime;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.App.ViewModels;

public partial class PlaylistListViewModel : ObservableObject
{
    private readonly IDispatcher _dispatcher;
    private readonly IPlaylistRepository _playlistRepo;
    private readonly IQueueService _queue;
    private readonly OverrideControlsViewModel _overrideControls;
    private readonly ILogger<PlaylistListViewModel> _logger;

    [ObservableProperty] private string _statusText = "Playlists";
    [ObservableProperty] private string _newPlaylistName = string.Empty;
    [ObservableProperty] private Playlist? _selectedPlaylist;
    [ObservableProperty] private MediaItem? _selectedPlaylistItem;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = string.Empty;
    [ObservableProperty] private string _playlistItemFilter = string.Empty;

    public ObservableCollection<Playlist> Playlists { get; } = [];
    public ObservableCollection<MediaItem> SelectedPlaylistItems { get; } = [];
    public ObservableCollection<MediaItem> FilteredPlaylistItems { get; } = [];
    public Action? OnPlaybackStarted { get; set; }
    public Func<string, Task<string?>>? SaveFileAction { get; set; }
    public Func<Task<string?>>? OpenFileAction { get; set; }
    public Func<string, string, Task<string?>>? PromptAction { get; set; }

    public PlaylistListViewModel(
        IDispatcher dispatcher,
        IPlaylistRepository playlistRepo,
        IQueueService queue,
        OverrideControlsViewModel overrideControls,
        ILogger<PlaylistListViewModel> logger)
    {
        _dispatcher = dispatcher;
        _playlistRepo = playlistRepo;
        _queue = queue;
        _overrideControls = overrideControls;
        _logger = logger;
        _ = LoadAsync();
    }

    public async Task ReloadAsync()
    {
        await LoadAsync();
        // Also refresh items for the currently selected playlist
        if (SelectedPlaylist != null)
            await LoadPlaylistItemsAsync(SelectedPlaylist);
    }

    private async Task LoadAsync()
    {
        try
        {
            var playlists = await _dispatcher.QueryAsync(new GetAllPlaylistsQuery());
            var counts = await _playlistRepo.GetItemCountsAsync();
            foreach (var p in playlists)
                p.ItemCount = counts.GetValueOrDefault(p.Id);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Playlists.Clear();
                foreach (var p in playlists) Playlists.Add(p);
                StatusText = $"Playlists ({Playlists.Count})";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlists");
        }
    }

    [RelayCommand]
    private async Task CreatePlaylist()
    {
        if (string.IsNullOrWhiteSpace(NewPlaylistName)) return;
        try
        {
            await _dispatcher.SendAsync(new CreatePlaylistCommand(NewPlaylistName.Trim()));
            NewPlaylistName = string.Empty;
            await LoadAsync();
        }
        catch (ValidationException ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeletePlaylist(Playlist playlist)
    {
        if (playlist == null) return;
        await _dispatcher.SendAsync(new DeletePlaylistCommand(playlist.Id));
        if (SelectedPlaylist?.Id == playlist.Id)
        {
            SelectedPlaylist = null;
            SelectedPlaylistItems.Clear();
        }
        await LoadAsync();
    }

    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        PlaylistItemFilter = string.Empty;
        if (value != null)
            _ = LoadPlaylistItemsAsync(value);
        else
        {
            SelectedPlaylistItems.Clear();
            FilteredPlaylistItems.Clear();
        }
    }

    partial void OnPlaylistItemFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = PlaylistItemFilter;
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? SelectedPlaylistItems.ToList()
            : SelectedPlaylistItems.Where(i =>
                i.Filename.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                i.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        FilteredPlaylistItems.Clear();
        foreach (var item in filtered) FilteredPlaylistItems.Add(item);
    }

    private async Task LoadPlaylistItemsAsync(Playlist playlist)
    {
        try
        {
            _logger.LogInformation("Loading items for playlist {Id} '{Name}' (type={Type})", playlist.Id, playlist.Name, playlist.Type);
            var items = await _dispatcher.QueryAsync(new GetPlaylistItemsQuery(playlist.Id, playlist));
            _logger.LogInformation("Playlist {Id} returned {Count} items", playlist.Id, items.Count);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                SelectedPlaylistItems.Clear();
                foreach (var item in items) SelectedPlaylistItems.Add(item);
                ApplyFilter();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlist items for playlist {Id}", playlist.Id);
        }
    }

    [RelayCommand]
    private void StartRename()
    {
        if (SelectedPlaylist == null) return;
        RenameText = SelectedPlaylist.Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private async Task ConfirmRename()
    {
        if (SelectedPlaylist == null || string.IsNullOrWhiteSpace(RenameText)) return;
        try
        {
            await _playlistRepo.RenameAsync(SelectedPlaylist.Id, RenameText.Trim());
            SelectedPlaylist.Name = RenameText.Trim();
            IsRenaming = false;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename playlist");
        }
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private async Task RemovePlaylistItem(MediaItem? item)
    {
        if (item == null || SelectedPlaylist == null) return;
        try
        {
            await _dispatcher.SendAsync(new RemovePlaylistItemCommand(SelectedPlaylist.Id, item.Id));
            SelectedPlaylistItems.Remove(item);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove playlist item");
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedItems(List<MediaItem>? items)
    {
        if (items == null || items.Count == 0 || SelectedPlaylist == null) return;
        foreach (var item in items)
        {
            try
            {
                await _dispatcher.SendAsync(new RemovePlaylistItemCommand(SelectedPlaylist.Id, item.Id));
                SelectedPlaylistItems.Remove(item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove playlist item {Id}", item.Id);
            }
        }
        ApplyFilter();
    }

    public async void MoveItem(int oldIndex, int newIndex)
    {
        if (SelectedPlaylist == null) return;
        if (SelectedPlaylist.Type != PlaylistTypes.Static) return;
        if (oldIndex == newIndex) return;
        if (oldIndex < 0 || oldIndex >= SelectedPlaylistItems.Count) return;
        if (newIndex < 0 || newIndex >= SelectedPlaylistItems.Count) return;

        SelectedPlaylistItems.Move(oldIndex, newIndex);
        ApplyFilter();

        var ids = SelectedPlaylistItems.Select(i => i.Id).ToList();
        try { await _playlistRepo.ReorderItemsAsync(SelectedPlaylist.Id, ids); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to persist playlist reorder"); }
    }

    [RelayCommand]
    private async Task MoveItemUp(MediaItem? item)
    {
        if (item == null || SelectedPlaylist == null) return;
        var index = SelectedPlaylistItems.IndexOf(item);
        if (index <= 0) return;

        // Swap positions in DB
        var other = SelectedPlaylistItems[index - 1];
        await _playlistRepo.UpdateItemPositionAsync(SelectedPlaylist.Id, item.Id, index - 1);
        await _playlistRepo.UpdateItemPositionAsync(SelectedPlaylist.Id, other.Id, index);

        SelectedPlaylistItems.Move(index, index - 1);
        ApplyFilter();
    }

    [RelayCommand]
    private async Task MoveItemDown(MediaItem? item)
    {
        if (item == null || SelectedPlaylist == null) return;
        var index = SelectedPlaylistItems.IndexOf(item);
        if (index < 0 || index >= SelectedPlaylistItems.Count - 1) return;

        var other = SelectedPlaylistItems[index + 1];
        await _playlistRepo.UpdateItemPositionAsync(SelectedPlaylist.Id, item.Id, index + 1);
        await _playlistRepo.UpdateItemPositionAsync(SelectedPlaylist.Id, other.Id, index);

        SelectedPlaylistItems.Move(index, index + 1);
        ApplyFilter();
    }

    [RelayCommand]
    private async Task PlayAll()
    {
        if (SelectedPlaylistItems.Count == 0) return;
        ApplyPresetForPlaylist();
        _queue.Clear();
        _queue.Enqueue(await BuildQueueItemsAsync(SelectedPlaylistItems));
        _queue.Next();
        OnPlaybackStarted?.Invoke();
    }

    [RelayCommand]
    private async Task ShufflePlay()
    {
        if (SelectedPlaylistItems.Count == 0) return;
        ApplyPresetForPlaylist();
        _queue.Clear();
        _queue.Enqueue(await BuildQueueItemsAsync(SelectedPlaylistItems));
        _queue.ShuffleMode = ShuffleMode.ShuffleOnce;
        _queue.Shuffle();
        _queue.Next();
        OnPlaybackStarted?.Invoke();
    }

    [RelayCommand]
    private async Task PlayNextItem(MediaItem? item)
    {
        if (item == null) return;
        MediaItem? script = null;
        try { script = await _dispatcher.QueryAsync(new FindScriptForVideoQuery(item.Id)); }
        catch { /* pairing may not exist */ }
        _queue.EnqueueNext(new QueueItem { Video = item, Script = script });
    }

    [RelayCommand]
    private async Task CreateFolderPlaylist(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        try
        {
            var name = Path.GetFileName(folderPath.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(name)) name = folderPath;
            await _playlistRepo.CreateAsync(name, PlaylistTypes.Folder, folderPath.Trim());
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create folder playlist");
        }
    }

    [RelayCommand]
    private async Task CreateSmartPlaylist()
    {
        try
        {
            var filterType = PromptAction != null
                ? await PromptAction("Create Smart Playlist",
                    "Filter type:\n0 = Unwatched\n1 = Has script\n2 = No script\n3 = Multiple scripts\n4 = Watched")
                : "0";
            var filter = filterType switch
            {
                "0" => """{"watched":"no"}""",
                "1" => """{"paired":"yes"}""",
                "2" => """{"paired":"no"}""",
                "3" => """{"paired":"multiple"}""",
                "4" => """{"watched":"yes"}""",
                _   => """{"watched":"no"}"""
            };
            var defaultName = string.IsNullOrWhiteSpace(NewPlaylistName) ? "Smart Playlist" : NewPlaylistName.Trim();
            var name = PromptAction != null
                ? await PromptAction("Playlist Name", "Enter name for smart playlist:")
                : defaultName;
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            var id = await _playlistRepo.CreateAsync(name, PlaylistTypes.Smart);
            await _playlistRepo.UpdateFilterAsync(id, filter);
            NewPlaylistName = string.Empty;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create smart playlist");
        }
    }

    [RelayCommand]
    private async Task ExportM3U()
    {
        if (SelectedPlaylist == null) return;
        try
        {
            var items = await _dispatcher.QueryAsync(new GetPlaylistItemsQuery(SelectedPlaylist.Id, SelectedPlaylist));
            var path = SaveFileAction != null ? await SaveFileAction.Invoke($"{SelectedPlaylist.Name}.m3u") : null;
            if (path == null) return;
            var sb = new System.Text.StringBuilder("#EXTM3U\n");
            foreach (var item in items)
            {
                var dur = item.DurationMs.HasValue
                    ? ((int)(item.DurationMs.Value / 1000)).ToString() : "-1";
                sb.AppendLine($"#EXTINF:{dur},{Path.GetFileNameWithoutExtension(item.Filename)}");
                sb.AppendLine(item.FullPath);
            }
            await File.WriteAllTextAsync(path, sb.ToString());
            StatusText = $"Exported {items.Count} items to M3U";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export M3U");
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportM3U()
    {
        try
        {
            var path = OpenFileAction != null ? await OpenFileAction.Invoke() : null;
            if (path == null) return;
            var lines = await File.ReadAllLinesAsync(path);
            var filePaths = lines
                .Where(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .ToList();
            var allMedia = await _dispatcher.QueryAsync(new GetLibraryItemsQuery());
            var matched = allMedia
                .Where(m => !m.IsScript && filePaths.Any(p =>
                    string.Equals(p, m.FullPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(p), m.Filename, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (matched.Count == 0)
            {
                StatusText = "No matching library items found";
                return;
            }
            var playlistName = Path.GetFileNameWithoutExtension(path);
            await _playlistRepo.CreateAsync(playlistName);
            await LoadAsync();
            var newPlaylist = Playlists.FirstOrDefault(p => p.Name == playlistName);
            if (newPlaylist != null)
                await _playlistRepo.AddItemsBatchAsync(newPlaylist.Id, matched.Select(m => m.Id).ToList());
            await LoadAsync();
            StatusText = $"Imported {matched.Count} items into '{playlistName}'";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import M3U");
            StatusText = $"Import failed: {ex.Message}";
        }
    }

    private void ApplyPresetForPlaylist()
    {
        if (SelectedPlaylist == null) return;
        var preset = _overrideControls.Presets
            .FirstOrDefault(p => p.PlaylistId == SelectedPlaylist.Id);
        if (preset != null)
            _overrideControls.SelectedPreset = preset;
    }

    private async Task<List<QueueItem>> BuildQueueItemsAsync(IEnumerable<MediaItem> items)
    {
        var result = new List<QueueItem>();
        foreach (var m in items)
        {
            MediaItem? script = null;
            try { script = await _dispatcher.QueryAsync(new FindScriptForVideoQuery(m.Id)); }
            catch { /* pairing may not exist */ }
            result.Add(new QueueItem { Video = m, Script = script });
        }
        return result;
    }
}
