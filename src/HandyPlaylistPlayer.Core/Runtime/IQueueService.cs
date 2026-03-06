using System.Collections.ObjectModel;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Runtime;

public interface IQueueService
{
    ObservableCollection<QueueItem> Items { get; }
    QueueItem? CurrentItem { get; }
    int CurrentIndex { get; }
    ShuffleMode ShuffleMode { get; set; }
    RepeatMode RepeatMode { get; set; }

    void Enqueue(IEnumerable<QueueItem> items);
    void EnqueueNext(QueueItem item);
    void Remove(int index);
    void Reorder(int oldIndex, int newIndex);
    void Clear();
    QueueItem? Next();
    QueueItem? Previous();
    QueueItem? JumpTo(int index);
    void Shuffle();
    void SortByComparison(Comparison<QueueItem> comparison);

    event EventHandler<QueueItem?> CurrentItemChanged;
}
