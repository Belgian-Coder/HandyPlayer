using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HandyPlaylistPlayer.Core;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Devices.HandyApi.Models;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Devices.HandyApi;

public enum HandyProtocol { HSSP, HDSP }

public class HandyApiClient : IDeviceBackend
{
    private readonly IScriptHostingService _hostingService;
    private readonly ServerTimeSyncService _timeSync;
    private readonly ILogger<HandyApiClient> _logger;
    private HttpClient? _httpClient;
    private string? _connectionKey;
    private HandyProtocol _protocol = HandyProtocol.HDSP;
    private double _playbackRate = 1.0;
    private volatile int _consecutiveFailures;
    private volatile bool _isReconnecting;
    private string? _lastHsspUrl;
    private string? _lastHsspSha256;
    private const int ReconnectThreshold = 3;
    private const int MaxReconnectAttempts = 5;

    public string Name => "Handy Wi-Fi API";
    public bool IsStreamingMode => _protocol == HandyProtocol.HDSP;
    public HandyProtocol Protocol => _protocol;
    public DeviceConnectionState ConnectionState { get; private set; } = DeviceConnectionState.Disconnected;
    public event EventHandler<DeviceConnectionState>? ConnectionStateChanged;

    // Device info populated on connect
    public HandyInfoResponse? DeviceInfo { get; private set; }
    public HandyStatusResponse? DeviceStatus { get; private set; }
    public HandySlideResponse? DeviceSlide { get; private set; }

    /// <summary>Average round-trip delay to the Handy server (measured during time sync).</summary>
    public long AvgRoundTripMs => _timeSync.AvgRoundTripMs;

    /// <summary>Quick RTT probe — updates AvgRoundTripMs with fresh measurement.</summary>
    public async Task<long> ProbeRttAsync(CancellationToken ct = default)
    {
        if (_httpClient == null) return 0;
        return await _timeSync.ProbeRttAsync(_httpClient, ct);
    }

    public HandyApiClient(
        IScriptHostingService hostingService,
        ServerTimeSyncService timeSync,
        ILogger<HandyApiClient> logger)
    {
        _hostingService = hostingService;
        _timeSync = timeSync;
        _logger = logger;
    }

    public void SetProtocol(HandyProtocol protocol)
    {
        _protocol = protocol;
        _logger.LogInformation("Handy protocol set to {Protocol}", protocol);
    }

    /// <summary>
    /// Sets the playback rate for HSSP synced playback.
    /// If currently playing in HSSP, re-sends the play command at the new rate.
    /// </summary>
    public async Task SetPlaybackRateAsync(double rate)
    {
        _playbackRate = rate;
        if (_protocol == HandyProtocol.HSSP && _hsspPlaying && _httpClient != null)
        {
            // Re-send play command at current estimated position with new rate
            var lastKnown = Interlocked.Read(ref _hsspLastKnownTimeMs);
            _logger.LogInformation("HSSP: updating playback rate to {Rate}x at position {Pos}ms", rate, lastKnown);
            try
            {
                await RetryAsync(async () =>
                    await _httpClient.PutAsJsonAsync("hssp/play",
                        new HandyHsspPlayRequest
                        {
                            EstimatedServerTime = _timeSync.GetEstimatedServerTime(),
                            StartTime = lastKnown,
                            Loop = false,
                            PlaybackRate = rate
                        }), default);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update HSSP playback rate");
            }
        }
    }

    public void SetConnectionKey(string key)
    {
        _connectionKey = key;
        _httpClient?.Dispose();
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5)
        };
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("https://www.handyfeeling.com/api/handy/v2/"),
            Timeout = TimeSpan.FromSeconds(10),
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        _httpClient.DefaultRequestHeaders.Add("X-Connection-Key", key);
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (_httpClient == null || string.IsNullOrEmpty(_connectionKey))
            throw new InvalidOperationException("Connection key not set");

        SetConnectionState(DeviceConnectionState.Connecting);

        try
        {
            var result = await RetryAsync(async () =>
                await _httpClient.GetFromJsonAsync<HandyConnectedResponse>("connected", ct), ct);

            if (result?.Connected != true)
            {
                SetConnectionState(DeviceConnectionState.Error);
                return false;
            }

            // Fetch device info and sync server time in parallel — these are independent
            var infoTask = FetchDeviceInfoAsync(ct);
            var syncTask = _timeSync.SyncAsync(_httpClient, rounds: 15, ct: ct);
            await Task.WhenAll(infoTask, syncTask);

            // Set device mode and slide range in parallel
            var mode = _protocol == HandyProtocol.HDSP ? HandyDeviceModes.HDSP : HandyDeviceModes.HSSP;
            var modeTask = RetryAsync(async () =>
                await _httpClient.PutAsJsonAsync("mode", new HandyModeRequest { Mode = mode }, ct), ct);
            // Reset slide range to full 0-100 so our position values map correctly.
            // Other apps may have narrowed the range; without this reset, HDSP positions
            // get physically remapped into a tiny band (e.g. 80-100), making it look like
            // the device is "stuck at the top".
            var slideTask = SetSlideRangeAsync(0, 100, ct);
            await Task.WhenAll(modeTask, slideTask);
            _logger.LogInformation("Device mode set to {Mode} ({Protocol})", mode, _protocol);

            if (_protocol == HandyProtocol.HDSP)
                ResetHdspTrace();

            _consecutiveFailures = 0;
            SetConnectionState(DeviceConnectionState.Connected);
            _logger.LogInformation("Connected to Handy device ({Protocol} mode)", _protocol);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Handy");
            SetConnectionState(DeviceConnectionState.Error);
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (ConnectionState == DeviceConnectionState.Connected)
                await StopPlaybackAsync();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Error stopping playback during disconnect"); }

        DeviceInfo = null;
        DeviceStatus = null;
        DeviceSlide = null;
        SetConnectionState(DeviceConnectionState.Disconnected);
    }

    public async Task SetupScriptAsync(FunscriptDocument script, CancellationToken ct = default)
    {
        if (_httpClient == null) throw new InvalidOperationException("Not connected");

        _logger.LogInformation("SetupScriptAsync called: protocol={Protocol}, actions={Count}",
            _protocol, script.Actions.Count);

        if (_protocol == HandyProtocol.HDSP)
        {
            // HDSP doesn't need script upload — streaming tick engine handles it
            _logger.LogInformation("HDSP mode: script setup skipped (tick engine will stream positions)");
            return;
        }

        // HSSP: upload script to hosting service
        var tempPath = Path.Combine(Path.GetTempPath(), $"handy_script_{Guid.NewGuid()}.csv");
        try
        {
            // CSV format matching official Handy SDK: header comment + at,pos per line
            var csv = "#Created by HandyPlaylistPlayer\n"
                + string.Join('\n', script.Actions.Select(a => $"{a.At},{a.Pos}"));
            await File.WriteAllTextAsync(tempPath, csv, ct);
            _logger.LogInformation("HSSP: wrote temp script ({Bytes} bytes, {Lines} actions) to {Path}",
                csv.Length, script.Actions.Count, tempPath);

            var (url, sha256) = await _hostingService.UploadScriptAsync(tempPath, ct);
            _lastHsspUrl = url;
            _lastHsspSha256 = sha256;
            _logger.LogInformation("HSSP: script uploaded to hosting: url={Url}, sha256={Sha256}", url, sha256);

            // Set mode to HSSP
            await RetryAsync(async () =>
                await _httpClient.PutAsJsonAsync("mode", new HandyModeRequest { Mode = HandyDeviceModes.HSSP }, ct), ct);
            _logger.LogInformation("HSSP: device mode set to {Mode}", HandyDeviceModes.HSSP);

            // Setup HSSP
            await RetryAsync(async () =>
                await _httpClient.PutAsJsonAsync("hssp/setup",
                    new HandyHsspSetupRequest { Url = url, Sha256 = sha256 }, ct), ct);

            _logger.LogInformation("HSSP script setup complete: url={Url}", url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HSSP script setup FAILED");
            throw;
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public async Task PreUploadScriptAsync(FunscriptDocument script, CancellationToken ct = default)
    {
        if (_protocol == HandyProtocol.HDSP) return; // HDSP doesn't need script upload

        // Pre-upload to hosting service only — doesn't configure device mode or HSSP setup.
        // This warms the hosting cache so SetupScriptAsync is faster on the next LoadAsync.
        var tempPath = Path.Combine(Path.GetTempPath(), $"handy_preload_{Guid.NewGuid()}.csv");
        try
        {
            var csv = "#Created by HandyPlaylistPlayer\n"
                + string.Join('\n', script.Actions.Select(a => $"{a.At},{a.Pos}"));
            await File.WriteAllTextAsync(tempPath, csv, ct);
            await _hostingService.UploadScriptAsync(tempPath, ct);
            _logger.LogInformation("Pre-uploaded script ({Count} actions) to hosting service", script.Actions.Count);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    // HSSP resync state (ScriptPlayer approach)
    // All 64-bit fields use Interlocked for atomic read/write across threads.
    private long _hsspLastKnownTimeMs;
    private long _hsspLastUpdateTicks;
    private long _hsspLastResyncTicks;
    private volatile bool _hsspPlaying;
    private volatile bool _hsspSeeking; // suppress resync during explicit seeks
    private const long HsspMaxDriftMs = 500;
    private const long HsspResyncIntervalMs = 10_000;

    public async Task StartPlaybackAsync(long startTimeMs, CancellationToken ct = default)
    {
        if (_httpClient == null) throw new InvalidOperationException("Not connected");

        _logger.LogInformation("StartPlaybackAsync called: protocol={Protocol}, startTime={StartTime}ms",
            _protocol, startTimeMs);

        if (_protocol == HandyProtocol.HDSP)
        {
            _logger.LogDebug("HDSP mode: StartPlaybackAsync is no-op (tick engine handles streaming)");
            return;
        }

        // HSSP: server time must be recomputed on each retry attempt to avoid stale timestamps
        var serverTime = _timeSync.GetEstimatedServerTime();
        _logger.LogInformation("HSSP play: serverTime={ServerTime}, startTime={StartTime}ms", serverTime, startTimeMs);

        await RetryAsync(async () =>
            await _httpClient.PutAsJsonAsync("hssp/play",
                new HandyHsspPlayRequest
                {
                    EstimatedServerTime = _timeSync.GetEstimatedServerTime(),
                    StartTime = startTimeMs,
                    Loop = false,
                    PlaybackRate = _playbackRate
                }, ct), ct);

        _hsspPlaying = true;
        Interlocked.Exchange(ref _hsspLastKnownTimeMs, startTimeMs);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _hsspLastUpdateTicks, now);
        Interlocked.Exchange(ref _hsspLastResyncTicks, now);
        _logger.LogInformation("HSSP playback started at {Position}ms", startTimeMs);
    }

    public async Task StopPlaybackAsync(CancellationToken ct = default)
    {
        if (_httpClient == null) return;

        _logger.LogInformation("StopPlaybackAsync called: protocol={Protocol}, wasPlaying={WasPlaying}",
            _protocol, _hsspPlaying);

        _hsspPlaying = false;

        if (_protocol == HandyProtocol.HDSP)
            return; // Tick engine simply stops sending positions

        try
        {
            using var response = await _httpClient.PutAsync("hssp/stop", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Error 100004 = device is in wrong mode (e.g. HDSP from bottom button) — harmless
                _logger.LogDebug("HSSP stop returned HTTP {Status} (ignored — device may be in different mode)",
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "HSSP stop failed (ignored)");
        }

        _logger.LogInformation("HSSP playback stopped");
    }

    public async Task SeekPlaybackAsync(long positionMs, CancellationToken ct = default)
    {
        _logger.LogInformation("SeekPlaybackAsync called: protocol={Protocol}, position={Position}ms",
            _protocol, positionMs);

        if (_protocol == HandyProtocol.HDSP)
            return; // Tick engine picks up new position automatically

        // Suppress resync during explicit seek to avoid double stop+play
        _hsspSeeking = true;
        try
        {
            // HSSP seek = stop + re-play at new position
            await StopPlaybackAsync(ct);
            await StartPlaybackAsync(positionMs, ct);
        }
        finally
        {
            _hsspSeeking = false;
        }
    }

    /// <summary>
    /// HSSP periodic resync — called on every media position update.
    /// Detects drift between estimated device time and actual media time.
    /// Hard resync (stop+play) if drift > 500ms, soft resync every 10s.
    /// Based on ScriptPlayer's Resync/ResyncNow approach.
    /// </summary>
    public async Task ResyncAsync(long mediaTimeMs, CancellationToken ct = default)
    {
        if (_protocol != HandyProtocol.HSSP || !_hsspPlaying || _hsspSeeking || _httpClient == null)
            return;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var freq = System.Diagnostics.Stopwatch.Frequency;

        // Estimate where the device thinks it is (atomic reads for 64-bit fields)
        var lastUpdate = Interlocked.Read(ref _hsspLastUpdateTicks);
        var lastKnown = Interlocked.Read(ref _hsspLastKnownTimeMs);
        var elapsedMs = (now - lastUpdate) * 1000 / freq;
        var estimatedDeviceTime = lastKnown + elapsedMs;
        var drift = Math.Abs(estimatedDeviceTime - mediaTimeMs);

        // Update tracking (atomic writes)
        Interlocked.Exchange(ref _hsspLastKnownTimeMs, mediaTimeMs);
        Interlocked.Exchange(ref _hsspLastUpdateTicks, now);

        var lastResync = Interlocked.Read(ref _hsspLastResyncTicks);
        var sinceLastResyncMs = (now - lastResync) * 1000 / freq;

        if (drift > HsspMaxDriftMs)
        {
            // Hard resync: stop + play at current position
            _logger.LogWarning("HSSP hard resync: drift={Drift}ms (>{Max}ms), restarting at {Time}ms",
                drift, HsspMaxDriftMs, mediaTimeMs);
            _hsspPlaying = false;
            try
            {
                using (await _httpClient.PutAsync("hssp/stop", null, ct)) { }
                var serverTime = _timeSync.GetEstimatedServerTime();
                _logger.LogInformation("HSSP hard resync play: serverTime={ServerTime}, startTime={StartTime}ms",
                    serverTime, mediaTimeMs);
                using (await _httpClient.PutAsJsonAsync("hssp/play",
                    new HandyHsspPlayRequest
                    {
                        EstimatedServerTime = serverTime,
                        StartTime = mediaTimeMs,
                        Loop = false,
                        PlaybackRate = 1.0
                    }, ct)) { }
                _hsspPlaying = true;
                _logger.LogInformation("HSSP hard resync completed at {Time}ms", mediaTimeMs);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "HSSP hard resync FAILED at {Time}ms", mediaTimeMs);
            }
            Interlocked.Exchange(ref _hsspLastResyncTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        }
        else if (sinceLastResyncMs > HsspResyncIntervalMs)
        {
            // Soft resync: adjust time without stopping
            var serverTime = _timeSync.GetEstimatedServerTime();
            _logger.LogInformation("HSSP soft resync at {Time}ms (drift={Drift}ms, serverTime={ServerTime})",
                mediaTimeMs, drift, serverTime);
            try
            {
                using (await _httpClient.PutAsJsonAsync("hssp/synctime",
                    new HandyHsspSyncTimeRequest
                    {
                        CurrentTime = mediaTimeMs,
                        ServerTime = serverTime,
                        Filter = 1.0
                    }, ct)) { }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "HSSP soft resync FAILED at {Time}ms", mediaTimeMs);
            }
            Interlocked.Exchange(ref _hsspLastResyncTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    private volatile int _hdspSendCount;
    private StreamWriter? _hdspTraceWriter;

    public void ResetHdspTrace()
    {
        Interlocked.Exchange(ref _hdspSendCount, 0);
        try { _hdspTraceWriter?.Dispose(); } catch { }
        _hdspTraceWriter = null;
    }

    public Task SendPositionAsync(int position, int durationMs, CancellationToken ct = default)
    {
        if (_httpClient == null) return Task.CompletedTask;

        if (_protocol == HandyProtocol.HDSP)
        {
            // No dedup here — tick engine deduplicates by action index.
            // Same-position actions are intentional (vibration patterns).
            FireAndForgetSend(position, durationMs, ct);
            return Task.CompletedTask;
        }

        // HSSP mode: switch to HDSP temporarily to send position, then switch back
        FireAndForgetSendWithModeSwitch(position, durationMs, ct);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends a direct position in HSSP mode by temporarily switching to HDSP mode 2,
    /// then switches back to HSSP so subsequent play commands work.
    /// Used for "go to bottom" on pause.
    /// </summary>
    private async void FireAndForgetSendWithModeSwitch(int position, int durationMs, CancellationToken ct)
    {
        try
        {
            if (_httpClient == null) return;
            var pos = Math.Clamp(position / 100.0, 0.0, 1.0);
            var duration = (int)Math.Floor(durationMs + 0.75);

            _logger.LogInformation("HSSP SendPosition: switching to HDSP mode for pos={Position}% dur={Duration}ms",
                position, durationMs);

            // Switch to HDSP mode and send position
            using (await _httpClient.PutAsJsonAsync("mode", new HandyModeRequest { Mode = HandyDeviceModes.HDSP }, ct)) { }

            var json = string.Create(CultureInfo.InvariantCulture,
                $"{{ \"immediateResponse\": true, \"stopOnTarget\": true, \"duration\": {duration}, \"position\": {pos:F4} }}");
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PutAsync("hdsp/xpt", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("HSSP SendPosition xpt failed: HTTP {Status} {Body}", (int)response.StatusCode, body);
            }
            else
            {
                _logger.LogInformation("HSSP SendPosition sent successfully");
            }

            // Wait for the HDSP movement to finish before switching back to HSSP.
            // Switching mode mid-movement cancels the physical motion on the device.
            await Task.Delay(durationMs + 200, CancellationToken.None);

            // Switch back to HSSP mode so the next StartPlaybackAsync (hssp/play) works.
            // Use CancellationToken.None — the caller's CT may already be cancelled (pause lifecycle)
            // and this restore MUST complete, otherwise the device stays in HDSP and play commands fail.
            if (_httpClient == null) return;
            using (await _httpClient.PutAsJsonAsync("mode", new HandyModeRequest { Mode = HandyDeviceModes.HSSP }, CancellationToken.None)) { }
            _logger.LogInformation("HSSP SendPosition: restored HSSP mode");

            // Re-send hssp/setup with cached script — mode switch may clear the association
            if (_lastHsspUrl != null && _lastHsspSha256 != null)
            {
                using (await _httpClient.PutAsJsonAsync("hssp/setup",
                    new HandyHsspSetupRequest { Url = _lastHsspUrl, Sha256 = _lastHsspSha256 }, CancellationToken.None)) { }
                _logger.LogInformation("HSSP SendPosition: re-sent hssp/setup after mode restore");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HSSP SendPosition failed");
        }
    }

    /// <summary>
    /// Fire-and-forget: sends position via hdsp/xpt and processes the response
    /// in the background so the tick loop is never blocked by network I/O.
    /// Handy 2 uses 0.0 (bottom) to 1.0 (top), so funscript 0-100 is divided by 100.
    /// </summary>
    private async void FireAndForgetSend(int position, int durationMs, CancellationToken ct)
    {
        try
        {
            var count = Interlocked.Increment(ref _hdspSendCount);
            var shouldLog = count <= 10 || count % 100 == 0;

            // Convert funscript 0-100 to device 0.0-1.0
            var pos = Math.Clamp(position / 100.0, 0.0, 1.0);
            var duration = (int)Math.Floor(durationMs + 0.75);

            var json = string.Create(CultureInfo.InvariantCulture,
                $"{{ \"immediateResponse\": true, \"stopOnTarget\": true, \"duration\": {duration}, \"position\": {pos:F4} }}");

            // Only measure timing when we'll actually log it
            var sw = shouldLog ? System.Diagnostics.Stopwatch.StartNew() : null;

            var client = _httpClient;
            if (client == null) return;

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PutAsync("hdsp/xpt", content, ct);

            // Only read response body when logging or on error
            if (!response.IsSuccessStatusCode || shouldLog)
            {
                var httpMs = sw?.ElapsedMilliseconds ?? 0;
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    _logger.LogWarning("HDSP xpt #{Count} HTTP {Status}: {Body} ({HttpMs}ms)",
                        count, (int)response.StatusCode, body, httpMs);
                else
                    _logger.LogInformation("HDSP xpt #{Count}: pos={Position}% ({DevPos:F2}) dur={Duration}ms ({HttpMs}ms)",
                        count, position, pos, duration, httpMs);
            }

            // Reset failure counter on success
            _consecutiveFailures = 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "HDSP send failed: pos={Position} dur={Duration}", position, durationMs);
            if (Interlocked.Increment(ref _consecutiveFailures) >= ReconnectThreshold
                && ConnectionState == DeviceConnectionState.Connected)
            {
                TryAutoReconnect();
            }
        }
        catch (OperationCanceledException) { /* normal cancellation */ }
    }

    public async Task EmergencyStopAsync()
    {
        if (_httpClient == null) return;

        try
        {
            using (await _httpClient.PutAsync("hamp/stop", null)) { }
            using (await _httpClient.PutAsync("hssp/stop", null)) { }
            _logger.LogWarning("Emergency stop sent to device");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Emergency stop failed");
        }
    }

    // --- Device info endpoints ---

    public async Task FetchDeviceInfoAsync(CancellationToken ct = default)
    {
        if (_httpClient == null) return;

        try
        {
            DeviceInfo = await _httpClient.GetFromJsonAsync<HandyInfoResponse>("info", ct);
            _logger.LogInformation("Device: {Model} FW {FwVersion} HW {HwVersion}",
                DeviceInfo?.Model, DeviceInfo?.FwVersion, DeviceInfo?.HwVersion);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch device info"); }

        try
        {
            DeviceStatus = await _httpClient.GetFromJsonAsync<HandyStatusResponse>("status", ct);
            _logger.LogInformation("Device status: mode={Mode} state={State}",
                DeviceStatus?.Mode, DeviceStatus?.State);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch device status"); }

        try
        {
            DeviceSlide = await _httpClient.GetFromJsonAsync<HandySlideResponse>("slide", ct);
            _logger.LogInformation("Device slide range: {Min}-{Max}",
                DeviceSlide?.Min, DeviceSlide?.Max);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch device slide"); }
    }

    public async Task SetSlideRangeAsync(double min, double max, CancellationToken ct = default)
    {
        if (_httpClient == null) return;

        await RetryAsync(async () =>
            await _httpClient.PutAsJsonAsync("slide",
                new HandySlideRequest { Min = min, Max = max }, ct), ct);

        DeviceSlide = new HandySlideResponse { Min = min, Max = max };
        _logger.LogInformation("Slide range set to {Min}-{Max}", min, max);
    }

    /// <summary>
    /// Auto-reconnect with exponential backoff when consecutive send failures exceed threshold.
    /// Video continues playing — device just stops moving until reconnection succeeds.
    /// </summary>
    private async void TryAutoReconnect()
    {
        if (_isReconnecting || ConnectionState != DeviceConnectionState.Connected) return;
        _isReconnecting = true;
        SetConnectionState(DeviceConnectionState.Connecting);
        _logger.LogWarning("Auto-reconnect triggered after {Failures} consecutive failures", _consecutiveFailures);

        for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            try
            {
                var delayMs = (int)(1000 * Math.Pow(2, attempt - 1)); // 1s, 2s, 4s, 8s, 16s
                await Task.Delay(Math.Min(delayMs, 8000));

                if (_httpClient == null || string.IsNullOrEmpty(_connectionKey)) break;

                var result = await _httpClient.GetFromJsonAsync<HandyConnectedResponse>("connected");
                if (result?.Connected == true)
                {
                    // Re-sync time (fewer rounds for reconnect) and restore mode + slide in parallel
                    await _timeSync.SyncAsync(_httpClient, rounds: 10);
                    var mode = _protocol == HandyProtocol.HDSP ? HandyDeviceModes.HDSP : HandyDeviceModes.HSSP;
                    var modeTask2 = Task.Run(async () =>
                    {
                        using var r = await _httpClient.PutAsJsonAsync("mode", new HandyModeRequest { Mode = mode });
                    });
                    var slideTask2 = SetSlideRangeAsync(0, 100);
                    await Task.WhenAll(modeTask2, slideTask2);
                    if (_protocol == HandyProtocol.HDSP) ResetHdspTrace();

                    _consecutiveFailures = 0;
                    SetConnectionState(DeviceConnectionState.Connected);
                    _logger.LogInformation("Auto-reconnect successful on attempt {Attempt}", attempt);
                    _isReconnecting = false;
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Auto-reconnect attempt {Attempt}/{Max} failed", attempt, MaxReconnectAttempts);
            }
        }

        _logger.LogError("Auto-reconnect failed after {Max} attempts", MaxReconnectAttempts);
        SetConnectionState(DeviceConnectionState.Error);
        _isReconnecting = false;
    }

    public void Dispose()
    {
        try { _hdspTraceWriter?.Dispose(); } catch { }
        _hdspTraceWriter = null;
        _httpClient?.Dispose();
    }

    private void SetConnectionState(DeviceConnectionState state)
    {
        ConnectionState = state;
        ConnectionStateChanged?.Invoke(this, state);
    }

    private static async Task<T?> RetryAsync<T>(Func<Task<T?>> action, CancellationToken ct, int maxAttempts = 3)
    {
        Exception? lastException = null;
        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                return await action();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (i < maxAttempts - 1)
            {
                lastException = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, i)), ct);
            }
        }
        throw lastException ?? new InvalidOperationException("Retry exhausted");
    }

    private async Task RetryAsync(Func<Task<HttpResponseMessage>> action, CancellationToken ct, int maxAttempts = 3)
    {
        Exception? lastException = null;
        for (int i = 0; i < maxAttempts; i++)
        {
            HttpResponseMessage? response = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                response = await action();
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Handy API HTTP {StatusCode} (attempt {Attempt}/{Max}): {Body}",
                        (int)response.StatusCode, i + 1, maxAttempts, body);
                }
                else if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogDebug("Handy API HTTP {StatusCode}: {Body}", (int)response.StatusCode, body);
                }
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (i < maxAttempts - 1)
            {
                lastException = ex;
                var delayMs = (int)(500 * Math.Pow(2, i));
                _logger.LogWarning(ex, "Handy API attempt {Attempt}/{Max} failed, retrying in {Delay}ms",
                    i + 1, maxAttempts, delayMs);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
            }
            finally
            {
                response?.Dispose();
            }
        }
        throw lastException ?? new InvalidOperationException("Retry exhausted");
    }
}
