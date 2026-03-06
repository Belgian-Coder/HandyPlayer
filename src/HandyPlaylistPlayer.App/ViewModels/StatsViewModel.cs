using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.App.ViewModels;

public partial class StatsViewModel : ObservableObject
{
    private readonly IMediaFileRepository _mediaFileRepo;
    private readonly IPairingRepository _pairingRepo;
    private readonly IPlaybackHistoryRepository _historyRepo;
    private readonly IPlaylistRepository _playlistRepo;
    private readonly ILogger<StatsViewModel> _logger;

    [ObservableProperty] private int _totalVideos;
    [ObservableProperty] private int _totalScripts;
    [ObservableProperty] private int _pairedCount;
    [ObservableProperty] private int _watchedCount;
    [ObservableProperty] private int _playlistCount;
    [ObservableProperty] private int _totalPlays;
    [ObservableProperty] private string _totalWatchTime = "0:00";
    [ObservableProperty] private string _totalLibrarySize = "0 MB";
    [ObservableProperty] private string _totalLibraryDuration = "0:00";

    public ObservableCollection<PlaybackHistoryEntry> RecentHistory { get; } = [];

    public StatsViewModel(
        IMediaFileRepository mediaFileRepo,
        IPairingRepository pairingRepo,
        IPlaybackHistoryRepository historyRepo,
        IPlaylistRepository playlistRepo,
        ILogger<StatsViewModel> logger)
    {
        _mediaFileRepo = mediaFileRepo;
        _pairingRepo = pairingRepo;
        _historyRepo = historyRepo;
        _playlistRepo = playlistRepo;
        _logger = logger;
    }

    [RelayCommand]
    public async Task Refresh()
    {
        try
        {
            var videos = await _mediaFileRepo.GetAllVideosAsync();
            var scripts = await _mediaFileRepo.GetAllScriptsAsync();
            var pairedIds = await _pairingRepo.GetPairedVideoIdsAsync();
            var playlists = await _playlistRepo.GetAllAsync();
            var totalPlays = await _historyRepo.GetTotalPlayCountAsync();
            var totalWatchMs = await _historyRepo.GetTotalWatchTimeAsync();
            var recent = await _historyRepo.GetRecentAsync(50);

            var watched = videos.Count(v => v.WatchedAt != null);
            var totalSize = videos.Sum(v => v.FileSize ?? 0) + scripts.Sum(s => s.FileSize ?? 0);
            var totalDurMs = videos.Sum(v => v.DurationMs ?? 0);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                TotalVideos = videos.Count;
                TotalScripts = scripts.Count;
                PairedCount = pairedIds.Count;
                WatchedCount = watched;
                PlaylistCount = playlists.Count;
                TotalPlays = totalPlays;
                TotalWatchTime = FormatDuration(totalWatchMs);
                TotalLibrarySize = FormatSize(totalSize);
                TotalLibraryDuration = FormatDuration(totalDurMs);

                RecentHistory.Clear();
                foreach (var entry in recent) RecentHistory.Add(entry);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load statistics");
        }
    }

    private static string FormatDuration(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
        return $"{ts.Minutes}m {ts.Seconds:D2}s";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30)
            return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20)
            return $"{bytes / (double)(1L << 20):F1} MB";
        return $"{bytes / 1024.0:F0} KB";
    }

    public static FuncValueConverter<long, string> MsDurationConverter { get; } = new(ms =>
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    });
}
