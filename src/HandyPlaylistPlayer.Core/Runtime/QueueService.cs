using System.Collections.ObjectModel;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Runtime;

public class QueueService : IQueueService
{
    private readonly List<int> _history = [];

    public ObservableCollection<QueueItem> Items { get; } = [];
    public QueueItem? CurrentItem { get; private set; }
    public int CurrentIndex { get; private set; } = -1;
    public ShuffleMode ShuffleMode { get; set; } = ShuffleMode.Off;
    public RepeatMode RepeatMode { get; set; } = RepeatMode.Off;

    public event EventHandler<QueueItem?>? CurrentItemChanged;

    public void Enqueue(IEnumerable<QueueItem> items)
    {
        foreach (var item in items)
        {
            item.QueueIndex = Items.Count;
            Items.Add(item);
        }
    }

    public void EnqueueNext(QueueItem item)
    {
        var insertIndex = CurrentIndex + 1;
        if (insertIndex > Items.Count) insertIndex = Items.Count;
        item.QueueIndex = insertIndex;
        Items.Insert(insertIndex, item);
        ReindexFrom(insertIndex);
    }

    public void Remove(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        Items.RemoveAt(index);
        ReindexFrom(index);

        // Adjust history entries to account for the removed index
        for (int h = _history.Count - 1; h >= 0; h--)
        {
            if (_history[h] == index)
                _history.RemoveAt(h);
            else if (_history[h] > index)
                _history[h]--;
        }

        if (index == CurrentIndex)
            SetCurrent(CurrentIndex < Items.Count ? CurrentIndex : -1);
        else if (index < CurrentIndex)
            CurrentIndex--;
    }

    public void Reorder(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= Items.Count) return;
        if (newIndex < 0 || newIndex >= Items.Count) return;

        var item = Items[oldIndex];
        Items.RemoveAt(oldIndex);
        Items.Insert(newIndex, item);
        ReindexFrom(Math.Min(oldIndex, newIndex));

        if (CurrentIndex == oldIndex)
            CurrentIndex = newIndex;
        else if (oldIndex < CurrentIndex && newIndex >= CurrentIndex)
            CurrentIndex--;
        else if (oldIndex > CurrentIndex && newIndex <= CurrentIndex)
            CurrentIndex++;

        // History indices are invalidated by reorder
        _history.Clear();
    }

    public void Clear()
    {
        Items.Clear();
        CurrentIndex = -1;
        CurrentItem = null;
        _history.Clear();
        CurrentItemChanged?.Invoke(this, null);
    }

    public QueueItem? JumpTo(int index)
    {
        if (index < 0 || index >= Items.Count) return null;
        if (CurrentIndex >= 0)
            _history.Add(CurrentIndex);
        SetCurrent(index);
        return CurrentItem;
    }

    public QueueItem? Next()
    {
        if (Items.Count == 0) return null;

        // RepeatOne: replay current track
        if (RepeatMode == RepeatMode.RepeatOne && CurrentIndex >= 0)
        {
            SetCurrent(CurrentIndex);
            return CurrentItem;
        }

        if (CurrentIndex >= 0)
            _history.Add(CurrentIndex);

        var nextIndex = CurrentIndex + 1;

        if (ShuffleMode == ShuffleMode.ShuffleOnce && nextIndex >= Items.Count)
        {
            // Shuffle once then switch to linear playback
            Shuffle();
            ShuffleMode = ShuffleMode.Off;
            nextIndex = 0;
        }
        else if (ShuffleMode == ShuffleMode.ContinuousShuffle && nextIndex >= Items.Count)
        {
            Shuffle();
            nextIndex = 0;
        }
        else if (RepeatMode == RepeatMode.RepeatAll && nextIndex >= Items.Count)
        {
            nextIndex = 0;
        }

        if (nextIndex >= Items.Count) return null;

        SetCurrent(nextIndex);
        return CurrentItem;
    }

    public QueueItem? Previous()
    {
        if (_history.Count > 0)
        {
            var prevIndex = _history[^1];
            _history.RemoveAt(_history.Count - 1);

            // Validate the history index is still in bounds
            if (prevIndex >= 0 && prevIndex < Items.Count)
            {
                SetCurrent(prevIndex);
                return CurrentItem;
            }
            // Index was stale — fall through to linear navigation
        }

        if (CurrentIndex > 0)
        {
            SetCurrent(CurrentIndex - 1);
            return CurrentItem;
        }

        return null;
    }

    public void SortByComparison(Comparison<QueueItem> comparison)
    {
        var currentItem = CurrentItem;
        var sorted = Items.ToList();
        sorted.Sort(comparison);

        Items.Clear();
        foreach (var item in sorted)
            Items.Add(item);

        ReindexFrom(0);
        CurrentIndex = currentItem != null ? Items.IndexOf(currentItem) : -1;
        _history.Clear();
    }

    public void Shuffle()
    {
        var currentItem = CurrentIndex >= 0 && CurrentIndex < Items.Count ? Items[CurrentIndex] : null;

        // Copy, shuffle via Fisher-Yates, then clear+re-add (same pattern as SortByComparison)
        // to avoid per-swap CollectionChanged events that crash the UI.
        var list = Items.ToList();
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        Items.Clear();
        foreach (var item in list)
            Items.Add(item);

        ReindexFrom(0);
        CurrentIndex = currentItem != null ? Items.IndexOf(currentItem) : -1;
        _history.Clear();
    }

    private void SetCurrent(int index)
    {
        CurrentIndex = index;
        CurrentItem = index >= 0 && index < Items.Count ? Items[index] : null;
        CurrentItemChanged?.Invoke(this, CurrentItem);
    }

    private void ReindexFrom(int startIndex)
    {
        for (int i = startIndex; i < Items.Count; i++)
            Items[i].QueueIndex = i;
    }
}
