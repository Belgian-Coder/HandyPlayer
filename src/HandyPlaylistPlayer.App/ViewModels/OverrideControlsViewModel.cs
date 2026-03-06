using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandyPlaylistPlayer.Core.Dispatching;
using IDispatcher = HandyPlaylistPlayer.Core.Dispatching.IDispatcher;
using HandyPlaylistPlayer.Core.Features.Playlists.GetAllPlaylists;
using HandyPlaylistPlayer.Core.Features.Presets.CreatePreset;
using HandyPlaylistPlayer.Core.Features.Presets.DeletePreset;
using HandyPlaylistPlayer.Core.Features.Presets.GetAllPresets;
using HandyPlaylistPlayer.Core.Features.Library.UpdatePairingOffset;
using HandyPlaylistPlayer.Core.Runtime;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.App.ViewModels;

public partial class OverrideControlsViewModel : ObservableObject
{
    private readonly IPlaybackCoordinator _coordinator;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<OverrideControlsViewModel> _logger;
    private bool _suppressApply;

    [ObservableProperty] private int _rangeMin;
    [ObservableProperty] private int _rangeMax = 100;
    [ObservableProperty] private int _offsetMs;
    [ObservableProperty] private double _speedLimit;
    [ObservableProperty] private double _intensity = 100; // percentage: 100 = normal, 50 = half, 150 = amplified
    [ObservableProperty] private bool _invert;
    [ObservableProperty] private double _edgeThreshold; // 0 = off, value = positions/sec threshold
    [ObservableProperty] private double _edgeReduction = 50; // percentage: 50 = half speed above threshold
    [ObservableProperty] private bool _isExpertMode;
    [ObservableProperty] private bool _isPanelVisible;
    [ObservableProperty] private Preset? _selectedPreset;
    [ObservableProperty] private Playlist? _selectedPlaylistForPreset;
    [ObservableProperty] private string _videoOffsetStatus = "";

    // Set by PlayerViewModel on each file load
    public int CurrentVideoId { get; set; }
    public int CurrentScriptId { get; set; }
    public bool HasPairing => CurrentVideoId > 0 && CurrentScriptId > 0;

    public ObservableCollection<Preset> Presets { get; } = [];
    public ObservableCollection<Playlist> AvailablePlaylists { get; } = [];

    public OverrideControlsViewModel(
        IPlaybackCoordinator coordinator,
        IDispatcher dispatcher,
        ILogger<OverrideControlsViewModel> logger)
    {
        _coordinator = coordinator;
        _dispatcher = dispatcher;
        _logger = logger;
        _ = LoadPresetsAsync();
        _ = LoadPlaylistsAsync();
    }

    private async Task LoadPresetsAsync()
    {
        try
        {
            var presets = await _dispatcher.QueryAsync(new GetAllPresetsQuery());
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Presets.Clear();
                foreach (var p in presets) Presets.Add(p);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load presets");
        }
    }

    partial void OnSelectedPresetChanged(Preset? value)
    {
        if (value == null) return;
        _suppressApply = true;
        RangeMin = value.RangeMin;
        RangeMax = value.RangeMax;
        OffsetMs = value.OffsetMs;
        SpeedLimit = value.SpeedLimit ?? 0;
        Intensity = (value.Intensity ?? 1.0) * 100;
        Invert = value.Invert;
        _suppressApply = false;
        ApplyToCoordinator();
    }

    partial void OnRangeMinChanged(int value) => ApplyToCoordinator();
    partial void OnRangeMaxChanged(int value) => ApplyToCoordinator();
    partial void OnOffsetMsChanged(int value) => ApplyToCoordinator();
    partial void OnSpeedLimitChanged(double value) => ApplyToCoordinator();
    partial void OnIntensityChanged(double value) => ApplyToCoordinator();
    partial void OnInvertChanged(bool value) => ApplyToCoordinator();
    partial void OnEdgeThresholdChanged(double value) => ApplyToCoordinator();
    partial void OnEdgeReductionChanged(double value) => ApplyToCoordinator();

    // Hot path — bypasses dispatcher for instant slider response
    private void ApplyToCoordinator()
    {
        if (_suppressApply) return;
        _coordinator.TransformSettings = new TransformSettings
        {
            RangeMin = RangeMin,
            RangeMax = RangeMax,
            OffsetMs = OffsetMs,
            SpeedLimit = SpeedLimit > 0 ? SpeedLimit : null,
            Intensity = Intensity != 100 ? Intensity / 100.0 : null,
            Invert = Invert,
            EdgeThreshold = EdgeThreshold > 0 ? EdgeThreshold : null,
            EdgeReduction = EdgeReduction / 100.0
        };
    }

    [RelayCommand]
    private void NudgeOffset(string amount)
    {
        if (int.TryParse(amount, out var delta))
            OffsetMs = Math.Clamp(OffsetMs + delta, -500, 500);
    }

    [RelayCommand]
    private void TogglePanel() => IsPanelVisible = !IsPanelVisible;

    [RelayCommand]
    private void ResetToDefaults()
    {
        _suppressApply = true;
        RangeMin = 0;
        RangeMax = 100;
        OffsetMs = 0;
        SpeedLimit = 0;
        Intensity = 100;
        Invert = false;
        EdgeThreshold = 0;
        EdgeReduction = 50;
        SelectedPreset = null;
        _suppressApply = false;
        ApplyToCoordinator();
    }

    private async Task LoadPlaylistsAsync()
    {
        try
        {
            var playlists = await _dispatcher.QueryAsync(new GetAllPlaylistsQuery());
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                AvailablePlaylists.Clear();
                foreach (var p in playlists) AvailablePlaylists.Add(p);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlists for preset association");
        }
    }

    [RelayCommand]
    private async Task SavePreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var preset = new Preset
            {
                Name = name,
                RangeMin = RangeMin,
                RangeMax = RangeMax,
                OffsetMs = OffsetMs,
                SpeedLimit = SpeedLimit > 0 ? SpeedLimit : null,
                Intensity = Intensity != 100 ? Intensity / 100.0 : null,
                Invert = Invert,
                IsExpert = IsExpertMode,
                PlaylistId = SelectedPlaylistForPreset?.Id
            };
            await _dispatcher.SendAsync(new CreatePresetCommand(preset));
            SelectedPlaylistForPreset = null;
            await LoadPresetsAsync();
        }
        catch (ValidationException ex)
        {
            _logger.LogWarning("Preset validation failed: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save preset");
        }
    }

    [RelayCommand]
    private async Task SaveOffsetToVideo()
    {
        if (!HasPairing) return;
        try
        {
            await _dispatcher.SendAsync(new UpdatePairingOffsetCommand(CurrentVideoId, CurrentScriptId, OffsetMs));
            VideoOffsetStatus = $"Saved {OffsetMs}ms to video";
            _ = ClearVideoOffsetStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save offset to video pairing");
            VideoOffsetStatus = "Save failed";
        }
    }

    private async Task ClearVideoOffsetStatusAsync()
    {
        await Task.Delay(3000);
        VideoOffsetStatus = "";
    }

    [RelayCommand]
    private async Task DeletePreset()
    {
        if (SelectedPreset == null) return;
        try
        {
            await _dispatcher.SendAsync(new DeletePresetCommand(SelectedPreset.Id));
            SelectedPreset = null;
            await LoadPresetsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete preset");
        }
    }
}
