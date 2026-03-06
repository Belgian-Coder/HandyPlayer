using System.Net.Http.Json;
using HandyPlaylistPlayer.Devices.HandyApi.Models;
using NSubstitute;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

/// <summary>
/// Live integration tests against a real Handy device.
/// These tests require a device to be online and reachable.
/// Run with: dotnet test --filter "Category=LiveDevice"
/// </summary>
[Trait("Category", "LiveDevice")]
public class HdspLiveDeviceTests : IDisposable
{
    private const string ConnectionKey = "g6zPaXWz";
    private readonly HttpClient _client;
    private readonly List<string> _log = [];

    public HdspLiveDeviceTests()
    {
        _client = new HttpClient
        {
            BaseAddress = new Uri("https://www.handyfeeling.com/api/handy/v2/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _client.DefaultRequestHeaders.Add("X-Connection-Key", ConnectionKey);
    }

    [Fact]
    public async Task Device_IsConnected()
    {
        var response = await _client.GetFromJsonAsync<HandyConnectedResponse>("connected");
        _log.Add($"Connected: {response?.Connected}");
        Assert.NotNull(response);
        Assert.True(response.Connected, "Device must be online for live tests");
    }

    [Fact]
    public async Task Device_CanGetInfo()
    {
        var connected = await _client.GetFromJsonAsync<HandyConnectedResponse>("connected");
        if (connected?.Connected != true)
        {
            _log.Add("Device not connected — skipping info test");
            return;
        }

        var info = await _client.GetFromJsonAsync<HandyInfoResponse>("info");
        _log.Add($"Model: {info?.Model}, FW: {info?.FwVersion}, HW: {info?.HwVersion}");
        Assert.NotNull(info);
    }

    [Fact]
    public async Task Device_CanGetSlideRange()
    {
        var slide = await _client.GetFromJsonAsync<HandySlideResponse>("slide");
        _log.Add($"Slide range: {slide?.Min} - {slide?.Max}");
        Assert.NotNull(slide);
        Assert.InRange(slide.Min, 0, 100);
        Assert.InRange(slide.Max, 0, 100);
        Assert.True(slide.Max > slide.Min, "Max should be greater than Min");
    }

    [Fact]
    public async Task Hdsp_SetMode_Succeeds()
    {
        // Set to HDSP mode (mode 2)
        using var response = await _client.PutAsJsonAsync("mode", new HandyModeRequest { Mode = 2 });
        var body = await response.Content.ReadAsStringAsync();
        _log.Add($"Set mode response: {response.StatusCode} - {body}");
        Assert.True(response.IsSuccessStatusCode, $"Failed to set HDSP mode: {body}");
    }

    [Fact]
    public async Task Hdsp_SendPosition0_Succeeds()
    {
        // Ensure HDSP mode
        using var modeResp = await _client.PutAsJsonAsync("mode", new HandyModeRequest { Mode = 2 });
        modeResp.EnsureSuccessStatusCode();

        // Send position 0 (bottom)
        using var response = await _client.PutAsJsonAsync("hdsp/xpt",
            new HandyHdspXptRequest { Position = 0, Duration = 500, ImmediateResponse = true });
        var body = await response.Content.ReadAsStringAsync();
        _log.Add($"xpt pos=0: {response.StatusCode} - {body}");
        Assert.True(response.IsSuccessStatusCode, $"HDSP xpt failed: {body}");
    }

    [Fact]
    public async Task Hdsp_SendPosition100_Succeeds()
    {
        using var modeResp = await _client.PutAsJsonAsync("mode", new HandyModeRequest { Mode = 2 });
        modeResp.EnsureSuccessStatusCode();

        using var response = await _client.PutAsJsonAsync("hdsp/xpt",
            new HandyHdspXptRequest { Position = 100, Duration = 500, ImmediateResponse = true });
        var body = await response.Content.ReadAsStringAsync();
        _log.Add($"xpt pos=100: {response.StatusCode} - {body}");
        Assert.True(response.IsSuccessStatusCode, $"HDSP xpt failed: {body}");
    }

    [Fact]
    public async Task Hdsp_FullStrokeTest_MovesDeviceFullRange()
    {
        // Set HDSP mode
        using var modeResp = await _client.PutAsJsonAsync("mode", new HandyModeRequest { Mode = 2 });
        modeResp.EnsureSuccessStatusCode();

        // Set slide range to full (0-100) to ensure max movement
        using var slideResp = await _client.PutAsJsonAsync("slide",
            new HandySlideRequest { Min = 0, Max = 100 });
        var slideBody = await slideResp.Content.ReadAsStringAsync();
        _log.Add($"Set slide 0-100: {slideResp.StatusCode} - {slideBody}");

        // Send alternating positions: 0 → 100 → 0 → 100
        var positions = new[] { 0, 100, 0, 100, 0 };
        foreach (var pos in positions)
        {
            using var resp = await _client.PutAsJsonAsync("hdsp/xpt",
                new HandyHdspXptRequest
                {
                    Position = pos,
                    Duration = 400,
                    ImmediateResponse = true
                });
            var body = await resp.Content.ReadAsStringAsync();
            _log.Add($"xpt pos={pos}: {resp.StatusCode} - {body}");
            Assert.True(resp.IsSuccessStatusCode, $"Failed at pos={pos}: {body}");
            await Task.Delay(500); // Wait for device to move
        }

        _log.Add("Full stroke test complete — device should have moved up and down");
    }

    [Fact]
    public async Task Hdsp_SimulatedPlayback_SendsRealisticPositions()
    {
        // Set HDSP mode
        using var modeResp = await _client.PutAsJsonAsync("mode", new HandyModeRequest { Mode = 2 });
        modeResp.EnsureSuccessStatusCode();

        // Simulate a funscript pattern: 0→100→0→100 at 500ms intervals
        // Like the tick engine would, interpolate at 100ms intervals
        var actions = new[]
        {
            (At: 0, Pos: 0), (At: 500, Pos: 100),
            (At: 1000, Pos: 0), (At: 1500, Pos: 100),
            (At: 2000, Pos: 0)
        };

        int successCount = 0;
        int errorCount = 0;

        for (int t = 0; t <= 2000; t += 100)
        {
            // Linear interpolation
            var pos = InterpolatePosition(t, actions);

            using var resp = await _client.PutAsJsonAsync("hdsp/xpt",
                new HandyHdspXptRequest
                {
                    Position = pos,
                    Duration = 150,
                    ImmediateResponse = true
                });

            if (resp.IsSuccessStatusCode)
            {
                successCount++;
                var body = await resp.Content.ReadAsStringAsync();
                _log.Add($"t={t}ms pos={pos:F1}: OK - {body}");
            }
            else
            {
                errorCount++;
                var body = await resp.Content.ReadAsStringAsync();
                _log.Add($"t={t}ms pos={pos:F1}: FAIL {resp.StatusCode} - {body}");
            }

            await Task.Delay(100);
        }

        _log.Add($"\nResults: {successCount} success, {errorCount} errors");
        Assert.True(errorCount == 0, $"{errorCount} API calls failed");
        Assert.True(successCount > 15, $"Expected >15 successful calls, got {successCount}");
    }

    [Fact]
    public async Task Hdsp_ActionBasedTargets_DeviceMovesCorrectly()
    {
        // This test exercises the EXACT logic the tick engine uses:
        // send the NEXT action's target position, not interpolated intermediate values.

        // Set HDSP mode
        using var modeResp = await _client.PutAsJsonAsync("mode", new HandyModeRequest { Mode = 2 });
        modeResp.EnsureSuccessStatusCode();

        // Use a subset of the user's real funscript
        var actions = new (int At, int Pos)[]
        {
            (0, 50), (162, 42), (325, 25), (487, 7), (650, 0),
            (983, 13), (1316, 44), (1649, 76), (1983, 90),
            (2358, 79), (2733, 55), (3108, 30), (3483, 20)
        };

        int successCount = 0;
        int errorCount = 0;
        int lastTargetIndex = -1;

        // Simulate 50ms ticks over 3.5 seconds, with ~200ms HTTP latency
        for (int t = 0; t <= 3500; t += 50)
        {
            // Find next action strictly after current time (same as GetNextAction)
            int nextIdx = -1;
            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i].At > t) { nextIdx = i; break; }
            }
            if (nextIdx < 0) continue;

            var target = actions[nextIdx];
            var dur = Math.Max(target.At - t, 20);

            // Simulate _hdspSendInFlight: only send when target changes
            // (in real code, same target gets resent but most are dropped by _hdspSendInFlight)
            if (nextIdx == lastTargetIndex) continue;

            using var resp = await _client.PutAsJsonAsync("hdsp/xpt",
                new HandyHdspXptRequest
                {
                    Position = target.Pos,
                    Duration = dur,
                    ImmediateResponse = true
                });

            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                successCount++;
                _log.Add($"t={t}ms → target[{nextIdx}] pos={target.Pos} dur={dur}ms: OK - {body}");
                lastTargetIndex = nextIdx;
            }
            else
            {
                errorCount++;
                _log.Add($"t={t}ms → target[{nextIdx}] pos={target.Pos}: FAIL {resp.StatusCode} - {body}");
            }

            // Wait for device to start moving before sending next
            await Task.Delay(Math.Min(dur, 300));
        }

        _log.Add($"\nAction-based results: {successCount} OK, {errorCount} errors, {actions.Length - 1} action targets");

        Assert.True(errorCount == 0, $"{errorCount} API calls failed. Log:\n{string.Join("\n", _log)}");
        Assert.True(successCount >= 8, $"Expected >=8 successful sends, got {successCount}");
    }

    [Fact]
    public async Task Hdsp_RealTickEngine_WithMockPlayer_DeviceMoves()
    {
        // This test uses the REAL StreamingTickEngine + REAL HandyApiClient
        // with a mock media player. If device moves correctly here but not
        // in the app, the issue is VLC time reporting.

        var hostingService = NSubstitute.Substitute.For<HandyPlaylistPlayer.Core.Interfaces.IScriptHostingService>();
        var timeSync = new HandyPlaylistPlayer.Devices.HandyApi.ServerTimeSyncService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HandyPlaylistPlayer.Devices.HandyApi.ServerTimeSyncService>.Instance);
        var apiLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<HandyPlaylistPlayer.Devices.HandyApi.HandyApiClient>.Instance;
        var client = new HandyPlaylistPlayer.Devices.HandyApi.HandyApiClient(hostingService, timeSync, apiLogger);

        client.SetConnectionKey(ConnectionKey);
        var connected = await client.ConnectAsync();
        Assert.True(connected, "Device must be online");

        // Script: 0→100→0→100→0 at 400ms intervals (clear visible strokes)
        var script = new HandyPlaylistPlayer.Core.Models.FunscriptDocument
        {
            Actions =
            [
                new(0, 0), new(400, 100), new(800, 0), new(1200, 100),
                new(1600, 0), new(2000, 100), new(2400, 0)
            ]
        };

        // Mock media player with advancing time
        var mockPlayer = NSubstitute.Substitute.For<HandyPlaylistPlayer.Core.Interfaces.IMediaPlayer>();
        mockPlayer.State.Returns(HandyPlaylistPlayer.Core.Models.PlaybackState.Playing);
        long currentTimeMs = 0;
        mockPlayer.PositionMs.Returns(ci => currentTimeMs);

        var pipeline = new HandyPlaylistPlayer.Core.Runtime.ScriptTransformPipeline();
        var engineLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var engine = new HandyPlaylistPlayer.Core.Runtime.StreamingTickEngine(
            mockPlayer, client, script, pipeline, engineLogger, tickRateMs: 50);

        _ = engine.StartAsync();

        // Advance time over 2.5 seconds — should produce 6 visible strokes
        for (int i = 0; i < 50; i++)
        {
            currentTimeMs = i * 50;
            await Task.Delay(55);
        }

        await engine.StopAsync();
        engine.Dispose();
        client.Dispose();

        _log.Add($"Tick engine ran for {currentTimeMs}ms — check device for 6 alternating strokes");
        // If you saw the device moving 0↔100 in clear strokes, the engine works.
        // If the device went to one end and stayed, the issue is _hdspSendInFlight timing.
    }

    private static double InterpolatePosition(int timeMs, (int At, int Pos)[] actions)
    {
        if (timeMs <= actions[0].At) return actions[0].Pos;
        if (timeMs >= actions[^1].At) return actions[^1].Pos;

        for (int i = 0; i < actions.Length - 1; i++)
        {
            if (timeMs >= actions[i].At && timeMs <= actions[i + 1].At)
            {
                double t = (double)(timeMs - actions[i].At) / (actions[i + 1].At - actions[i].At);
                return actions[i].Pos + t * (actions[i + 1].Pos - actions[i].Pos);
            }
        }
        return 50;
    }

    public void Dispose() => _client.Dispose();
}
