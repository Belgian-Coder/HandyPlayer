using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Features.PatternMode.GeneratePattern;
using HandyPlaylistPlayer.Core.Features.Playback.Stop;
using HandyPlaylistPlayer.Core.Services;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.App.ViewModels;

public partial class PatternModeViewModel : ObservableObject
{
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<PatternModeViewModel> _logger;

    [ObservableProperty] private string _statusText = "Pattern Mode";
    [ObservableProperty] private PatternType _selectedPattern = PatternType.Sine;
    [ObservableProperty] private double _frequency = 1.0;
    [ObservableProperty] private int _amplitudeMin;
    [ObservableProperty] private int _amplitudeMax = 100;
    [ObservableProperty] private int _durationSeconds = 60;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _seedText = string.Empty;

    public PatternType[] AvailablePatterns { get; } = Enum.GetValues<PatternType>();

    public PatternModeViewModel(
        IDispatcher dispatcher,
        ILogger<PatternModeViewModel> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    [RelayCommand]
    private async Task PlayPattern()
    {
        try
        {
            int? seed = int.TryParse(SeedText, out var s) ? s : null;
            await _dispatcher.SendAsync(new GeneratePatternCommand(
                SelectedPattern, Frequency, AmplitudeMin, AmplitudeMax, DurationSeconds * 1000, seed));
            IsPlaying = true;
            StatusText = $"Playing {SelectedPattern} at {Frequency:F1} Hz";
        }
        catch (ValidationException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start pattern");
            StatusText = "Error starting pattern";
        }
    }

    [RelayCommand]
    private async Task StopPattern()
    {
        await _dispatcher.SendAsync(new StopPlaybackCommand());
        IsPlaying = false;
        StatusText = "Pattern stopped";
    }
}
