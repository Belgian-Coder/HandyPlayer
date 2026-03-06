using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using HandyPlaylistPlayer.App.ViewModels;

namespace HandyPlaylistPlayer.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        LibraryList.DoubleTapped += OnListDoubleTapped;
        LibraryGrid.DoubleTapped += OnListDoubleTapped;
        DataContextChanged += OnDataContextChanged;

        // Bubble-routed event for all checkboxes in the list
        LibraryList.AddHandler(ToggleButton.IsCheckedChangedEvent, OnItemCheckChanged, RoutingStrategies.Bubble);

        if (LibraryList.ContextMenu is { } contextMenu)
            contextMenu.Opening += OnContextMenuOpening;

        if (LibraryGrid.ContextMenu is { } gridContextMenu)
            gridContextMenu.Opening += OnGridContextMenuOpening;
    }

    private void OnItemCheckChanged(object? sender, RoutedEventArgs e)
    {
        // Only process item checkboxes (inside ListBoxItems), not the header checkbox
        if (e.Source is Control source && source.FindAncestorOfType<ListBoxItem>() != null
            && DataContext is LibraryViewModel vm)
            vm.OnItemSelectionChanged();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
        {
            // Only assign delegates once — ViewModel is a singleton, so subsequent
            // DataContextChanged calls would accumulate closures capturing this view.
            vm.ConfirmAction ??= async message =>
            {
                var window = this.FindAncestorOfType<Window>();
                if (window == null) return false;
                var dialog = new ConfirmDialog(message);
                var result = await dialog.ShowDialog<bool?>(window);
                return result == true;
            };

            vm.PickScriptAction ??= async () =>
            {
                var window = this.FindAncestorOfType<Window>();
                if (window == null) return null;
                var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Funscript File",
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Funscript Files") { Patterns = ["*.funscript"] },
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

            vm.PickVideoFileAction ??= async () =>
            {
                var window = this.FindAncestorOfType<Window>();
                if (window == null) return null;
                var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Locate Video File",
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Video Files") { Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.wmv", "*.webm", "*.flv", "*.m4v"] },
                        new FilePickerFileType("All Files") { Patterns = ["*"] }
                    ],
                    AllowMultiple = false
                });
                return files.Count > 0 ? files[0].Path.LocalPath : null;
            };
        }
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm || vm.SelectedItem == null)
        {
            e.Cancel = true;
            return;
        }

        PopulatePlaylistSubmenu(AddToPlaylistMenu, vm);
    }

    private void OnGridContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm || vm.SelectedItem == null)
        {
            e.Cancel = true;
            return;
        }

        PopulatePlaylistSubmenu(AddToPlaylistMenuGrid, vm);
    }

    private static void PopulatePlaylistSubmenu(MenuItem menuItem, LibraryViewModel vm)
    {
        menuItem.Items.Clear();

        foreach (var playlist in vm.Playlists)
        {
            menuItem.Items.Add(new MenuItem
            {
                Header = playlist.Name,
                Command = vm.AddToPlaylistCommand,
                CommandParameter = playlist
            });
        }

        if (vm.Playlists.Count > 0)
            menuItem.Items.Add(new Separator());

        menuItem.Items.Add(new MenuItem
        {
            Header = "New Playlist...",
            Command = vm.CreatePlaylistAndAddCommand
        });
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
        {
            var selectedItem = (LibraryList.IsVisible ? LibraryList.SelectedItem : LibraryGrid.SelectedItem)
                               as LibraryItemViewModel;
            if (selectedItem != null && vm.PlayItemCommand.CanExecute(selectedItem))
                vm.PlayItemCommand.Execute(selectedItem);
        }
    }
}
