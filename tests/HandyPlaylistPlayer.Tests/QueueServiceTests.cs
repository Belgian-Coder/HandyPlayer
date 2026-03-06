using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Runtime;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class QueueServiceTests
{
    private static QueueItem MakeItem(int id, string name) => new()
    {
        Video = new MediaItem { Id = id, Filename = name, FullPath = $"/videos/{name}.mp4", Extension = ".mp4" }
    };

    [Fact]
    public void Enqueue_AddsItems()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C")]);
        Assert.Equal(3, svc.Items.Count);
    }

    [Fact]
    public void Next_AdvancesToNextItem()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B")]);

        var first = svc.Next();
        Assert.Equal(1, first!.Video.Id);
        Assert.Equal(0, svc.CurrentIndex);

        var second = svc.Next();
        Assert.Equal(2, second!.Video.Id);
        Assert.Equal(1, svc.CurrentIndex);
    }

    [Fact]
    public void Next_PastEnd_ReturnsNull()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A")]);
        svc.Next();
        Assert.Null(svc.Next());
    }

    [Fact]
    public void Previous_GoesBack()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C")]);
        svc.Next(); // 0
        svc.Next(); // 1
        svc.Next(); // 2

        var prev = svc.Previous();
        Assert.Equal(2, prev!.Video.Id);
    }

    [Fact]
    public void Clear_EmptiesQueue()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B")]);
        svc.Next();
        svc.Clear();

        Assert.Empty(svc.Items);
        Assert.Null(svc.CurrentItem);
        Assert.Equal(-1, svc.CurrentIndex);
    }

    [Fact]
    public void Shuffle_ReordersItems()
    {
        var svc = new QueueService();
        var items = Enumerable.Range(1, 20).Select(i => MakeItem(i, $"Item{i}")).ToList();
        svc.Enqueue(items);

        var originalOrder = svc.Items.Select(i => i.Video.Id).ToList();
        svc.Shuffle();
        var shuffledOrder = svc.Items.Select(i => i.Video.Id).ToList();

        // With 20 items, shuffle should produce a different order (extremely unlikely to be same)
        Assert.NotEqual(originalOrder, shuffledOrder);
        // Same items, different order
        Assert.Equal(originalOrder.OrderBy(x => x), shuffledOrder.OrderBy(x => x));
    }

    [Fact]
    public void EnqueueNext_InsertsAfterCurrent()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C")]);
        svc.Next(); // current = 0 (A)

        svc.EnqueueNext(MakeItem(4, "D"));
        Assert.Equal(4, svc.Items[1].Video.Id); // D inserted at position 1
        Assert.Equal(2, svc.Items[2].Video.Id); // B pushed to position 2
    }

    [Fact]
    public void Remove_UpdatesIndices()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C")]);
        svc.Remove(1);

        Assert.Equal(2, svc.Items.Count);
        Assert.Equal(1, svc.Items[0].Video.Id);
        Assert.Equal(3, svc.Items[1].Video.Id);
    }

    [Fact]
    public void ContinuousShuffle_WrapsAround()
    {
        var svc = new QueueService { ShuffleMode = ShuffleMode.ContinuousShuffle };
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B")]);
        svc.Next(); // 0
        svc.Next(); // 1
        var wrapped = svc.Next(); // should wrap around after shuffle
        Assert.NotNull(wrapped);
    }

    [Fact]
    public void Reorder_MovesItemForward()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C"), MakeItem(4, "D")]);

        svc.Reorder(0, 2);

        Assert.Equal(2, svc.Items[0].Video.Id);
        Assert.Equal(3, svc.Items[1].Video.Id);
        Assert.Equal(1, svc.Items[2].Video.Id);
        Assert.Equal(4, svc.Items[3].Video.Id);
    }

    [Fact]
    public void Reorder_MovesItemBackward()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C"), MakeItem(4, "D")]);

        svc.Reorder(3, 1);

        Assert.Equal(1, svc.Items[0].Video.Id);
        Assert.Equal(4, svc.Items[1].Video.Id);
        Assert.Equal(2, svc.Items[2].Video.Id);
        Assert.Equal(3, svc.Items[3].Video.Id);
    }

    [Fact]
    public void Reorder_UpdatesCurrentIndex_WhenCurrentMoved()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C")]);
        svc.Next(); // current = 0 (A)

        svc.Reorder(0, 2);

        Assert.Equal(2, svc.CurrentIndex);
        Assert.Equal(1, svc.CurrentItem!.Video.Id);
    }

    [Fact]
    public void Reorder_UpdatesCurrentIndex_WhenItemMovedPastCurrent()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C")]);
        svc.Next(); // 0
        svc.Next(); // current = 1 (B)

        svc.Reorder(0, 2);

        Assert.Equal(0, svc.CurrentIndex);
    }

    [Fact]
    public void Reorder_UpdatesCurrentIndex_WhenItemMovedBeforeCurrent()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C")]);
        svc.Next(); // 0
        svc.Next(); // current = 1 (B)

        svc.Reorder(2, 0);

        Assert.Equal(2, svc.CurrentIndex);
    }

    [Fact]
    public void Reorder_InvalidIndices_NoChange()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B")]);

        svc.Reorder(-1, 0);
        Assert.Equal(1, svc.Items[0].Video.Id);
        Assert.Equal(2, svc.Items[1].Video.Id);

        svc.Reorder(0, 5);
        Assert.Equal(1, svc.Items[0].Video.Id);
        Assert.Equal(2, svc.Items[1].Video.Id);
    }

    [Fact]
    public void Reorder_UpdatesQueueIndices()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C")]);

        svc.Reorder(2, 0);

        for (int i = 0; i < svc.Items.Count; i++)
            Assert.Equal(i, svc.Items[i].QueueIndex);
    }

    [Fact]
    public void Remove_CurrentItem_UpdatesCurrentToNextItem()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C")]);
        svc.Next(); // 0
        svc.Next(); // current = 1 (B)

        svc.Remove(1);

        Assert.Equal(1, svc.CurrentIndex);
        Assert.Equal(3, svc.CurrentItem!.Video.Id);
    }

    [Fact]
    public void Remove_BeforeCurrent_AdjustsIndex()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C")]);
        svc.Next(); // 0
        svc.Next(); // 1
        svc.Next(); // current = 2 (C)

        svc.Remove(0);

        Assert.Equal(1, svc.CurrentIndex);
        Assert.Equal(3, svc.CurrentItem!.Video.Id);
    }

    [Fact]
    public void CurrentItemChanged_FiresOnNext()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A")]);

        QueueItem? firedItem = null;
        svc.CurrentItemChanged += (_, item) => firedItem = item;

        svc.Next();

        Assert.NotNull(firedItem);
        Assert.Equal(1, firedItem!.Video.Id);
    }

    [Fact]
    public void Shuffle_PreservesCurrentItem()
    {
        var svc = new QueueService();
        svc.Enqueue(Enumerable.Range(1, 20).Select(i => MakeItem(i, $"Item{i}")).ToList());
        svc.Next(); // current = 0
        svc.Next(); // current = 1
        var currentBefore = svc.CurrentItem;

        svc.Shuffle();

        // CurrentIndex should point to the same item after shuffle
        Assert.Same(currentBefore, svc.Items[svc.CurrentIndex]);
    }

    [Fact]
    public void Shuffle_ClearsHistory()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C")]);
        svc.Next(); // 0
        svc.Next(); // 1 — history has [0]
        svc.Next(); // 2 — history has [0, 1]

        svc.Shuffle();

        // After shuffle, Previous should fall through to linear navigation since history is cleared
        var prev = svc.Previous();
        if (svc.CurrentIndex > 0)
        {
            Assert.NotNull(prev);
            // Should navigate to adjacent item, not arbitrary historical index
        }
    }

    [Fact]
    public void Reorder_ClearsHistory()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C")]);
        svc.Next(); // 0
        svc.Next(); // 1 — history has [0]

        svc.Reorder(2, 0);

        // History cleared, so Previous uses linear fallback
        var prev = svc.Previous();
        Assert.NotNull(prev);
    }

    [Fact]
    public void Previous_StaleHistoryIndex_FallsBackToLinear()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C"), MakeItem(4, "D")]);
        svc.Next(); // 0 (A)
        svc.Next(); // 1 (B)
        svc.Next(); // 2 (C)
        svc.Next(); // 3 (D)

        // Remove items to make history potentially stale
        svc.Remove(2); // Remove item at index 2
        svc.Remove(1); // Remove item at index 1

        // Previous should handle gracefully even with adjusted/stale indices
        var prev = svc.Previous();
        if (prev != null)
            Assert.Contains(prev, svc.Items);
    }

    [Fact]
    public void ShuffleOnce_ShufflesAndSwitchesToOff()
    {
        var svc = new QueueService { ShuffleMode = ShuffleMode.ShuffleOnce };
        svc.Enqueue(Enumerable.Range(1, 10).Select(i => MakeItem(i, $"Item{i}")).ToList());

        // Play through all items
        for (int i = 0; i < 10; i++) svc.Next();

        // Next past end should trigger shuffle and wrap
        var wrapped = svc.Next();
        Assert.NotNull(wrapped);
        Assert.Equal(ShuffleMode.Off, svc.ShuffleMode);
    }

    [Fact]
    public void Remove_AdjustsHistoryIndices()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B"), MakeItem(3, "C"), MakeItem(4, "D")]);
        svc.Next(); // 0 (A)
        svc.Next(); // 1 (B)
        svc.Next(); // 2 (C)
        svc.Next(); // 3 (D) — history has [0, 1, 2]

        // Remove item at index 1 (B)
        svc.Remove(1);
        // History entries: 0 stays, 1 (B) removed, 2 decremented to 1
        // Remaining history should be [0, 1] pointing to A and C

        var prev1 = svc.Previous();
        Assert.NotNull(prev1);
        Assert.Equal(3, prev1!.Video.Id); // C (was index 2, now index 1)
    }

    [Fact]
    public void Shuffle_EmptyQueue_NoOp()
    {
        var svc = new QueueService();
        svc.Shuffle(); // should not throw
        Assert.Empty(svc.Items);
    }

    [Fact]
    public void Shuffle_SingleItem_RemainsInPlace()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A")]);
        svc.Shuffle();
        Assert.Single(svc.Items);
        Assert.Equal(1, svc.Items[0].Video.Id);
    }

    [Fact]
    public void Next_EmptyQueue_ReturnsNull()
    {
        var svc = new QueueService();
        Assert.Null(svc.Next());
        Assert.Equal(-1, svc.CurrentIndex);
    }

    [Fact]
    public void Previous_EmptyQueue_ReturnsNull()
    {
        var svc = new QueueService();
        Assert.Null(svc.Previous());
    }

    [Fact]
    public void Previous_AtStart_ReturnsNull()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A"), MakeItem(2, "B")]);
        svc.Next(); // current = 0
        Assert.Null(svc.Previous());
    }

    [Fact]
    public void Remove_LastItem_ClearsCurrentItem()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "A")]);
        svc.Next(); // current = 0
        svc.Remove(0);
        Assert.Empty(svc.Items);
        Assert.Null(svc.CurrentItem);
    }

    [Fact]
    public void EnqueueNext_EmptyQueue_InsertsAtStart()
    {
        var svc = new QueueService();
        svc.EnqueueNext(MakeItem(1, "A"));
        Assert.Single(svc.Items);
        Assert.Equal(1, svc.Items[0].Video.Id);
    }

    // ── SortByComparison tests ──────────────────────────────────

    [Fact]
    public void SortByComparison_SortsByTitle()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(3, "Charlie"), MakeItem(1, "Alpha"), MakeItem(2, "Bravo")]);

        svc.SortByComparison((a, b) => string.Compare(a.Video.Filename, b.Video.Filename, StringComparison.OrdinalIgnoreCase));

        Assert.Equal("Alpha", svc.Items[0].Video.Filename);
        Assert.Equal("Bravo", svc.Items[1].Video.Filename);
        Assert.Equal("Charlie", svc.Items[2].Video.Filename);
    }

    [Fact]
    public void SortByComparison_SortsByTitleDescending()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "Alpha"), MakeItem(2, "Bravo"), MakeItem(3, "Charlie")]);

        svc.SortByComparison((a, b) => string.Compare(b.Video.Filename, a.Video.Filename, StringComparison.OrdinalIgnoreCase));

        Assert.Equal("Charlie", svc.Items[0].Video.Filename);
        Assert.Equal("Bravo", svc.Items[1].Video.Filename);
        Assert.Equal("Alpha", svc.Items[2].Video.Filename);
    }

    [Fact]
    public void SortByComparison_PreservesCurrentItem()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(3, "Charlie"), MakeItem(1, "Alpha"), MakeItem(2, "Bravo")]);
        svc.Next(); // current = Charlie (index 0)

        svc.SortByComparison((a, b) => string.Compare(a.Video.Filename, b.Video.Filename, StringComparison.OrdinalIgnoreCase));

        // Charlie moved to index 2 after sort
        Assert.Equal(2, svc.CurrentIndex);
        Assert.Equal("Charlie", svc.CurrentItem!.Video.Filename);
    }

    [Fact]
    public void SortByComparison_EmptyQueue_NoException()
    {
        var svc = new QueueService();
        svc.SortByComparison((a, b) => string.Compare(a.Video.Filename, b.Video.Filename, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(svc.Items);
    }

    [Fact]
    public void SortByComparison_SingleItem_NoChange()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(1, "Only")]);
        svc.Next();

        svc.SortByComparison((a, b) => string.Compare(a.Video.Filename, b.Video.Filename, StringComparison.OrdinalIgnoreCase));

        Assert.Single(svc.Items);
        Assert.Equal(0, svc.CurrentIndex);
    }

    [Fact]
    public void SortByComparison_NoCurrentItem_IndexRemainsNegative()
    {
        var svc = new QueueService();
        svc.Enqueue([MakeItem(2, "Bravo"), MakeItem(1, "Alpha")]);

        svc.SortByComparison((a, b) => string.Compare(a.Video.Filename, b.Video.Filename, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(-1, svc.CurrentIndex);
        Assert.Equal("Alpha", svc.Items[0].Video.Filename);
    }
}
