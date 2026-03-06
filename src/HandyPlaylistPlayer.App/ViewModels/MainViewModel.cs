using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandyPlaylistPlayer.Core;
using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Features.Library.FindScriptForVideo;
using HandyPlaylistPlayer.Core.Features.Library.ScanLibrary;
using HandyPlaylistPlayer.Core.Features.Settings.GetSetting;
using HandyPlaylistPlayer.Core.Features.Settings.SaveSetting;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Runtime;
using HandyPlaylistPlayer.Core.Services;
using Microsoft.Extensions.Logging;
using IDispatcher = HandyPlaylistPlayer.Core.Dispatching.IDispatcher;

namespace HandyPlaylistPlayer.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "HandyPlayer";

    [ObservableProperty]
    private int _selectedNavIndex;

    /// <summary>
    /// The overlay page shown on top of the player (null when on Player tab).
    /// </summary>
    [ObservableProperty]
    private ObservableObject? _overlayPage;

    /// <summary>
    /// Whether the overlay is visible (false when on Player tab).
    /// </summary>
    [ObservableProperty]
    private bool _isOverlayVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSidebar))]
    private bool _isFullscreen;

    [ObservableProperty]
    private bool _showShortcutHelp;

    [ObservableProperty]
    private bool _showFullscreenHint;

    public bool ShowSidebar => !IsFullscreen;

    private bool _hasShownFullscreenHint;

    private int _preFullscreenNavIndex;
    private readonly PlayerViewModel _playerViewModel;
    private readonly LibraryViewModel _libraryViewModel;
    private readonly PlaylistListViewModel _playlistListViewModel;
    private readonly PatternModeViewModel _patternModeViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly StatsViewModel _statsViewModel;
    private readonly IDispatcher _dispatcher;
    private readonly IMediaFileRepository _mediaFileRepo;
    private readonly IQueueService _queue;
    private readonly LibraryWatcher _libraryWatcher;
    private readonly ILogger<MainViewModel> _logger;

    public PlayerViewModel Player => _playerViewModel;

    public MainViewModel(
        PlayerViewModel playerViewModel,
        LibraryViewModel libraryViewModel,
        PlaylistListViewModel playlistListViewModel,
        PatternModeViewModel patternModeViewModel,
        SettingsViewModel settingsViewModel,
        StatsViewModel statsViewModel,
        IDispatcher dispatcher,
        IMediaFileRepository mediaFileRepo,
        IQueueService queue,
        LibraryWatcher libraryWatcher,
        ILogger<MainViewModel> logger)
    {
        _playerViewModel = playerViewModel;
        _libraryViewModel = libraryViewModel;
        _playlistListViewModel = playlistListViewModel;
        _patternModeViewModel = patternModeViewModel;
        _settingsViewModel = settingsViewModel;
        _statsViewModel = statsViewModel;
        _dispatcher = dispatcher;
        _mediaFileRepo = mediaFileRepo;
        _queue = queue;
        _libraryWatcher = libraryWatcher;
        _logger = logger;

        _playlistListViewModel.OnPlaybackStarted = () => SelectedNavIndex = 0;
        _libraryViewModel.OnScanCompleted = () => _playlistListViewModel.ReloadAsync();

        // Auto-refresh library when file watcher detects changes
        _libraryWatcher.LibraryChanged += async (_, _) =>
        {
            try
            {
                await _libraryViewModel.RefreshAsync();
                await _playlistListViewModel.ReloadAsync();
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Library watcher refresh failed"); }
        };

        // Fire-and-forget background library scan on startup
        _ = ScanLibraryOnStartupAsync();
        _ = RestoreQueueOnStartupAsync();
    }

    private async Task ScanLibraryOnStartupAsync()
    {
        try
        {
            // Wait for UI to fully render and become responsive before scanning
            await Task.Delay(3000);
            _logger.LogInformation("Starting background library scan on startup");
            await Task.Run(() => _dispatcher.SendAsync(new ScanLibraryCommand()));
            _logger.LogInformation("Background library scan completed");

            // Refresh library and playlist views with updated data
            await _libraryViewModel.RefreshAsync();
            await _playlistListViewModel.ReloadAsync();

            // Start file watchers after initial scan
            _ = _libraryWatcher.StartAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background library scan failed — not critical");
        }
    }

    private async Task RestoreQueueOnStartupAsync()
    {
        try
        {
            await Task.Delay(1000); // Let UI settle and settings load

            var restoreEnabled = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.RestoreQueueOnStartup));
            if (restoreEnabled != "true") return;

            var json = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.QueueState));
            if (string.IsNullOrEmpty(json)) return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var videoIds = root.GetProperty("VideoIds").EnumerateArray().Select(e => e.GetInt32()).ToList();
            var currentIndex = root.GetProperty("CurrentIndex").GetInt32();

            if (videoIds.Count == 0) return;

            var mediaItems = await Task.Run(() => _mediaFileRepo.GetByIdsAsync(videoIds));
            var mediaMap = mediaItems.ToDictionary(m => m.Id);

            var queueItems = new List<QueueItem>();
            foreach (var id in videoIds)
            {
                if (!mediaMap.TryGetValue(id, out var video)) continue;
                MediaItem? script = null;
                try { script = await _dispatcher.QueryAsync(new FindScriptForVideoQuery(id)); }
                catch { /* pairing may not exist */ }
                queueItems.Add(new QueueItem { Video = video, Script = script });
            }

            if (queueItems.Count > 0)
            {
                _queue.Enqueue(queueItems);
                _logger.LogInformation("Restored queue with {Count} items", queueItems.Count);

                // Jump to and play the item that was active when the app was last closed.
                // PlayerViewModel listens to CurrentItemChanged and will load + play it.
                var jumpIndex = Math.Clamp(currentIndex, 0, _queue.Items.Count - 1);
                _queue.JumpTo(jumpIndex);
            }

            // Clear saved state so it doesn't re-restore if the user closes without playing
            await _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.QueueState, ""));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore queue — not critical");
        }
    }

    partial void OnSelectedNavIndexChanged(int value)
    {
        // Prevent navigating away from player in fullscreen
        if (IsFullscreen && value != 0)
        {
            SelectedNavIndex = 0;
            return;
        }

        if (value == 0)
        {
            // Player tab — hide overlay, player is always visible underneath
            OverlayPage = null;
            IsOverlayVisible = false;
            _playerViewModel.IsVideoVisible = true;
        }
        else
        {
            // Non-player tabs — show as overlay on top of the player.
            // Hide the VideoView because NativeControlHost renders at the OS level
            // and ignores Avalonia's ZIndex, causing it to overlap overlay pages.
            _playerViewModel.IsVideoVisible = false;
            OverlayPage = value switch
            {
                1 => _libraryViewModel,
                2 => _playlistListViewModel,
                3 => _patternModeViewModel,
                4 => _settingsViewModel,
                5 => _statsViewModel,
                _ => null
            };
            // Auto-refresh when navigating to page
            if (value == 1) _ = _libraryViewModel.RefreshAsync();
            if (value == 2) _ = _playlistListViewModel.ReloadAsync();
            if (value == 5) _ = _statsViewModel.Refresh();
            IsOverlayVisible = OverlayPage != null;
        }
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        if (!IsFullscreen)
        {
            _preFullscreenNavIndex = SelectedNavIndex;
            SelectedNavIndex = 0;
            IsFullscreen = true;
            _playerViewModel.IsFullscreen = true;

            // Show first-use hint once
            if (!_hasShownFullscreenHint)
            {
                _hasShownFullscreenHint = true;
                ShowFullscreenHint = true;
                _ = DismissFullscreenHintAfterDelay();
            }
        }
        else
        {
            IsFullscreen = false;
            _playerViewModel.IsFullscreen = false;
            ShowFullscreenHint = false;
            SelectedNavIndex = _preFullscreenNavIndex;
        }
    }

    private async Task DismissFullscreenHintAfterDelay()
    {
        await Task.Delay(4000);
        ShowFullscreenHint = false;
    }

    [RelayCommand]
    private void ToggleShortcutHelp() => ShowShortcutHelp = !ShowShortcutHelp;

    [RelayCommand]
    private void NavigateToPlayer() => SelectedNavIndex = 0;

    [RelayCommand]
    private void NavigateToLibrary() => SelectedNavIndex = 1;

    [RelayCommand]
    private void NavigateToPlaylists() => SelectedNavIndex = 2;

    [RelayCommand]
    private void NavigateToPatterns() => SelectedNavIndex = 3;

    [RelayCommand]
    private void NavigateToSettings() => SelectedNavIndex = 4;
}
