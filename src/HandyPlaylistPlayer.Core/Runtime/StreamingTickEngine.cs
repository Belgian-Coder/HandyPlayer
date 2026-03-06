using System.Globalization;
using System.Text;
using HandyPlaylistPlayer.Core.Services;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Core.Runtime;

public class StreamingTickEngine : IDisposable
{
    private readonly IMediaPlayer _mediaPlayer;
    private readonly IDeviceBackend _device;
    private readonly FunscriptDocument _script;
    private readonly ScriptTransformPipeline _pipeline;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _tickTask;
    private volatile int _tickRateMs;
    private volatile int _offsetMs;
    private volatile bool _isRunning;
    private StreamWriter? _traceWriter;
    private readonly StringBuilder _traceSb = new(256);

    public bool IsRunning => _isRunning;

    public StreamingTickEngine(
        IMediaPlayer mediaPlayer,
        IDeviceBackend device,
        FunscriptDocument script,
        ScriptTransformPipeline pipeline,
        ILogger logger,
        int tickRateMs = 10)
    {
        _mediaPlayer = mediaPlayer;
        _device = device;
        _script = script;
        _pipeline = pipeline;
        _logger = logger;
        _tickRateMs = tickRateMs;
    }

    public void UpdateOffset(int offsetMs) => _offsetMs = offsetMs;
    public void UpdateTickRate(int tickRateMs) => _tickRateMs = Math.Max(10, tickRateMs);

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning) return Task.CompletedTask;
        _isRunning = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pipeline.Reset();

        // Open trace file for HDSP diagnostics (overwrites previous run)
        try
        {
            var tracePath = Path.Combine(AppContext.BaseDirectory, "hdsp_trace.csv");
            _traceWriter = new StreamWriter(tracePath, append: false) { AutoFlush = true };
            _traceWriter.WriteLine("tick,wall_ms,vlc_state,vlc_time_ms,media_time_ms,action_idx,target_at,raw_pos,transformed_pos,duration_ms,sent");
            _logger.LogInformation("HDSP trace file: {Path}", tracePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open HDSP trace file");
        }

        _tickTask = Task.Run(() => TickLoopAsync(_cts.Token), _cts.Token);
        _logger.LogInformation("Streaming tick engine started at {Rate}ms, script has {Count} actions",
            _tickRateMs, _script.Actions.Count);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _cts?.Cancel();

        if (_tickTask != null)
        {
            try { await _tickTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Tick task error during stop"); }
            _tickTask = null;
        }

        CloseTrace();
        _logger.LogInformation("Streaming tick engine stopped");
    }

    public void Stop()
    {
        _isRunning = false;
        _cts?.Cancel();
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        var currentTickRate = _tickRateMs;
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(currentTickRate));
        int tickCount = 0;
        // Tracks which keyframe index we last sent — advance sequentially so
        // fast transitions (vibrations, rapid strokes) are never skipped.
        int currentIndex = -1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var actions = _script.Actions;

        // Periodic summary counters (logged every 5 seconds)
        int sendCount = 0, skipStateCount = 0, skipBeforeCount = 0, skipEndCount = 0, skipWaitCount = 0;
        long lastSummaryMs = 0;
        const long SummaryIntervalMs = 5000;
        try
        {
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
            {
                var wallMs = sw.ElapsedMilliseconds;
                var vlcState = _mediaPlayer.State;
                var vlcTimeMs = _mediaPlayer.PositionMs;

                // Periodic summary log
                if (wallMs - lastSummaryMs >= SummaryIntervalMs)
                {
                    _logger.LogInformation(
                        "Tick engine summary: ticks={Ticks}, sends={Sends}, skipState={SkipState}, skipBefore={SkipBefore}, skipEnd={SkipEnd}, skipWait={SkipWait}, vlcState={State}, vlcTime={VlcTime}ms, currentIdx={Idx}/{Total}",
                        tickCount, sendCount, skipStateCount, skipBeforeCount, skipEndCount, skipWaitCount,
                        vlcState, vlcTimeMs, currentIndex, actions.Count);
                    lastSummaryMs = wallMs;
                }

                if (vlcState != PlaybackState.Playing)
                {
                    skipStateCount++;
                    WriteTrace(tickCount, wallMs, vlcState, vlcTimeMs, -1, -1, -1, -1, -1, -1, "skip_state");
                    continue;
                }

                var mediaTimeMs = vlcTimeMs + _offsetMs;

                // Find which keyframe index the media time has reached.
                // This is the last action whose timestamp <= mediaTimeMs.
                var mediaIndex = FindCurrentIndex(mediaTimeMs, actions);

                if (mediaIndex < 0)
                {
                    // Before the first action — nothing to send yet
                    skipBeforeCount++;
                    tickCount++;
                    continue;
                }

                // On seek backward, reset to the new position
                if (mediaIndex < currentIndex)
                {
                    _logger.LogInformation("Tick engine: seek backward detected, mediaIdx={MediaIdx} < currentIdx={CurrentIdx}",
                        mediaIndex, currentIndex);
                    currentIndex = mediaIndex - 1;
                }

                // The next keyframe to send is currentIndex + 1 (the one we're moving toward).
                var sendIdx = currentIndex + 1;

                if (sendIdx >= actions.Count)
                {
                    skipEndCount++;
                    WriteTrace(tickCount, wallMs, vlcState, vlcTimeMs, mediaTimeMs, -1, -1, -1, -1, -1, "skip_end");
                    tickCount++;
                    continue;
                }

                // Only send when media time has passed or reached the previous keyframe
                // (i.e., we've crossed to a new segment), matching MultiFunPlayer's approach.
                if (sendIdx > 0 && mediaTimeMs < actions[sendIdx - 1].At)
                {
                    skipWaitCount++;
                    tickCount++;
                    continue;
                }

                var targetAction = actions[sendIdx];

                // Duration = inter-keyframe gap (how long this movement takes in the script).
                // This matches MultiFunPlayer: snapshot.Duration = keyframeTo.Position - keyframeFrom.Position
                var prevAction = sendIdx > 0 ? actions[sendIdx - 1] : targetAction;
                var durationMs = Math.Clamp((int)(targetAction.At - prevAction.At), 20, 5000);

                var transformedPos = _pipeline.Transform(targetAction.Pos, mediaTimeMs);

                string sendResult;
                try
                {
                    await _device.SendPositionAsync(transformedPos, durationMs, ct);
                    currentIndex = sendIdx;
                    sendCount++;
                    sendResult = "ok";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sendResult = $"err:{ex.GetType().Name}";
                    _logger.LogWarning(ex, "Failed to send position command");
                }

                WriteTrace(tickCount, wallMs, vlcState, vlcTimeMs, mediaTimeMs,
                    sendIdx, targetAction.At, targetAction.Pos,
                    transformedPos, durationMs, sendResult);

                tickCount++;

                // Recreate timer if tick rate changed
                var newTickRate = _tickRateMs;
                if (newTickRate != currentTickRate)
                {
                    currentTickRate = newTickRate;
                    var newTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(currentTickRate));
                    var oldTimer = timer;
                    timer = newTimer;
                    oldTimer.Dispose();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tick engine error");
        }
        finally
        {
            timer.Dispose();
            CloseTrace();
            _logger.LogInformation(
                "Tick engine completed: ticks={Ticks}, sends={Sends}, skipState={SkipState}, skipBefore={SkipBefore}, skipEnd={SkipEnd}, skipWait={SkipWait}",
                tickCount, sendCount, skipStateCount, skipBeforeCount, skipEndCount, skipWaitCount);
        }
    }

    /// <summary>
    /// Binary search for the last action whose timestamp is &lt;= timeMs.
    /// Returns -1 if timeMs is before all actions.
    /// </summary>
    private static int FindCurrentIndex(long timeMs, IReadOnlyList<FunscriptAction> actions)
    {
        int lo = 0, hi = actions.Count - 1;
        int result = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (actions[mid].At <= timeMs)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }

    private void WriteTrace(int tick, long wallMs, PlaybackState state, long vlcTime, long mediaTime,
        int actionIdx, long targetAt, int rawPos, int transformedPos, int durationMs, string sent)
    {
        try
        {
            if (_traceWriter == null) return;
            // Reuse StringBuilder to avoid per-tick string allocations
            _traceSb.Clear();
            _traceSb.Append(tick).Append(',')
                .Append(wallMs).Append(',')
                .Append(state).Append(',')
                .Append(vlcTime).Append(',')
                .Append(mediaTime).Append(',')
                .Append(actionIdx).Append(',')
                .Append(targetAt).Append(',')
                .Append(rawPos).Append(',')
                .Append(transformedPos).Append(',')
                .Append(durationMs).Append(',')
                .Append(sent);
            _traceWriter.WriteLine(_traceSb);
        }
        catch { /* trace is best-effort */ }
    }

    private void CloseTrace()
    {
        try { _traceWriter?.Dispose(); } catch { }
        _traceWriter = null;
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts?.Cancel();

        try
        {
            _tickTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* tick task cleanup is best-effort in sync dispose */ }

        CloseTrace();
        _cts?.Dispose();
    }
}
