using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Core.Runtime;

public class PlaybackCoordinator : IPlaybackCoordinator, IDisposable
{
    private readonly IFunscriptParser _parser;
    private readonly ILogger<PlaybackCoordinator> _logger;
    private readonly object _engineLock = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile bool _isSeeking;
    private IMediaPlayer? _mediaPlayer;
    private IDeviceBackend? _deviceBackend;
    private FunscriptDocument? _currentScript;
    private readonly ScriptTransformPipeline _pipeline = new();
    private StreamingTickEngine? _tickEngine;

    // Gapless: pre-parsed next script (kept separate from active playback state)
    private string? _preparedScriptPath;
    private FunscriptDocument? _preparedScript;

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public long CurrentPositionMs => _mediaPlayer?.PositionMs ?? 0;
    public long DurationMs => _mediaPlayer?.DurationMs ?? 0;
    public DeviceConnectionState DeviceState => _deviceBackend?.ConnectionState ?? DeviceConnectionState.Disconnected;
    public bool IsStreamingMode => _deviceBackend?.IsStreamingMode ?? false;
    public FunscriptDocument? CurrentScript => _currentScript;

    public TransformSettings TransformSettings
    {
        get => _pipeline.Settings;
        set
        {
            _pipeline.Settings = value;
            var engine = _tickEngine;
            if (engine != null)
            {
                engine.UpdateOffset(value.OffsetMs);
            }
        }
    }

    public event EventHandler<PlaybackState>? PlaybackStateChanged;
    public event EventHandler<long>? PositionChanged;
    public event EventHandler<DeviceConnectionState>? DeviceStateChanged;
    public event EventHandler? MediaEnded;

    public PlaybackCoordinator(IFunscriptParser parser, ILogger<PlaybackCoordinator> logger)
    {
        _parser = parser;
        _logger = logger;
    }

    public void SetMediaPlayer(IMediaPlayer mediaPlayer)
    {
        ArgumentNullException.ThrowIfNull(mediaPlayer);

        if (_mediaPlayer != null)
        {
            _mediaPlayer.StateChanged -= OnMediaStateChanged;
            _mediaPlayer.PositionChanged -= OnMediaPositionChanged;
            _mediaPlayer.MediaEnded -= OnMediaEnded;
        }

        _mediaPlayer = mediaPlayer;
        _mediaPlayer.StateChanged += OnMediaStateChanged;
        _mediaPlayer.PositionChanged += OnMediaPositionChanged;
        _mediaPlayer.MediaEnded += OnMediaEnded;
    }

    public void SetDeviceBackend(IDeviceBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);

        if (_deviceBackend != null)
            _deviceBackend.ConnectionStateChanged -= OnDeviceStateChanged;

        _deviceBackend = backend;
        _deviceBackend.ConnectionStateChanged += OnDeviceStateChanged;
    }

    public async Task LoadAsync(string videoPath, string? scriptPath, CancellationToken ct = default)
    {
        if (_mediaPlayer == null) throw new InvalidOperationException("Media player not set");

        _logger.LogInformation("LoadAsync: video={Video}, script={Script}", videoPath, scriptPath ?? "(none)");

        await _loadLock.WaitAsync(ct);
        try
        {
            await StopAsync();
            _pipeline.Reset();

            await _mediaPlayer.LoadAsync(videoPath);

            if (scriptPath != null)
            {
                try
                {
                    // Use pre-parsed script if it matches (gapless transition)
                    if (_preparedScriptPath == scriptPath && _preparedScript != null)
                    {
                        _currentScript = _preparedScript;
                        _logger.LogInformation("Using pre-parsed script ({Count} actions) for gapless transition",
                            _currentScript.Actions.Count);
                    }
                    else
                    {
                        _currentScript = await _parser.ParseFileAsync(scriptPath, ct);
                        _logger.LogInformation("Loaded script with {Count} actions, deviceConnected={Conn}",
                            _currentScript.Actions.Count, _deviceBackend?.ConnectionState);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse funscript — video will still play without device control");
                    _currentScript = null;
                }

                // Clear prepared state regardless (used or not)
                _preparedScriptPath = null;
                _preparedScript = null;

                if (_currentScript != null && _deviceBackend?.ConnectionState == DeviceConnectionState.Connected)
                {
                    try
                    {
                        await _deviceBackend.SetupScriptAsync(_currentScript, ct);
                        _logger.LogInformation("Script setup on device completed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to setup script on device — video will still play");
                    }
                }
            }
            else
            {
                _currentScript = null;
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task PrepareNextAsync(string? scriptPath, CancellationToken ct = default)
    {
        if (scriptPath == null) return;

        try
        {
            // Parse outside the lock (IO-bound work)
            _logger.LogInformation("Pre-parsing next script: {Path}", scriptPath);
            var script = await _parser.ParseFileAsync(scriptPath, ct);
            _logger.LogInformation("Pre-parsed next script: {Count} actions ready", script.Actions.Count);

            // Atomically set both fields under the load lock to prevent races with LoadAsync
            await _loadLock.WaitAsync(ct);
            try
            {
                _preparedScriptPath = scriptPath;
                _preparedScript = script;
            }
            finally
            {
                _loadLock.Release();
            }

            // For HSSP: pre-upload to hosting service (doesn't touch device, just caches the URL)
            if (!IsStreamingMode && _deviceBackend?.ConnectionState == DeviceConnectionState.Connected)
            {
                try
                {
                    await _deviceBackend.PreUploadScriptAsync(script, ct);
                    _logger.LogInformation("Pre-uploaded next script to hosting service");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Pre-upload failed — will upload on load instead");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-parse failed — will parse on load instead");
            _preparedScriptPath = null;
            _preparedScript = null;
        }
    }

    public async Task LoadPatternAsync(FunscriptDocument pattern, CancellationToken ct = default)
    {
        await _loadLock.WaitAsync(ct);
        try
        {
            await StopAsync();
            _pipeline.Reset();
            _currentScript = pattern;
            _logger.LogInformation("Loaded pattern with {Count} actions", _currentScript.Actions.Count);

            if (_deviceBackend?.ConnectionState == DeviceConnectionState.Connected)
            {
                try
                {
                    await _deviceBackend.SetupScriptAsync(_currentScript, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to setup script on device for pattern playback");
                }
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task PlayAsync()
    {
        _logger.LogInformation("PlayAsync called: hasMediaPlayer={HasMP}, hasScript={HasScript}, deviceConnected={DevConn}, isStreaming={IsStream}",
            _mediaPlayer != null, _currentScript != null,
            _deviceBackend?.ConnectionState == DeviceConnectionState.Connected,
            IsStreamingMode);

        if (_mediaPlayer == null && !IsPatternOnlyMode()) return;

        if (_mediaPlayer != null)
            await _mediaPlayer.PlayAsync();

        if (_deviceBackend?.ConnectionState == DeviceConnectionState.Connected && _currentScript != null)
        {
            try
            {
                if (IsStreamingMode)
                {
                    _logger.LogInformation("Starting streaming tick engine (HDSP mode)");
                    StartStreamingEngine();
                }
                else
                {
                    var pos = Math.Max(0, (_mediaPlayer?.PositionMs ?? 0) + TransformSettings.OffsetMs);
                    _logger.LogInformation("Calling StartPlaybackAsync (HSSP mode) at {Position}ms (offset={Offset}ms)", pos, TransformSettings.OffsetMs);
                    await _deviceBackend.StartPlaybackAsync(pos);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start device playback");
            }
        }
        else
        {
            _logger.LogWarning("Device playback NOT started: connected={Conn}, hasScript={Script}",
                _deviceBackend?.ConnectionState, _currentScript != null);
        }

        SetState(PlaybackState.Playing);
    }

    public async Task PauseAsync()
    {
        _logger.LogInformation("PauseAsync called: isStreaming={IsStream}", IsStreamingMode);

        if (_mediaPlayer != null)
            await _mediaPlayer.PauseAsync();

        await StopStreamingEngineAsync();

        if (!IsStreamingMode && _deviceBackend?.ConnectionState == DeviceConnectionState.Connected)
        {
            try
            {
                await _deviceBackend.StopPlaybackAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop device playback");
            }
        }

        SetState(PlaybackState.Paused);
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("StopAsync called: isStreaming={IsStream}", IsStreamingMode);

        // Pause + seek to 0 instead of stop — keeps the first frame visible instead of going to black
        if (_mediaPlayer != null)
        {
            await _mediaPlayer.PauseAsync();
            await _mediaPlayer.SeekAsync(0);
        }

        await StopStreamingEngineAsync();

        if (_deviceBackend?.ConnectionState == DeviceConnectionState.Connected)
        {
            try
            {
                await _deviceBackend.StopPlaybackAsync();
                // Send device to bottom position on stop
                await _deviceBackend.SendPositionAsync(0, 500);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop device playback");
            }
        }

        _pipeline.Reset();
        SetState(PlaybackState.Stopped);
    }

    public async Task EmergencyStopAsync()
    {
        _logger.LogWarning("Emergency stop triggered on coordinator");

        if (_mediaPlayer != null)
            await _mediaPlayer.StopAsync();

        await StopStreamingEngineAsync();

        // Use device-level emergency stop (more aggressive than normal stop)
        if (_deviceBackend?.ConnectionState == DeviceConnectionState.Connected)
        {
            try
            {
                await _deviceBackend.EmergencyStopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to emergency stop device");
            }
        }

        _pipeline.Reset();
        SetState(PlaybackState.Stopped);
    }

    public async Task SeekAsync(long positionMs)
    {
        _logger.LogInformation("SeekAsync: position={Position}ms, isStreaming={IsStream}", positionMs, IsStreamingMode);

        // Suppress HSSP resync during the entire seek operation (media seek + device seek)
        _isSeeking = true;
        try
        {
            if (_mediaPlayer != null)
                await _mediaPlayer.SeekAsync(positionMs);

            // Only sync device seek when playback is active — seeking while paused/stopped
            // should only update the video frame, not start the device
            if (State == PlaybackState.Playing && !IsStreamingMode
                && _deviceBackend?.ConnectionState == DeviceConnectionState.Connected && _currentScript != null)
            {
                await _deviceBackend.SeekPlaybackAsync(Math.Max(0, positionMs + TransformSettings.OffsetMs));
            }
            // Streaming engine automatically picks up the new position on next tick
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seek device playback");
        }
        finally
        {
            _isSeeking = false;
        }
    }

    public async Task ConnectDeviceAsync(CancellationToken ct = default)
    {
        if (_deviceBackend == null) throw new InvalidOperationException("Device backend not set");

        _logger.LogInformation("ConnectDeviceAsync called: isStreaming={IsStream}, hasScript={HasScript}",
            IsStreamingMode, _currentScript != null);

        await _deviceBackend.ConnectAsync(ct);

        _logger.LogInformation("ConnectDeviceAsync: device state after connect = {State}", _deviceBackend.ConnectionState);

        // If a script is already loaded, set it up on the newly connected device
        if (_deviceBackend.ConnectionState == DeviceConnectionState.Connected && _currentScript != null)
        {
            try
            {
                await _deviceBackend.SetupScriptAsync(_currentScript, ct);
                _logger.LogInformation("Script auto-setup on device connect");

                // If playback is already active, start device playback at current position
                if (State == PlaybackState.Playing && _mediaPlayer != null)
                {
                    if (IsStreamingMode)
                    {
                        _logger.LogInformation("Starting streaming engine after connect (HDSP)");
                        StartStreamingEngine();
                    }
                    else
                    {
                        var pos = Math.Max(0, _mediaPlayer.PositionMs + TransformSettings.OffsetMs);
                        _logger.LogInformation("Starting playback after connect (HSSP) at {Position}ms (offset={Offset}ms)", pos, TransformSettings.OffsetMs);
                        await _deviceBackend.StartPlaybackAsync(pos, ct);
                    }
                    _logger.LogInformation("Device playback started at {Position}ms after connect", _mediaPlayer.PositionMs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-setup script on device connect");
            }
        }
    }

    public async Task DisconnectDeviceAsync()
    {
        await StopStreamingEngineAsync();
        if (_deviceBackend != null)
            await _deviceBackend.DisconnectAsync();
    }

    private void StartStreamingEngine()
    {
        lock (_engineLock)
        {
            if (_tickEngine != null)
            {
                _tickEngine.Stop();
                _tickEngine.Dispose();
                _tickEngine = null;
            }

            if (_mediaPlayer == null || _deviceBackend == null || _currentScript == null) return;

            _tickEngine = new StreamingTickEngine(
                _mediaPlayer, _deviceBackend, _currentScript, _pipeline, _logger, tickRateMs: 10);
            _tickEngine.UpdateOffset(TransformSettings.OffsetMs);
            _ = _tickEngine.StartAsync();
            _logger.LogInformation("Started streaming tick engine");
        }
    }

    private async Task StopStreamingEngineAsync()
    {
        StreamingTickEngine? engine;
        lock (_engineLock)
        {
            engine = _tickEngine;
            _tickEngine = null;
        }

        if (engine != null)
        {
            await engine.StopAsync();
            engine.Dispose();
        }
    }

    private bool IsPatternOnlyMode() => _currentScript != null && _mediaPlayer == null;

    private void SetState(PlaybackState state)
    {
        State = state;
        PlaybackStateChanged?.Invoke(this, state);
    }

    private void OnMediaStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        // Ignore transient state changes during seeks (e.g. stopped-state seek does Play→Time→Pause)
        if (_isSeeking) return;

        if (e.State == PlaybackState.Buffering)
            SetState(PlaybackState.Buffering);
        else if (e.State == PlaybackState.Playing && State == PlaybackState.Buffering)
            SetState(PlaybackState.Playing);
    }

    private void OnMediaPositionChanged(object? sender, PositionChangedEventArgs e)
    {
        PositionChanged?.Invoke(this, e.PositionMs);

        // HSSP: forward position for periodic resync / drift detection
        // Skip during seeks — SeekPlaybackAsync handles its own stop+play
        if (!_isSeeking && !IsStreamingMode && State == PlaybackState.Playing
            && _deviceBackend?.ConnectionState == DeviceConnectionState.Connected)
        {
            _ = _deviceBackend.ResyncAsync(Math.Max(0, e.PositionMs + TransformSettings.OffsetMs));
        }
    }

    private async void OnMediaEnded(object? sender, EventArgs e)
    {
        await StopStreamingEngineAsync();

        if (_deviceBackend?.ConnectionState == DeviceConnectionState.Connected)
        {
            try
            {
                // Stop device playback in HSSP mode
                if (!IsStreamingMode)
                    await _deviceBackend.StopPlaybackAsync();
                // Send device to bottom position
                await _deviceBackend.SendPositionAsync(0, 500);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop device on media end");
            }
        }

        SetState(PlaybackState.Stopped);
        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnDeviceStateChanged(object? sender, DeviceConnectionState state)
    {
        DeviceStateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.StateChanged -= OnMediaStateChanged;
            _mediaPlayer.PositionChanged -= OnMediaPositionChanged;
            _mediaPlayer.MediaEnded -= OnMediaEnded;
        }

        if (_deviceBackend != null)
            _deviceBackend.ConnectionStateChanged -= OnDeviceStateChanged;

        lock (_engineLock)
        {
            if (_tickEngine != null)
            {
                _tickEngine.Stop();
                _tickEngine.Dispose();
                _tickEngine = null;
            }
        }

        _loadLock.Dispose();
    }
}
