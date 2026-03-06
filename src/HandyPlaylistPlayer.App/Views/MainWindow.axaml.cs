using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HandyPlaylistPlayer.App.ViewModels;
using HandyPlaylistPlayer.Core;
using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Features.Settings.GetSetting;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.App.Views;

public partial class MainWindow : Window
{
    private KeybindingMap _keybindings = new();

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _ = LoadKeybindingsAsync();
    }

    private async Task LoadKeybindingsAsync()
    {
        try
        {
            var dispatcher = App.Services.GetService<IDispatcher>();
            if (dispatcher == null) return;
            var json = await dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.Keybindings));
            if (!string.IsNullOrEmpty(json))
                _keybindings = KeybindingMap.FromJson(json);
        }
        catch { /* use defaults */ }
    }

    public void ReloadKeybindings(KeybindingMap map) => _keybindings = map;

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel mainVm) return;
        // Don't intercept keys when focus is in input controls or interactive widgets.
        // Space/Enter in a ListBox should select items, not toggle play/pause.
        if (e.Source is TextBox or Slider or ListBoxItem or ComboBox or ComboBoxItem) return;

        var player = mainVm.Player;
        var action = _keybindings.GetAction(e.Key.ToString());
        if (action == null) return;

        try
        {
            switch (action)
            {
                case nameof(KeybindingMap.PlayPause):
                    await player.PlayPauseCommand.ExecuteAsync(null);
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.Escape):
                    if (mainVm.IsFullscreen)
                        SetFullscreen(mainVm, false);
                    else
                        await player.EmergencyStopActionCommand.ExecuteAsync(null);
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.Fullscreen):
                    SetFullscreen(mainVm, !mainVm.IsFullscreen);
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.SeekForward):
                    await player.HandleSeekForward();
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.SeekBackward):
                    await player.HandleSeekBackward();
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.NextTrack):
                    player.NextTrackCommand.Execute(null);
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.PrevTrack):
                    player.PreviousTrackCommand.Execute(null);
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.NudgePlus):
                    player.HandleNudgeOffsetPlus();
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.NudgeMinus):
                    player.HandleNudgeOffsetMinus();
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.ToggleOverrides):
                    player.ToggleOverridePanel();
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.VolumeUp):
                    player.HandleVolumeUp();
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.VolumeDown):
                    player.HandleVolumeDown();
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.ToggleMute):
                    player.HandleToggleMute();
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.ShowHelp):
                    mainVm.ToggleShortcutHelpCommand.Execute(null);
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.SetLoopA):
                    player.SetLoopACommand.Execute(null);
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.SetLoopB):
                    player.SetLoopBCommand.Execute(null);
                    e.Handled = true;
                    break;
                case nameof(KeybindingMap.ClearLoop):
                    player.ClearLoopCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetService<ILogger<MainWindow>>();
            logger?.LogWarning(ex, "Keyboard shortcut error for key {Key}", e.Key);
        }
    }

    private void OnShortcutOverlayClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ShowShortcutHelp = false;
    }

    public void SetFullscreen(MainViewModel vm, bool fullscreen)
    {
        if (vm.IsFullscreen == fullscreen) return;

        if (fullscreen)
        {
            WindowState = WindowState.FullScreen;
            vm.ToggleFullscreenCommand.Execute(null);
        }
        else
        {
            vm.ToggleFullscreenCommand.Execute(null);
            WindowState = WindowState.Normal;
        }
    }
}
