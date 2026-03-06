using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandyPlaylistPlayer.Core.Dispatching;
using IDispatcher = HandyPlaylistPlayer.Core.Dispatching.IDispatcher;
using HandyPlaylistPlayer.Core.Features.Library.DeleteMediaFiles;
using HandyPlaylistPlayer.Core.Features.Playlists.AddPlaylistItem;
using HandyPlaylistPlayer.Core.Features.Playlists.CreatePlaylist;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.App.ViewModels;

public partial class QueuePanelViewModel : ObservableObject
{
    private readonly IQueueService _queue;
    private readonly IDispatcher _dispatcher;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isRightPosition = true;
    [ObservableProperty] private QueueItemViewModel? _selectedItem;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JumpToNextUnwatchedCommand))]
    private int _currentIndex = -1;
    [ObservableProperty] private string _saveStatus = string.Empty;

    private string? _thumbnailDir;

    public ObservableCollection<QueueItemViewModel> Items { get; } = [];

    /// <summary>Set by the View to show a confirmation dialog before destructive actions.</summary>
    public Func<string, Task<bool>>? ConfirmAction { get; set; }

    public string ShuffleModeText => _queue.ShuffleMode switch
    {
        ShuffleMode.ShuffleOnce => "Shuffle 1x",
        ShuffleMode.ContinuousShuffle => "Shuffle \u221E",
        _ => "Shuffle"
    };

    public string RepeatModeText => _queue.RepeatMode switch
    {
        RepeatMode.RepeatAll => "Repeat All",
        RepeatMode.RepeatOne => "Repeat 1",
        _ => "Repeat"
    };

    public bool IsShuffleActive => _queue.ShuffleMode != ShuffleMode.Off;
    public bool IsRepeatActive => _queue.RepeatMode != RepeatMode.Off;

    public string HeaderText
    {
        get
        {
            var totalMs = Items.Sum(i => i.QueueItem.Video.DurationMs ?? 0);
            if (totalMs > 0)
            {
                var ts = TimeSpan.FromMilliseconds(totalMs);
                var dur = ts.TotalHours >= 1
                    ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
                return $"Queue ({Items.Count}) \u2022 {dur}";
            }
            return $"Queue ({Items.Count})";
        }
    }

    public QueuePanelViewModel(IQueueService queue, IDispatcher dispatcher)
    {
        _queue = queue;
        _dispatcher = dispatcher;

        // Sync initial state
        SyncItems();
        CurrentIndex = _queue.CurrentIndex;

        // Listen for queue changes
        _queue.Items.CollectionChanged += OnQueueCollectionChanged;
        _queue.CurrentItemChanged += OnCurrentItemChanged;
    }

    [RelayCommand]
    private void TogglePanel() => IsVisible = !IsVisible;

    [RelayCommand]
    private void TogglePosition() => IsRightPosition = !IsRightPosition;

    [RelayCommand]
    private void PlayItem(QueueItemViewModel? item)
    {
        if (item == null) return;
        var index = Items.IndexOf(item);
        if (index >= 0)
            _queue.JumpTo(index);
    }

    [RelayCommand]
    private void PlayNext(QueueItemViewModel? item)
    {
        if (item == null) return;
        var index = Items.IndexOf(item);
        if (index < 0) return;
        // Move the item to right after the current track
        var targetIndex = _queue.CurrentIndex + 1;
        if (index != targetIndex && targetIndex < _queue.Items.Count)
            _queue.Reorder(index, targetIndex);
    }

    [RelayCommand]
    private void RemoveItem(QueueItemViewModel? item)
    {
        if (item == null) return;
        var index = Items.IndexOf(item);
        if (index >= 0)
            _queue.Remove(index);
    }

    [RelayCommand]
    private void RemoveSelected(System.Collections.IList? selectedItems)
    {
        if (selectedItems == null || selectedItems.Count == 0) return;
        var indices = selectedItems.OfType<QueueItemViewModel>()
            .Select(vm => Items.IndexOf(vm))
            .Where(i => i >= 0)
            .OrderByDescending(i => i)
            .ToList();
        foreach (var index in indices)
            _queue.Remove(index);
    }

    [RelayCommand]
    private async Task DeleteFromDisk(QueueItemViewModel? item)
    {
        if (item == null) return;
        var video  = item.QueueItem.Video;
        var script = item.QueueItem.Script;

        var message = script != null
            ? $"Delete '{item.DisplayName}' and its paired script from disk?\n\nThis cannot be undone."
            : $"Delete '{item.DisplayName}' from disk?\n\nThis cannot be undone.";

        if (ConfirmAction != null && !await ConfirmAction(message))
            return;

        var toDelete = new List<MediaItem> { video };
        if (script != null)
            toDelete.Add(script);

        await _dispatcher.SendAsync(new DeleteMediaFilesCommand(toDelete));

        if (!File.Exists(video.FullPath))
        {
            var index = Items.IndexOf(item);
            if (index >= 0)
                _queue.Remove(index);
        }
        else
        {
            SaveStatus = "Could not delete — file may be in use";
            _ = ClearSaveStatusAfterDelay();
        }
    }

    [RelayCommand]
    private void ClearQueue()
    {
        _queue.Clear();
    }

    public void MoveItem(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= _queue.Items.Count) return;
        if (newIndex < 0 || newIndex >= _queue.Items.Count) return;
        if (oldIndex == newIndex) return;

        // Unsubscribe before reorder: Reorder fires Remove+Insert events that get posted
        // deferred to the UI thread. If we let them run after our manual SyncItems() below,
        // they'll remove/re-add from the already-correct list → duplicate items displayed.
        _queue.Items.CollectionChanged -= OnQueueCollectionChanged;
        try
        {
            _queue.Reorder(oldIndex, newIndex);
        }
        finally
        {
            _queue.Items.CollectionChanged += OnQueueCollectionChanged;
        }

        SyncItems();
        CurrentIndex = _queue.CurrentIndex;
        UpdateCurrentHighlight();
        OnPropertyChanged(nameof(HeaderText));
    }

    [RelayCommand]
    private void CycleShuffleMode()
    {
        _queue.ShuffleMode = _queue.ShuffleMode switch
        {
            ShuffleMode.Off => ShuffleMode.ShuffleOnce,
            ShuffleMode.ShuffleOnce => ShuffleMode.ContinuousShuffle,
            _ => ShuffleMode.Off
        };

        // On first activation, shuffle the queue immediately
        if (_queue.ShuffleMode == ShuffleMode.ShuffleOnce)
        {
            _queue.Items.CollectionChanged -= OnQueueCollectionChanged;
            try { _queue.Shuffle(); }
            finally { _queue.Items.CollectionChanged += OnQueueCollectionChanged; }
            SyncItems();
            CurrentIndex = _queue.CurrentIndex;
            UpdateCurrentHighlight();
        }

        OnPropertyChanged(nameof(ShuffleModeText));
        OnPropertyChanged(nameof(IsShuffleActive));
    }

    [RelayCommand]
    private async Task SaveAsPlaylist()
    {
        if (_queue.Items.Count == 0) return;
        try
        {
            var name = $"Queue {DateTime.Now:yyyy-MM-dd HH:mm}";
            var playlistId = await _dispatcher.SendAsync(new CreatePlaylistCommand(name));
            for (int i = 0; i < _queue.Items.Count; i++)
            {
                await _dispatcher.SendAsync(new AddPlaylistItemCommand(playlistId, _queue.Items[i].Video.Id, i));
            }
            SaveStatus = $"Saved as '{name}'";
            _ = ClearSaveStatusAfterDelay();
        }
        catch (Exception)
        {
            SaveStatus = "Save failed";
        }
    }

    private async Task ClearSaveStatusAfterDelay()
    {
        await Task.Delay(3000);
        SaveStatus = string.Empty;
    }

    [RelayCommand]
    private void CycleRepeatMode()
    {
        _queue.RepeatMode = _queue.RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.RepeatAll,
            RepeatMode.RepeatAll => RepeatMode.RepeatOne,
            _ => RepeatMode.Off
        };

        OnPropertyChanged(nameof(RepeatModeText));
        OnPropertyChanged(nameof(IsRepeatActive));
    }

    [RelayCommand]
    private void SortQueue(string? mode)
    {
        if (_queue.Items.Count < 2) return;

        Comparison<QueueItem> comparison = mode switch
        {
            "TitleDesc"    => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(
                                 Path.GetFileNameWithoutExtension(b.Video.Filename),
                                 Path.GetFileNameWithoutExtension(a.Video.Filename)),
            "Duration"     => (a, b) => (a.Video.DurationMs ?? 0).CompareTo(b.Video.DurationMs ?? 0),
            "DurationDesc" => (a, b) => (b.Video.DurationMs ?? 0).CompareTo(a.Video.DurationMs ?? 0),
            "Unwatched"    => (a, b) => (a.Video.WatchedAt.HasValue ? 1 : 0)
                                            .CompareTo(b.Video.WatchedAt.HasValue ? 1 : 0),
            "Watched"      => (a, b) => (b.Video.WatchedAt.HasValue ? 1 : 0)
                                            .CompareTo(a.Video.WatchedAt.HasValue ? 1 : 0),
            _              => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(
                                 Path.GetFileNameWithoutExtension(a.Video.Filename),
                                 Path.GetFileNameWithoutExtension(b.Video.Filename)),
        };

        // Temporarily unsubscribe so deferred CollectionChanged posts don't duplicate items
        // after we manually call SyncItems() below.
        _queue.Items.CollectionChanged -= OnQueueCollectionChanged;
        try
        {
            _queue.SortByComparison(comparison);
        }
        finally
        {
            _queue.Items.CollectionChanged += OnQueueCollectionChanged;
        }

        SyncItems();
        CurrentIndex = _queue.CurrentIndex;
        UpdateCurrentHighlight();
        OnPropertyChanged(nameof(HeaderText));
    }

    [RelayCommand(CanExecute = nameof(HasNextUnwatched))]
    private void JumpToNextUnwatched()
    {
        var start = CurrentIndex + 1;
        var next  = Items.Skip(start).FirstOrDefault(i => !i.IsWatched)
                    ?? Items.Take(Math.Max(CurrentIndex, 0)).FirstOrDefault(i => !i.IsWatched);
        if (next != null) PlayItem(next);
    }

    private bool HasNextUnwatched() => Items.Any(i => !i.IsWatched);

    public void SetThumbnailDir(string dir) => _thumbnailDir = dir;

    private void LoadThumbnailForItem(QueueItemViewModel vm)
    {
        if (_thumbnailDir == null) return;
        var path = Path.Combine(_thumbnailDir, $"{vm.QueueItem.Video.Id}.jpg");
        if (File.Exists(path))
        {
            try { vm.Thumbnail = new Avalonia.Media.Imaging.Bitmap(path); }
            catch (Exception) { /* corrupt or locked thumbnail file — skip */ }
        }
    }

    /// <summary>Updates the watched indicator on queue items matching the given media file ID.</summary>
    public void MarkWatched(int mediaFileId)
    {
        foreach (var item in Items)
        {
            if (item.QueueItem.Video.Id == mediaFileId)
                item.IsWatched = true;
        }
    }

    /// <summary>Clears the watched indicator on queue items matching the given media file ID.</summary>
    public void MarkUnwatched(int mediaFileId)
    {
        foreach (var item in Items)
        {
            if (item.QueueItem.Video.Id == mediaFileId)
                item.IsWatched = false;
        }
    }

    private void OnQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SyncItems(e);
            OnPropertyChanged(nameof(HeaderText));
        });
    }

    private void OnCurrentItemChanged(object? sender, QueueItem? item)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CurrentIndex = _queue.CurrentIndex;
            UpdateCurrentHighlight();
        });
    }

    private void SyncItems(NotifyCollectionChangedEventArgs? e = null)
    {
        // For simple add/remove, apply incremental updates to avoid full rebuild
        // but only if the view model count matches expectations (guards against stale events)
        // Guard: skip stale Add events that arrive after a Reset already rebuilt the correct state.
        // (e.g., Clear() + Enqueue() fires Reset then Add — by the time the deferred Add runs,
        // the Reset handler already populated Items from the queue's current state.)
        if (e is { Action: NotifyCollectionChangedAction.Add, NewItems: { Count: > 0 } newItems, NewStartingIndex: >= 0 }
            && e.NewStartingIndex <= Items.Count
            && Items.Count + newItems.Count <= _queue.Items.Count)
        {
            for (int i = 0; i < newItems.Count; i++)
            {
                if (newItems[i] is QueueItem qi)
                {
                    var vm = new QueueItemViewModel(qi, e.NewStartingIndex + i == _queue.CurrentIndex);
                    Items.Insert(e.NewStartingIndex + i, vm);
                    LoadThumbnailForItem(vm);
                }
            }
            OnPropertyChanged(nameof(HeaderText));
            return;
        }

        if (e is { Action: NotifyCollectionChangedAction.Remove, OldStartingIndex: >= 0, OldItems.Count: > 0 }
            && e.OldStartingIndex < Items.Count)
        {
            for (int i = e.OldItems.Count - 1; i >= 0; i--)
            {
                var idx = e.OldStartingIndex + i;
                if (idx < Items.Count)
                    Items.RemoveAt(idx);
            }
            UpdateCurrentHighlight();
            OnPropertyChanged(nameof(HeaderText));
            return;
        }

        // Full rebuild for reset/replace/move, when indices aren't available,
        // or when counts have diverged
        Items.Clear();
        for (int i = 0; i < _queue.Items.Count; i++)
        {
            var qi = _queue.Items[i];
            var vm = new QueueItemViewModel(qi, i == _queue.CurrentIndex);
            Items.Add(vm);
            LoadThumbnailForItem(vm);
        }
        OnPropertyChanged(nameof(HeaderText));
    }

    private void UpdateCurrentHighlight()
    {
        for (int i = 0; i < Items.Count; i++)
            Items[i].IsCurrent = i == CurrentIndex;
    }
}

public partial class QueueItemViewModel : ObservableObject
{
    public QueueItem QueueItem { get; }

    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isWatched;
    [ObservableProperty] private bool _isDraggingThis;
    [ObservableProperty] private bool _isDropTarget;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _thumbnail;

    public string DisplayName => Path.GetFileNameWithoutExtension(QueueItem.Video.Filename);
    public bool HasScript => QueueItem.Script != null;
    public string Duration => QueueItem.Video.DurationMs.HasValue
        ? TimeSpan.FromMilliseconds(QueueItem.Video.DurationMs.Value).ToString(@"mm\:ss")
        : "";

    public QueueItemViewModel(QueueItem queueItem, bool isCurrent)
    {
        QueueItem = queueItem;
        _isCurrent = isCurrent;
        _isWatched = queueItem.Video.WatchedAt.HasValue;
    }
}
