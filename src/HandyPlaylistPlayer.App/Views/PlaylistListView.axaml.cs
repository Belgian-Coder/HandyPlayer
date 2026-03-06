using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using HandyPlaylistPlayer.App.ViewModels;
using HandyPlaylistPlayer.Core;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.App.Views;

public partial class PlaylistListView : UserControl
{
    private const string DragFormat = "PlaylistItemIndex";
    private const double DragThresholdPixels = 8;
    private Point _dragStartPoint;
    private bool _isDragging;
    private int _dropInsertIndex = -1;

    public PlaylistListView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Drag-drop setup for playlist items reorder
        PlaylistItemsList.AddHandler(PointerPressedEvent, OnItemPointerPressed, RoutingStrategies.Tunnel);
        PlaylistItemsList.AddHandler(PointerMovedEvent, OnItemPointerMoved, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(PlaylistItemsList, true);
        PlaylistItemsList.AddHandler(DragDrop.DragOverEvent, OnItemDragOver);
        PlaylistItemsList.AddHandler(DragDrop.DropEvent, OnItemDrop);
        PlaylistItemsList.AddHandler(DragDrop.DragLeaveEvent, OnItemDragLeave);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not PlaylistListViewModel vm) return;

        vm.SaveFileAction ??= async suggestedName =>
        {
            var window = this.FindAncestorOfType<Window>();
            if (window == null) return null;
            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Playlist",
                SuggestedFileName = suggestedName,
                FileTypeChoices =
                [
                    new FilePickerFileType("M3U Playlist") { Patterns = ["*.m3u"] }
                ]
            });
            return file?.Path.LocalPath;
        };

        vm.OpenFileAction ??= async () =>
        {
            var window = this.FindAncestorOfType<Window>();
            if (window == null) return null;
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Playlist",
                FileTypeFilter =
                [
                    new FilePickerFileType("M3U Playlist") { Patterns = ["*.m3u"] },
                    new FilePickerFileType("All Files") { Patterns = ["*"] }
                ],
                AllowMultiple = false
            });
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        };

        vm.PromptAction ??= async (prompt, defaultValue) =>
        {
            var window = this.FindAncestorOfType<Window>();
            if (window == null) return null;
            var dialog = new InputDialog(prompt, defaultValue);
            return await dialog.ShowDialog<string?>(window);
        };
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        PlaylistItemsList.SelectAll();
    }

    private async void OnDeleteSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PlaylistListViewModel vm) return;
        var selected = PlaylistItemsList.SelectedItems?
            .Cast<MediaItem>()
            .ToList();
        if (selected?.Count > 0)
            await vm.DeleteSelectedItemsCommand.ExecuteAsync(selected);
        PlaylistItemsList.SelectedItems?.Clear();
    }

    private async void OnCreateFolderPlaylistClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select folder for playlist",
                AllowMultiple = false
            });

            if (folders.Count > 0 && DataContext is PlaylistListViewModel vm)
            {
                var path = folders[0].TryGetLocalPath();
                if (!string.IsNullOrEmpty(path))
                    await vm.CreateFolderPlaylistCommand.ExecuteAsync(path);
            }
        }
        catch { /* folder picker cancelled or failed */ }
    }

    // --- Drag-to-reorder for playlist items (static playlists only) ---

    private bool IsStaticPlaylist =>
        DataContext is PlaylistListViewModel vm &&
        vm.SelectedPlaylist?.Type == PlaylistTypes.Static;

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStartPoint = e.GetPosition(PlaylistItemsList);
        _isDragging = false;
    }

    private async void OnItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging || !IsStaticPlaylist) return;
        if (!e.GetCurrentPoint(PlaylistItemsList).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(PlaylistItemsList);
        var diff = pos - _dragStartPoint;
        if (Math.Abs(diff.Y) < DragThresholdPixels) return;

        var listBoxItem = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (listBoxItem?.DataContext is not MediaItem item) return;

        if (DataContext is not PlaylistListViewModel vm) return;
        var index = vm.FilteredPlaylistItems.IndexOf(item);
        if (index < 0) return;

        _isDragging = true;
        DragGhostText.Text = item.Filename;
        DragGhost.IsVisible = true;

        try
        {
            var data = new DataObject();
            data.Set(DragFormat, index);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        catch { /* drag cancelled */ }
        finally
        {
            _isDragging = false;
            HideDragVisuals();
        }
    }

    private void OnItemDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DragFormat))
        {
            e.DragEffects = DragDropEffects.None;
            HideDragVisuals();
            return;
        }
        e.DragEffects = DragDropEffects.Move;

        if (e.Data.Get(DragFormat) is not int srcIndex) return;
        if (DataContext is not PlaylistListViewModel vm) return;

        var pos = e.GetPosition(PlaylistItemsList);
        DragGhost.Margin = new Thickness(Math.Max(0, pos.X + 16), Math.Max(0, pos.Y - 12), 0, 0);

        var hovered = PlaylistItemsList.GetVisualAt(pos)?.FindAncestorOfType<ListBoxItem>();
        if (hovered?.DataContext is MediaItem targetItem)
        {
            var tgtIndex = vm.FilteredPlaylistItems.IndexOf(targetItem);
            if (tgtIndex < 0) { HideDropIndicator(); return; }

            var itemTopInList = hovered.TranslatePoint(new Point(0, 0), PlaylistItemsList);
            if (itemTopInList == null) { HideDropIndicator(); return; }

            var itemH = hovered.Bounds.Height;
            var relY = pos.Y - itemTopInList.Value.Y;
            bool dropBelow = relY >= itemH / 2;

            double lineY = dropBelow ? itemTopInList.Value.Y + itemH : itemTopInList.Value.Y;

            if (dropBelow)
                _dropInsertIndex = srcIndex < tgtIndex ? tgtIndex : tgtIndex + 1;
            else
                _dropInsertIndex = srcIndex < tgtIndex ? tgtIndex - 1 : tgtIndex;

            _dropInsertIndex = Math.Clamp(_dropInsertIndex, 0, vm.FilteredPlaylistItems.Count - 1);

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
            _dropInsertIndex = vm.FilteredPlaylistItems.Count - 1;
            HideDropIndicator();
        }
    }

    private void OnItemDragLeave(object? sender, DragEventArgs e) => HideDropIndicator();

    private void OnItemDrop(object? sender, DragEventArgs e)
    {
        HideDragVisuals();
        if (!e.Data.Contains(DragFormat)) return;
        if (DataContext is not PlaylistListViewModel vm) return;
        if (e.Data.Get(DragFormat) is not int oldIndex) return;
        if (_dropInsertIndex < 0) return;

        vm.MoveItem(oldIndex, _dropInsertIndex);
        _dropInsertIndex = -1;
    }

    private void HideDragVisuals()
    {
        DragGhost.IsVisible = false;
        HideDropIndicator();
    }

    private void HideDropIndicator() => DropIndicator.IsVisible = false;
}
