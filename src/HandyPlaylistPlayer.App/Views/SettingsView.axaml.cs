using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HandyPlaylistPlayer.App.ViewModels;

namespace HandyPlaylistPlayer.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.GetWindow = () => this.FindAncestorOfType<Window>();
    }

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Scroll each newly selected tab's ScrollViewer to the top
        if (sender is TabControl tc && tc.SelectedItem is TabItem tab)
        {
            var sv = tab.FindDescendantOfType<ScrollViewer>();
            sv?.ScrollToHome();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || !vm.IsCapturingKey) return;

        if (e.Key == Key.Escape)
        {
            vm.CancelKeyCaptureCommand.Execute(null);
        }
        else
        {
            vm.ApplyCapturedKey(e.Key.ToString());
        }
        e.Handled = true;
    }
}
