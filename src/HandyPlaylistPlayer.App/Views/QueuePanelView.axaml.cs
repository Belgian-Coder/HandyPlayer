using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HandyPlaylistPlayer.App.ViewModels;

namespace HandyPlaylistPlayer.App.Views;

public partial class QueuePanelView : UserControl
{
    private static readonly DataFormat<string> DragFormat = DataFormat.CreateInProcessFormat<string>("HandyPlayer.QueueItemIndex");
    private const double DragThresholdPixels = 8;
    private Point _dragStartPoint;
    private PointerPressedEventArgs? _dragStartArgs;
    private bool _isDragging;
    private QueueItemViewModel? _draggingItem;
    private int _dropInsertIndex = -1;

    public QueuePanelView()
    {
        InitializeComponent();
        QueueList.DoubleTapped += OnDoubleTapped;
        DataContextChanged += OnDataContextChanged;

        // Drag-drop setup
        QueueList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        QueueList.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(QueueList, true);
        QueueList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        QueueList.AddHandler(DragDrop.DropEvent, OnDrop);
        QueueList.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is QueuePanelViewModel vm)
        {
            vm.ConfirmAction ??= async message =>
            {
                var window = this.FindAncestorOfType<Window>();
                if (window == null) return false;
                var dialog = new ConfirmDialog(message);
                var result = await dialog.ShowDialog<bool?>(window);
                return result == true;
            };
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is QueuePanelViewModel vm &&
            QueueList.SelectedItem is QueueItemViewModel item)
        {
            vm.PlayItemCommand.Execute(item);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStartPoint = e.GetPosition(QueueList);
        _dragStartArgs = e;
        _isDragging = false;
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging) return;
        if (!e.GetCurrentPoint(QueueList).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(QueueList);
        var diff = pos - _dragStartPoint;
        if (Math.Abs(diff.Y) < DragThresholdPixels) return;

        var listBoxItem = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (listBoxItem?.DataContext is not QueueItemViewModel item) return;

        var index = FindItemIndex(item);
        if (index < 0) return;

        var pressArgs = _dragStartArgs;
        // Only start from the press that began this gesture (stale args from an earlier press are ignored).
        if (pressArgs == null || pressArgs.Pointer != e.Pointer) return;

        _isDragging = true;
        _draggingItem = item;
        item.IsDraggingThis = true;

        // Set up drag ghost text
        DragGhostText.Text = item.DisplayName;
        DragGhost.IsVisible = true;

        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(DragFormat, index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await DragDrop.DoDragDropAsync(pressArgs, data, DragDropEffects.Move);
        }
        catch (Exception) { /* drag cancelled or failed — not critical */ }
        finally
        {
            _isDragging = false;
            _dragStartArgs = null;
            if (_draggingItem != null) { _draggingItem.IsDraggingThis = false; _draggingItem = null; }
            HideDragVisuals();
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DragFormat))
        {
            e.DragEffects = DragDropEffects.None;
            HideDragVisuals();
            return;
        }
        e.DragEffects = DragDropEffects.Move;

        if (!int.TryParse(e.DataTransfer.TryGetValue(DragFormat), out var srcIndex)) return;
        if (DataContext is not QueuePanelViewModel vm) return;

        var pos = e.GetPosition(QueueList);

        // Position the drag ghost near the cursor
        DragGhost.Margin = new Thickness(Math.Max(0, pos.X + 16), Math.Max(0, pos.Y - 12), 0, 0);

        // Find the ListBoxItem under the cursor and determine above/below
        var hovered = QueueList.GetVisualAt(pos)?.FindAncestorOfType<ListBoxItem>();
        if (hovered?.DataContext is QueueItemViewModel targetVm)
        {
            var tgtIndex = FindItemIndex(targetVm);
            if (tgtIndex < 0) { HideDropIndicator(); return; }

            // Translate item top to the overlay Panel's coordinate space
            var itemTopInList = hovered.TranslatePoint(new Point(0, 0), QueueList);
            if (itemTopInList == null) { HideDropIndicator(); return; }

            var itemH = hovered.Bounds.Height;
            var relY = pos.Y - itemTopInList.Value.Y;
            bool dropBelow = relY >= itemH / 2;

            // Calculate indicator Y position (relative to the Panel which matches QueueList size)
            double lineY = dropBelow ? itemTopInList.Value.Y + itemH : itemTopInList.Value.Y;

            // Calculate the actual Reorder newIndex accounting for remove-then-insert semantics
            if (dropBelow)
                _dropInsertIndex = srcIndex < tgtIndex ? tgtIndex : tgtIndex + 1;
            else
                _dropInsertIndex = srcIndex < tgtIndex ? tgtIndex - 1 : tgtIndex;

            // Clamp to valid range
            _dropInsertIndex = Math.Clamp(_dropInsertIndex, 0, vm.Items.Count - 1);

            // Don't show indicator if dropping in same position
            if (_dropInsertIndex == srcIndex)
            {
                HideDropIndicator();
                return;
            }

            DropIndicator.Margin = new Thickness(0, lineY, 0, 0);
            DropIndicator.IsVisible = true;
        }
        else
        {
            // Past the last item — drop at end
            _dropInsertIndex = vm.Items.Count - 1;
            HideDropIndicator();
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        HideDropIndicator();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        HideDragVisuals();
        if (!e.DataTransfer.Contains(DragFormat)) return;
        if (DataContext is not QueuePanelViewModel vm) return;
        if (!int.TryParse(e.DataTransfer.TryGetValue(DragFormat), out var oldIndex)) return;
        if (_dropInsertIndex < 0) return;

        vm.MoveItem(oldIndex, _dropInsertIndex);
        _dropInsertIndex = -1;
    }

    private void HideDragVisuals()
    {
        DragGhost.IsVisible = false;
        HideDropIndicator();
    }

    private void HideDropIndicator()
    {
        DropIndicator.IsVisible = false;
    }

    private int FindItemIndex(QueueItemViewModel item)
    {
        if (DataContext is not QueuePanelViewModel vm) return -1;
        return vm.Items.IndexOf(item);
    }
}
