using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandyPlaylistPlayer.Core;
using HandyPlaylistPlayer.Core.Dispatching;
using IDispatcher = HandyPlaylistPlayer.Core.Dispatching.IDispatcher;
using HandyPlaylistPlayer.Core.Features.Device.ConnectDevice;
using HandyPlaylistPlayer.Core.Features.Device.DisconnectDevice;
using HandyPlaylistPlayer.Core.Features.Settings.GetSetting;
using HandyPlaylistPlayer.Core.Features.Playback.EmergencyStop;
using HandyPlaylistPlayer.Core.Features.Playback.LoadMedia;
using HandyPlaylistPlayer.Core.Features.Playback.PlayPause;
using HandyPlaylistPlayer.Core.Features.Playback.Seek;
using HandyPlaylistPlayer.Core.Features.Playback.Stop;
using HandyPlaylistPlayer.Core.Features.Library.ToggleWatched;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Runtime;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Devices.HandyApi;
using HandyPlaylistPlayer.Media.Mpv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.App.ViewModels;

public partial class PlayerViewModel : ObservableObject
{
    private readonly IDispatcher _dispatcher;
    private readonly IPlaybackCoordinator _coordinator;
    private readonly IQueueService _queue;
    private readonly HandyApiClient _handyClient;
    private readonly MpvMediaPlayerAdapter _mpvAdapter;
    private readonly IMediaFileRepository _mediaFileRepo;
    private readonly IPlaybackHistoryRepository _historyRepo;
    private readonly ILogger<PlayerViewModel> _logger;
    private volatile bool _isSeeking;
    private volatile int _seekStepMs = 15000;
    private long _lastSavedPositionMs;
    private volatile int _currentMediaFileId;
    private volatile int _currentScriptFileId;
    private DateTime _playbackStartedAt;
    private CancellationTokenSource? _adaptiveOffsetCts;
    private CancellationTokenSource? _seekDelayCts;
    private long _lastAppliedRtt;
    private volatile bool _nextPrepared;    // gapless: prevent duplicate pre-parse
    private volatile bool _watchedMarked;   // watched-threshold: prevent double-marking per video
    private int _loopSeekFlag;              // 0 = idle, 1 = seeking (atomic via Interlocked)
    private volatile bool _saveLastPosition; // cached from DB on each file load
    private double _watchedThreshold = 0.9;    // fraction (0.0-1.0), written from UI/load thread
    private volatile int _gaplessPrepareMs = 30000;     // ms before end to pre-parse next

    [ObservableProperty] private string _statusText = "No media loaded";
    [ObservableProperty] private string _deviceStatus = "Disconnected";
    [ObservableProperty] private string _timeDisplay = "00:00 / 00:00";
    [ObservableProperty] private string _seekPreviewText = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _seekPosition;
    [ObservableProperty] private double _seekMaximum = 100;
    [ObservableProperty] private double _volume = 0.8;
    [ObservableProperty] private string _currentVideoPath = string.Empty;
    [ObservableProperty] private string _currentScriptPath = string.Empty;
    [ObservableProperty] private bool _isDeviceConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _connectionHealthText = "";
    [ObservableProperty] private string _connectionQualityColor = "Gray";
    [ObservableProperty] private FunscriptDocument? _currentScript;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _isVideoVisible = true;
    [ObservableProperty] private bool _isFullscreen;

    // A-B Loop
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLooping))]
    [NotifyPropertyChangedFor(nameof(LoopStatusText))]
    private long? _loopStartMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLooping))]
    [NotifyPropertyChangedFor(nameof(LoopStatusText))]
    private long? _loopEndMs;

    public bool IsLooping => LoopStartMs.HasValue && LoopEndMs.HasValue;
    public string LoopStatusText
    {
        get
        {
            var s = LoopStartMs;
            var e = LoopEndMs;
            if (s.HasValue && e.HasValue)
                return $"Loop: {FormatTimeShort(s.Value)} - {FormatTimeShort(e.Value)}";
            if (s.HasValue)
                return $"Loop A: {FormatTimeShort(s.Value)}";
            return "";
        }
    }

    // Playback speed
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedDisplay))]
    [NotifyPropertyChangedFor(nameof(CanDecreaseSpeed))]
    [NotifyPropertyChangedFor(nameof(CanIncreaseSpeed))]
    private double _playbackSpeed = 1.0;

    private static readonly double[] SpeedPresets = [0.50, 0.75, 1.00, 1.25, 1.50, 2.00];

    public string SpeedDisplay => PlaybackSpeed == 1.0 ? "1×" : $"{PlaybackSpeed:F2}×";
    public bool CanDecreaseSpeed => PlaybackSpeed > SpeedPresets[0] + 0.01;
    public bool CanIncreaseSpeed => PlaybackSpeed < SpeedPresets[^1] - 0.01;

    partial void OnPlaybackSpeedChanged(double value)
    {
        _mpvAdapter.SetLiveProperty("speed", value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        // Sync playback rate to device (HSSP uses server-side rate control)
        _ = _handyClient.SetPlaybackRateAsync(value);
    }

    [RelayCommand(CanExecute = nameof(CanIncreaseSpeed))]
    private void IncreaseSpeed()
    {
        var idx = Array.FindIndex(SpeedPresets, s => s >= PlaybackSpeed + 0.01);
        PlaybackSpeed = SpeedPresets[idx < 0 ? SpeedPresets.Length - 1 : Math.Min(idx, SpeedPresets.Length - 1)];
    }

    [RelayCommand(CanExecute = nameof(CanDecreaseSpeed))]
    private void DecreaseSpeed()
    {
        var idx = Array.FindLastIndex(SpeedPresets, s => s <= PlaybackSpeed - 0.01);
        PlaybackSpeed = SpeedPresets[Math.Max(idx, 0)];
    }

    // Player control visibility (loaded from settings at startup)
    [ObservableProperty] private bool _showNavButtons    = true;
    [ObservableProperty] private bool _showFullscreenBtn = true;
    [ObservableProperty] private bool _showAbLoopButtons = true;
    [ObservableProperty] private bool _showSpeedControls = true;
    [ObservableProperty] private bool _showQueueButton   = true;
    [ObservableProperty] private bool _showOverridesBtn  = true;
    [ObservableProperty] private bool _showEqButton      = true;
    [ObservableProperty] private bool _showTracksButton  = true;
    [ObservableProperty] private bool _showVolumeControl = true;

    // Video equalizer
    [ObservableProperty] private int  _brightness = 0;
    [ObservableProperty] private int  _contrast   = 0;
    [ObservableProperty] private int  _saturation = 0;
    [ObservableProperty] private int  _gamma      = 0;
    [ObservableProperty] private int  _hue        = 0;
    [ObservableProperty] private bool _showEqualizer;

    partial void OnBrightnessChanged(int value) { if (_mpvAdapter != null) _mpvAdapter.SetLiveProperty("brightness", value.ToString()); }
    partial void OnContrastChanged(int value)   { if (_mpvAdapter != null) _mpvAdapter.SetLiveProperty("contrast",   value.ToString()); }
    partial void OnSaturationChanged(int value) { if (_mpvAdapter != null) _mpvAdapter.SetLiveProperty("saturation", value.ToString()); }
    partial void OnGammaChanged(int value)      { if (_mpvAdapter != null) _mpvAdapter.SetLiveProperty("gamma",      value.ToString()); }
    partial void OnHueChanged(int value)        { if (_mpvAdapter != null) _mpvAdapter.SetLiveProperty("hue",        value.ToString()); }

    [RelayCommand]
    private void ResetEqualizer() { Brightness = Contrast = Saturation = Gamma = Hue = 0; }

    [RelayCommand]
    private void ToggleEqualizer() => ShowEqualizer = !ShowEqualizer;

    [RelayCommand]
    private void ToggleTracks() => ShowTracks = !ShowTracks;

    // Track selection
    [ObservableProperty] private bool      _showTracks;
    [ObservableProperty] private MpvTrack? _selectedAudioTrack;
    [ObservableProperty] private MpvTrack? _selectedSubtitleTrack;

    public ObservableCollection<MpvTrack> AudioTracks    { get; } = [];
    public ObservableCollection<MpvTrack> SubtitleTracks { get; } = [];

    public bool HasMultipleTracks => AudioTracks.Count > 1 || SubtitleTracks.Count > 1;

    partial void OnSelectedAudioTrackChanged(MpvTrack? value)
    {
        if (value != null && _mpvAdapter != null)
            _mpvAdapter.SelectAudioTrack(value.Id);
    }

    partial void OnSelectedSubtitleTrackChanged(MpvTrack? value)
    {
        if (_mpvAdapter == null) return;
        if (value == null || value.Id == -1) _mpvAdapter.DisableSubtitles();
        else _mpvAdapter.SelectSubtitleTrack(value.Id);
    }

    private void RefreshTracks()
    {
        if (_mpvAdapter == null) return;
        var all = _mpvAdapter.GetTracks();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AudioTracks.Clear();
            var noSub = new MpvTrack("sub", -1, "None", "", "", false);
            SubtitleTracks.Clear();
            SubtitleTracks.Add(noSub);
            foreach (var t in all)
            {
                if (t.Type == "audio") AudioTracks.Add(t);
                else if (t.Type == "sub") SubtitleTracks.Add(t);
            }
            SelectedAudioTrack    = AudioTracks.FirstOrDefault(t => t.IsSelected);
            SelectedSubtitleTrack = SubtitleTracks.FirstOrDefault(t => t.IsSelected) ?? noSub;
            OnPropertyChanged(nameof(HasMultipleTracks));
        });
    }

    // Script movement visualizer
    [ObservableProperty] private bool   _showScriptVisualizer = false;
    [ObservableProperty] private double _visualizerPosition   = 0.0; // 0.0 = bottom, 1.0 = top

    [RelayCommand]
    private void ToggleScriptVisualizer() => ShowScriptVisualizer = !ShowScriptVisualizer;

    public OverrideControlsViewModel OverrideControls { get; }
    public QueuePanelViewModel QueuePanel { get; }
    public MpvMediaPlayerAdapter? MpvAdapter => _mpvAdapter;

    public bool IsQueueVisible => QueuePanel.IsVisible;

    public PlayerViewModel(
        IDispatcher dispatcher,
        IPlaybackCoordinator coordinator,
        IQueueService queue,
        HandyApiClient handyClient,
        MpvMediaPlayerAdapter mpvAdapter,
        OverrideControlsViewModel overrideControls,
        QueuePanelViewModel queuePanel,
        IMediaFileRepository mediaFileRepo,
        IPlaybackHistoryRepository historyRepo,
        ILogger<PlayerViewModel> logger)
    {
        _dispatcher = dispatcher;
        _coordinator = coordinator;
        _queue = queue;
        _handyClient = handyClient;
        _mpvAdapter = mpvAdapter;
        OverrideControls = overrideControls;
        QueuePanel = queuePanel;
        QueuePanel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(QueuePanelViewModel.IsVisible)) OnPropertyChanged(nameof(IsQueueVisible)); };
        _mediaFileRepo = mediaFileRepo;
        _historyRepo = historyRepo;
        _logger = logger;

        // Load player UI visibility preferences
        SafeFireAndForget(LoadPlayerUiSettingsAsync());

        // Set up the coordinator with our MPV player
        _coordinator.SetMediaPlayer(_mpvAdapter);
        _coordinator.SetDeviceBackend(_handyClient);

        _coordinator.PositionChanged += (_, posMs) =>
        {
            // A-B Loop: seek back to A when position reaches B
            var loopStart = LoopStartMs;
            var loopEnd = LoopEndMs;
            if (loopStart.HasValue && loopEnd.HasValue && posMs >= loopEnd.Value)
            {
                if (Interlocked.CompareExchange(ref _loopSeekFlag, 1, 0) == 0)
                {
                    var targetMs = loopStart.Value;
                    Task.Run(async () =>
                    {
                        try { await _coordinator.SeekAsync(targetMs); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Loop seek failed"); }
                        finally { Interlocked.Exchange(ref _loopSeekFlag, 0); }
                    });
                }
                return;
            }
            Interlocked.Exchange(ref _loopSeekFlag, 0);

            // Pre-compute visualizer position on background thread (cheap), assign on UI thread
            var vizPos = (ShowScriptVisualizer && CurrentScript != null)
                ? InterpolateScriptPosition(posMs, CurrentScript)
                : (double?)null;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_isSeeking)
                {
                    SeekPosition = posMs;
                    SeekMaximum = Math.Max(_coordinator.DurationMs, 1);
                    TimeDisplay = FormatTime(posMs, _coordinator.DurationMs);
                }
                if (vizPos.HasValue)
                    VisualizerPosition = vizPos.Value;
            });

            // Save position every 5 seconds of progress (only when setting is on)
            if (_saveLastPosition && _currentMediaFileId > 0 && Math.Abs(posMs - _lastSavedPositionMs) >= 5000)
            {
                _lastSavedPositionMs = posMs;
                SafeFireAndForget(SavePositionAsync(_currentMediaFileId, posMs));
            }

            // Gapless: pre-parse next script when within configured window of end
            if (!_nextPrepared && _coordinator.DurationMs > 0
                && posMs >= _coordinator.DurationMs - _gaplessPrepareMs)
            {
                _nextPrepared = true;
                SafeFireAndForget(PrepareNextQueueItemAsync());
            }

            // Mark as watched after configured threshold of the video has been played
            if (!_watchedMarked && _currentMediaFileId > 0
                && _coordinator.DurationMs > 0
                && posMs >= _coordinator.DurationMs * _watchedThreshold)
            {
                _watchedMarked = true;
                var mediaId = _currentMediaFileId;
                SafeFireAndForget(_dispatcher.SendAsync(new ToggleWatchedCommand(mediaId, true)));
                Avalonia.Threading.Dispatcher.UIThread.Post(() => QueuePanel.MarkWatched(mediaId));
            }
        };

        _coordinator.PlaybackStateChanged += (_, state) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsPlaying = state == PlaybackState.Playing;
                StatusText = state switch
                {
                    PlaybackState.Playing => "",
                    PlaybackState.Paused => "",
                    PlaybackState.Stopped => "Stopped",
                    PlaybackState.Buffering => "Buffering...",
                    _ => "No media loaded"
                };
            });

            if ((state == PlaybackState.Paused || state == PlaybackState.Stopped)
                && _handyClient != null && _handyClient.ConnectionState == DeviceConnectionState.Connected)
            {
                _ = Task.Run(async () =>
                {
                    try { await _handyClient.SendPositionAsync(0, 500); }
                    catch { }
                });
            }
        };

        _coordinator.DeviceStateChanged += (_, state) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                IsDeviceConnected = state == DeviceConnectionState.Connected;
                IsConnecting = state == DeviceConnectionState.Connecting;
                var protocolName = _handyClient.Protocol == HandyProtocol.HDSP ? "HDSP" : "HSSP";
                DeviceStatus = state switch
                {
                    DeviceConnectionState.Connected => $"Connected ({protocolName})",
                    DeviceConnectionState.Connecting => "Connecting...",
                    DeviceConnectionState.Error => "Error",
                    _ => "Disconnected"
                };

                // Update connection health indicator
                UpdateConnectionHealth(state);

                // On connect: apply default offset, start adaptive offset loop
                if (state == DeviceConnectionState.Connected)
                {
                    await ApplyDefaultSettingsOnConnect();
                    StartAdaptiveOffset();
                }
                else if (state == DeviceConnectionState.Disconnected || state == DeviceConnectionState.Error)
                {
                    StopAdaptiveOffset();
                }
            });
        };

        // Auto-advance to next track when media ends
        _coordinator.MediaEnded += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Record history and mark as watched for the finished item
                if (_currentMediaFileId > 0)
                {
                    SafeFireAndForget(RecordPlaybackHistoryAsync(_currentMediaFileId));
                    // Clear saved position on natural completion so next play starts from the beginning
                    if (_saveLastPosition)
                        SafeFireAndForget(SavePositionAsync(_currentMediaFileId, null));
                    if (!_watchedMarked)
                    {
                        _watchedMarked = true;
                        SafeFireAndForget(_dispatcher.SendAsync(new ToggleWatchedCommand(_currentMediaFileId, true)));
                    }
                    QueuePanel.MarkWatched(_currentMediaFileId);
                }

                // Advance to next track in queue
                if (_queue.Items.Count > 0)
                    _queue.Next();
            });
        };

        // Auto-advance on queue change
        int skipCount = 0;
        _queue.CurrentItemChanged += async (_, item) =>
        {
            if (item != null)
            {
                try
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await LoadAndPlayItem(item);
                    });
                    skipCount = 0; // Reset on successful load
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load queue item: {Path}", item.Video.FullPath);

                    // Auto-skip to next track if file is missing, but limit to prevent infinite loop
                    if (!File.Exists(item.Video.FullPath) && skipCount < _queue.Items.Count)
                    {
                        skipCount++;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            StatusText = "File not found — skipping");
                        _queue.Next();
                    }
                    else
                    {
                        skipCount = 0;
                        ShowError(ex, "Playback failed");
                    }
                }
            }
            else
            {
                skipCount = 0;
            }
        };

        SafeFireAndForget(LoadSeekStepAsync());
    }

    private async Task LoadPlayerUiSettingsAsync()
    {
        try
        {
            async Task<bool> GetBool(string key, bool def = true)
            {
                var v = await _dispatcher.QueryAsync(new GetSettingQuery(key));
                return v == null ? def : v == "true";
            }
            ShowNavButtons    = await GetBool(SettingKeys.ShowNavButtons);
            ShowFullscreenBtn = await GetBool(SettingKeys.ShowFullscreenBtn);
            ShowAbLoopButtons = await GetBool(SettingKeys.ShowAbLoopButtons);
            ShowSpeedControls = await GetBool(SettingKeys.ShowSpeedControls);
            ShowQueueButton   = await GetBool(SettingKeys.ShowQueueButton);
            ShowOverridesBtn  = await GetBool(SettingKeys.ShowOverridesBtn);
            ShowEqButton      = await GetBool(SettingKeys.ShowEqButton);
            ShowTracksButton  = await GetBool(SettingKeys.ShowTracksButton);
            ShowVolumeControl = await GetBool(SettingKeys.ShowVolumeControl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load player UI settings");
        }
    }

    private async Task LoadSeekStepAsync()
    {
        try
        {
            var saved = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.SeekStepSeconds));
            if (int.TryParse(saved, out var seconds) && seconds > 0)
                _seekStepMs = seconds * 1000;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load seek step setting");
        }
    }

    /// <summary>Called by SettingsViewModel when the seek step changes at runtime.</summary>
    public void UpdateSeekStep(int seconds) => _seekStepMs = Math.Max(1, seconds) * 1000;
    public void UpdateWatchedThreshold(int percent) => _watchedThreshold = Math.Clamp(percent, 10, 100) / 100.0;
    public void UpdateGaplessPrepareSeconds(int seconds) => _gaplessPrepareMs = Math.Max(1, seconds) * 1000;
    public int AutoHideSeconds { get; set; } = 3;

    private void UpdateConnectionHealth(DeviceConnectionState state)
    {
        if (state == DeviceConnectionState.Connected)
        {
            var rtt = _handyClient.AvgRoundTripMs;
            var protocol = _handyClient.Protocol == HandyProtocol.HDSP ? "HDSP" : "HSSP";
            var rttText = rtt > 0 ? $"{rtt}ms" : "?";
            var quality = rtt switch { < 80 => "OK", < 200 => "Fair", _ => "High" };
            ConnectionHealthText = $"{protocol} | RTT: {rttText} ({quality})";
            ConnectionQualityColor = rtt switch { < 80 => "#4CAF50", < 200 => "#FF9800", _ => "#F44336" };
        }
        else if (state == DeviceConnectionState.Connecting)
        {
            ConnectionHealthText = "Connecting...";
            ConnectionQualityColor = "#FF9800";
        }
        else
        {
            ConnectionHealthText = "";
            ConnectionQualityColor = "Gray";
        }
    }

    private async Task ApplyDefaultSettingsOnConnect()
    {
        try
        {
            // Apply saved default offset, or auto-detect from network RTT if not set
            var savedOffset = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.DefaultOffsetMs));
            if (int.TryParse(savedOffset, out var offset) && offset != 0)
            {
                OverrideControls.OffsetMs = offset;
                _logger.LogInformation("Applied default sync offset: {Offset}ms", offset);
            }
            else
            {
                // No manual offset set — auto-detect from measured network round-trip time.
                // One-way latency ≈ RTT/2. Use negative offset so device leads by that amount,
                // compensating for the delay between sending a command and the device acting on it.
                var rtt = _handyClient.AvgRoundTripMs;
                if (rtt > 0)
                {
                    Interlocked.Exchange(ref _lastAppliedRtt, rtt);
                    var autoOffset = -(int)(rtt / 2);
                    OverrideControls.OffsetMs = autoOffset;
                    _logger.LogInformation("Auto-detected sync offset from network RTT: {Rtt}ms → offset {Offset}ms", rtt, autoOffset);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        DeviceStatus = $"Connected (auto-offset: {autoOffset}ms)");
                }
            }

            // Log device slide range — do NOT apply to OverrideControls because
            // the device already applies its own slide range. Setting it here would
            // cause double-application: transform pipeline compresses → device compresses again.
            var slide = _handyClient.DeviceSlide;
            if (slide != null)
            {
                _logger.LogInformation("Device slide range: {Min}-{Max} (applied by device, not transform pipeline)", slide.Min, slide.Max);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply default settings on connect");
        }
    }

    private void StartAdaptiveOffset()
    {
        StopAdaptiveOffset();
        _adaptiveOffsetCts = new CancellationTokenSource();
        _ = AdaptiveOffsetLoop(_adaptiveOffsetCts.Token);
    }

    private void StopAdaptiveOffset()
    {
        _adaptiveOffsetCts?.Cancel();
        _adaptiveOffsetCts?.Dispose();
        _adaptiveOffsetCts = null;
    }

    private async Task AdaptiveOffsetLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(30_000, ct);
                var newRtt = await _handyClient.ProbeRttAsync(ct);
                if (newRtt <= 0) continue;

                // Only adjust if RTT changed significantly (>20ms from last applied)
                var drift = Math.Abs(newRtt - Interlocked.Read(ref _lastAppliedRtt));
                if (drift > 20)
                {
                    Interlocked.Exchange(ref _lastAppliedRtt, newRtt);
                    var newOffset = -(int)(newRtt / 2);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        OverrideControls.OffsetMs = newOffset;
                        _logger.LogInformation("Adaptive offset adjusted: RTT {Rtt}ms → offset {Offset}ms (drift {Drift}ms)",
                            newRtt, newOffset, drift);
                    });
                }

                // Update health display with fresh RTT
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    UpdateConnectionHealth(DeviceConnectionState.Connected));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adaptive offset loop error");
        }
    }

    [RelayCommand]
    private async Task PlayPause()
    {
        try
        {
            var isCurrentlyPlaying = _coordinator.State == PlaybackState.Playing;
            var expectedPositionMs = (long)SeekPosition; // Capture before play overwrites it

            await _dispatcher.SendAsync(new PlayPauseCommand(isCurrentlyPlaying));

            // Sync button state with actual coordinator state after command completes,
            // in case the async Post() from PlaybackStateChanged hasn't arrived yet
            IsPlaying = _coordinator.State == PlaybackState.Playing;

            // After resuming, verify VLC is at the expected position.
            // VLC can restart from 0 when resuming after PreviewFirstFrame or certain pause states.
            // SeekPosition gets overwritten by PositionChanged during the delay, so use the saved value.
            if (!isCurrentlyPlaying && expectedPositionMs > 1000)
            {
                await Task.Delay(200);
                var actualPos = _coordinator.CurrentPositionMs;
                if (Math.Abs(actualPos - expectedPositionMs) > 2000)
                {
                    _logger.LogInformation("Position drift: VLC at {Actual}ms, expected {Expected}ms — re-seeking",
                        actualPos, expectedPositionMs);
                    await _dispatcher.SendAsync(new SeekCommand(expectedPositionMs));
                }
            }
        }
        catch (Exception ex)
        {
            ShowError(ex, "Play/Pause failed");
        }
    }

    [RelayCommand]
    private async Task Stop()
    {
        try
        {
            await _dispatcher.SendAsync(new StopPlaybackCommand());
        }
        catch (Exception ex)
        {
            ShowError(ex, "Stop failed");
        }
    }

    [RelayCommand]
    private async Task EmergencyStopAction()
    {
        try
        {
            await _dispatcher.SendAsync(new EmergencyStopCommand());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Emergency stop error");
        }
    }

    [RelayCommand]
    private async Task DeviceToBottom()
    {
        try
        {
            await _handyClient.SendPositionAsync(0, 500);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device to bottom failed");
        }
    }

    [RelayCommand]
    private async Task MoveToBottomAndPause()
    {
        await _coordinator.PauseAsync();
        if (_handyClient != null && _handyClient.ConnectionState == DeviceConnectionState.Connected)
        {
            try { await _handyClient.SendPositionAsync(0, 500); }
            catch { }
        }
    }

    [RelayCommand]
    private void NextTrack()
    {
        _queue.Next();
    }

    [RelayCommand]
    private void PreviousTrack()
    {
        _queue.Previous();
    }

    [RelayCommand]
    private async Task OpenVideo(Window window)
    {
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Video File",
                FileTypeFilter =
                [
                    new FilePickerFileType("Video Files") { Patterns = ["*.mp4", "*.mkv", "*.webm", "*.avi", "*.wmv", "*.mov"] },
                    new FilePickerFileType("All Files") { Patterns = ["*"] }
                ],
                AllowMultiple = false
            });

            if (files.Count > 0)
            {
                var videoPath = files[0].Path.LocalPath;
                ErrorText = string.Empty;

                // Try to auto-find a paired .funscript
                var dir = Path.GetDirectoryName(videoPath);
                string? scriptPath = null;
                if (dir != null)
                {
                    var baseName = Path.GetFileNameWithoutExtension(videoPath);
                    var paired = Path.Combine(dir, baseName + ".funscript");
                    if (File.Exists(paired))
                        scriptPath = paired;
                }

                // Clear queue and enqueue as a single item so LoadAndPlayItem handles
                // track discovery, position save, etc. the same way as library playback.
                var videoItem = new MediaItem
                {
                    FullPath  = videoPath,
                    Filename  = Path.GetFileName(videoPath),
                    Extension = Path.GetExtension(videoPath),
                };
                MediaItem? scriptItem = scriptPath == null ? null : new MediaItem
                {
                    FullPath  = scriptPath,
                    Filename  = Path.GetFileName(scriptPath),
                    Extension = ".funscript",
                    IsScript  = true,
                };
                _queue.Clear();
                _queue.Enqueue([new QueueItem { Video = videoItem, Script = scriptItem }]);
                _queue.JumpTo(0);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex, "Failed to open video");
        }
    }

    [RelayCommand]
    private async Task OpenScript(Window window)
    {
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Funscript File",
                FileTypeFilter =
                [
                    new FilePickerFileType("Funscript Files") { Patterns = ["*.funscript"] },
                    new FilePickerFileType("All Files") { Patterns = ["*"] }
                ],
                AllowMultiple = false
            });

            if (files.Count > 0)
            {
                CurrentScriptPath = files[0].Path.LocalPath;
                if (!string.IsNullOrEmpty(CurrentVideoPath))
                {
                    await _dispatcher.SendAsync(new LoadMediaCommand(CurrentVideoPath, CurrentScriptPath));
                    CurrentScript = _coordinator.CurrentScript;
                }
            }
        }
        catch (Exception ex)
        {
            ShowError(ex, "Failed to open script");
        }
    }

    [RelayCommand]
    private async Task ConnectDevice()
    {
        try
        {
            // Load connection key from saved settings
            var savedKey = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.ConnectionKey));
            if (string.IsNullOrWhiteSpace(savedKey))
            {
                DeviceStatus = "Set connection key in Settings";
                return;
            }

            // Load and apply protocol setting
            var savedProtocol = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.HandyProtocol));
            if (savedProtocol == ProtocolNames.HSSP)
                _handyClient.SetProtocol(HandyProtocol.HSSP);
            else
                _handyClient.SetProtocol(HandyProtocol.HDSP);

            _handyClient.SetConnectionKey(savedKey.Trim());
            await _dispatcher.SendAsync(new ConnectDeviceCommand());
        }
        catch (Exception ex)
        {
            ShowError(ex, "Connection failed");
        }
    }

    [RelayCommand]
    private async Task DisconnectDevice()
    {
        try
        {
            await _dispatcher.SendAsync(new DisconnectDeviceCommand());
        }
        catch (Exception ex)
        {
            ShowError(ex, "Disconnect failed");
        }
    }

    [RelayCommand]
    private void DismissError()
    {
        ErrorText = string.Empty;
    }

    public void OnSeekStarted()
    {
        _isSeeking = true;
    }

    public void OnSeekPreview(double value)
    {
        SeekPreviewText = FormatTimeShort((long)value);
    }

    public async Task OnSeekCompleted(double value)
    {
        SeekPreviewText = "";
        try
        {
            await _dispatcher.SendAsync(new SeekCommand((long)value));
        }
        catch (Exception ex)
        {
            ShowError(ex, "Seek failed");
        }

        // Set position explicitly after seek — stale TimeChanged events from
        // ResumeSeekPause's brief resume may have overwritten SeekPosition.
        SeekPosition = value;
        // Keep _isSeeking true briefly to absorb remaining queued Posts.
        // Cancel previous delay if a new seek arrives before it completes.
        _seekDelayCts?.Cancel();
        _seekDelayCts?.Dispose();
        var cts = _seekDelayCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(150, cts.Token);
            _isSeeking = false;
        }
        catch (OperationCanceledException) { }
    }

    partial void OnVolumeChanged(double value)
    {
        if (_mpvAdapter != null)
            _mpvAdapter.Volume = value;
    }

    private async Task PrepareNextQueueItemAsync()
    {
        try
        {
            var nextIndex = _queue.CurrentIndex + 1;
            if (nextIndex < 0 || nextIndex >= _queue.Items.Count) return;
            var nextItem = _queue.Items[nextIndex];
            var scriptPath = nextItem.Script?.FullPath;
            await _coordinator.PrepareNextAsync(scriptPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-parse next queue item failed — not critical");
        }
    }

    private async Task LoadAndPlayItem(QueueItem item)
    {
        // Clear A-B loop, speed, equalizer, and per-video flags when loading a new track
        LoopStartMs = null;
        LoopEndMs = null;
        PlaybackSpeed = 1.0;
        Brightness = Contrast = Saturation = Gamma = Hue = 0;
        _nextPrepared = false;
        _watchedMarked = false;

        // Read settings once per file load and cache them
        _saveLastPosition = (await _dispatcher.QueryAsync(
            new GetSettingQuery(SettingKeys.SaveLastPosition))) == "true";
        var wtStr = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.WatchedThresholdPercent));
        if (int.TryParse(wtStr, out var wtVal) && wtVal >= 10 && wtVal <= 100) _watchedThreshold = wtVal / 100.0;
        var gpStr = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.GaplessPrepareSeconds));
        if (int.TryParse(gpStr, out var gpVal) && gpVal > 0) _gaplessPrepareMs = gpVal * 1000;

        // Save position of the previous item before loading new one (mid-way skip)
        if (_saveLastPosition && _currentMediaFileId > 0)
        {
            SafeFireAndForget(SavePositionAsync(_currentMediaFileId, (long)SeekPosition));
        }
        if (_currentMediaFileId > 0)
            SafeFireAndForget(RecordPlaybackHistoryAsync(_currentMediaFileId));

        _currentMediaFileId = item.Video.Id;
        _currentScriptFileId = item.Script?.Id ?? 0;
        _lastSavedPositionMs = 0;
        _playbackStartedAt = DateTime.UtcNow;

        // Update override controls with current pairing for per-video offset saving
        OverrideControls.CurrentVideoId = item.Video.Id;
        OverrideControls.CurrentScriptId = item.Script?.Id ?? 0;

        CurrentVideoPath = item.Video.FullPath;
        CurrentScriptPath = item.Script?.FullPath ?? string.Empty;
        ErrorText = string.Empty;
        await _dispatcher.SendAsync(new LoadMediaCommand(
            CurrentVideoPath, string.IsNullOrEmpty(CurrentScriptPath) ? null : CurrentScriptPath));

        // Update heatmap with loaded script (direct assignment — already on UI thread)
        CurrentScript = _coordinator.CurrentScript;
        if (CurrentScript == null)
            _logger.LogWarning("No funscript loaded for {Path} — heatmap will be empty", CurrentScriptPath);
        else
            _logger.LogInformation("Heatmap loaded with {Count} actions", CurrentScript.Actions.Count);

        // Refresh audio/subtitle track list for the newly loaded file.
        // Called immediately (tracks should be available after PLAYBACK_RESTART) and
        // again after a short delay as a safety net for slow demuxers.
        RefreshTracks();
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RefreshTracks);
        });

        // Resume from saved position if available (skip if near start or end)
        var resumePos = item.Video.LastPositionMs;
        var hasResumePos = resumePos is > 3000 && item.Video.DurationMs.HasValue
            && resumePos.Value < item.Video.DurationMs.Value - 10000;

        // LoadAsync always ends paused at the first frame.
        // Seek to resume position if setting is on and position is available (while still paused), then start playing.
        if (_saveLastPosition && hasResumePos)
            await _dispatcher.SendAsync(new SeekCommand(resumePos!.Value));

        await _dispatcher.SendAsync(new PlayPauseCommand(false));
    }

    private async Task RecordPlaybackHistoryAsync(int mediaFileId)
    {
        try
        {
            var durationMs = (long)(DateTime.UtcNow - _playbackStartedAt).TotalMilliseconds;
            if (durationMs > 5000) // Only record if played more than 5 seconds
                await _historyRepo.RecordAsync(mediaFileId, durationMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record playback history");
        }
    }

    private async Task SavePositionAsync(int mediaFileId, long? positionMs)
    {
        try
        {
            await _mediaFileRepo.SaveLastPositionAsync(mediaFileId, positionMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save playback position");
        }
    }

    private void ShowError(Exception ex, string context)
    {
        _logger.LogError(ex, context);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ErrorText = $"{context}: {ex.Message}";
            StatusText = context;
        });
    }

    [RelayCommand]
    private Task SeekForward() => HandleSeekForward();

    [RelayCommand]
    private Task SeekBackward() => HandleSeekBackward();

    // Keyboard shortcut handlers
    public async Task HandleSeekForward()
    {
        _isSeeking = true;
        var duration = _coordinator.DurationMs;
        var target = Math.Min(_coordinator.CurrentPositionMs + _seekStepMs,
            Math.Max(0, duration - Math.Min(500, duration)));
        SeekPosition = target;
        TimeDisplay = FormatTime(target, _coordinator.DurationMs);
        await _dispatcher.SendAsync(new SeekCommand(target));
        // Keep _isSeeking true briefly — VLC fires stale TimeChanged events
        // on a background thread after the seek completes
        _ = ClearSeekingAfterDelay();
    }

    public async Task HandleSeekBackward()
    {
        _isSeeking = true;
        var target = Math.Max(0, _coordinator.CurrentPositionMs - _seekStepMs);
        SeekPosition = target;
        TimeDisplay = FormatTime(target, _coordinator.DurationMs);
        await _dispatcher.SendAsync(new SeekCommand(target));
        _ = ClearSeekingAfterDelay();
    }

    private async Task ClearSeekingAfterDelay()
    {
        await Task.Delay(500);
        _isSeeking = false;
    }
    public void HandleNudgeOffsetPlus() => OverrideControls.NudgeOffsetCommand.Execute("10");
    public void HandleNudgeOffsetMinus() => OverrideControls.NudgeOffsetCommand.Execute("-10");

    // A-B Loop
    [RelayCommand]
    private void SetLoopA()
    {
        LoopStartMs = (long)SeekPosition;
        // If B is set and A is now after B, clear B
        if (LoopEndMs.HasValue && LoopStartMs >= LoopEndMs)
            LoopEndMs = null;
        _logger.LogInformation("Loop A set at {Ms}ms", LoopStartMs);
    }

    [RelayCommand]
    private void SetLoopB()
    {
        if (!LoopStartMs.HasValue)
        {
            // Set A first if not set
            LoopStartMs = 0;
        }
        LoopEndMs = (long)SeekPosition;
        // If B is before A, swap
        if (LoopEndMs <= LoopStartMs)
        {
            (LoopStartMs, LoopEndMs) = (LoopEndMs, LoopStartMs);
        }
        _logger.LogInformation("Loop B set at {Ms}ms, looping {Start}-{End}", LoopEndMs, LoopStartMs, LoopEndMs);
    }

    [RelayCommand]
    private void ClearLoop()
    {
        LoopStartMs = null;
        LoopEndMs = null;
        _logger.LogInformation("Loop cleared");
    }

    public void ToggleOverridePanel() => OverrideControls.TogglePanelCommand.Execute(null);

    private double _preMuteVolume;

    public void HandleVolumeUp() => Volume = Math.Min(1.0, Volume + 0.05);
    public void HandleVolumeDown() => Volume = Math.Max(0.0, Volume - 0.05);
    public void HandleToggleMute()
    {
        if (Volume > 0)
        {
            _preMuteVolume = Volume;
            Volume = 0;
        }
        else
        {
            Volume = _preMuteVolume > 0 ? _preMuteVolume : 0.5;
        }
    }

    private static string FormatTime(long posMs, long durMs)
    {
        var pos = TimeSpan.FromMilliseconds(Math.Max(0, posMs));
        var dur = TimeSpan.FromMilliseconds(Math.Max(0, durMs));
        if (dur.TotalHours >= 1)
            return $"{(int)pos.TotalHours}:{pos.Minutes:D2}:{pos.Seconds:D2} / {(int)dur.TotalHours}:{dur.Minutes:D2}:{dur.Seconds:D2}";
        return $"{pos.Minutes:D2}:{pos.Seconds:D2} / {dur.Minutes:D2}:{dur.Seconds:D2}";
    }

    private static string FormatTimeShort(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>Fire-and-forget with exception logging — prevents unobserved task exceptions.</summary>
    private void SafeFireAndForget(Task task, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        task.ContinueWith(t =>
            _logger.LogWarning(t.Exception, "Fire-and-forget failed in {Caller}", caller),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private static double InterpolateScriptPosition(long posMs, FunscriptDocument script)
        => HandyPlaylistPlayer.Core.Services.FunscriptInterpolator.GetNormalizedPosition(posMs, script.Actions);
}
