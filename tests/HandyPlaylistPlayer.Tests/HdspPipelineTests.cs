using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Runtime;
using HandyPlaylistPlayer.Core.Services;
using NSubstitute;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

/// <summary>
/// Tests the complete HDSP position pipeline: funscript → interpolation → transform → device.
/// Validates that positions sent to the device are correct for known inputs.
/// </summary>
public class HdspPipelineTests
{
    // Realistic funscript: 0→100→0→100 at 500ms intervals
    private static readonly FunscriptDocument TestScript = new()
    {
        Actions =
        [
            new(0, 0), new(500, 100), new(1000, 0), new(1500, 100),
            new(2000, 0), new(2500, 100), new(3000, 0)
        ]
    };

    // ────────────────────────────────────────────────────
    // Transform pipeline with default settings
    // ────────────────────────────────────────────────────

    [Fact]
    public void DefaultSettings_NoSmoothing_FullRangePassthrough()
    {
        // Default settings have smoothing disabled — positions pass through unchanged
        var pipeline = new ScriptTransformPipeline();

        Assert.Equal(0, pipeline.Transform(0, 0));
        Assert.Equal(100, pipeline.Transform(100, 50));
        Assert.Equal(0, pipeline.Transform(0, 100));
        Assert.Equal(100, pipeline.Transform(100, 150));
        Assert.Equal(50, pipeline.Transform(50, 200));
    }

    // ────────────────────────────────────────────────────
    // Full pipeline simulation (interpolator + transform)
    // ────────────────────────────────────────────────────

    [Fact]
    public void FullPipeline_NoSmoothing_PositionsMatchInterpolation()
    {
        var pipeline = new ScriptTransformPipeline
        {
            Settings = TransformSettings.Default
        };

        var sentPositions = new List<int>();

        // Simulate 50ms tick engine over 3 seconds
        for (long t = 0; t <= 3000; t += 50)
        {
            var rawPos = FunscriptInterpolator.GetPositionAtTime(t, TestScript.Actions);
            var transformed = pipeline.Transform(rawPos, t);
            sentPositions.Add(transformed);
        }

        // Should hit extremes (0 and 100)
        Assert.Contains(0, sentPositions);
        Assert.Contains(100, sentPositions);

        // At t=0, pos should be 0
        Assert.Equal(0, sentPositions[0]);

        // At t=500ms (index 10), pos should be 100
        Assert.Equal(100, sentPositions[10]);

        // At t=1000ms (index 20), pos should be 0
        Assert.Equal(0, sentPositions[20]);
    }

    [Fact]
    public void FullPipeline_DefaultNoSmoothing_FullRange()
    {
        var pipeline = new ScriptTransformPipeline(); // default: smoothing disabled

        int min = 100, max = 0;

        for (long t = 0; t <= 3000; t += 50)
        {
            var rawPos = FunscriptInterpolator.GetPositionAtTime(t, TestScript.Actions);
            var transformed = pipeline.Transform(rawPos, t);
            min = Math.Min(min, transformed);
            max = Math.Max(max, transformed);
        }

        // Without smoothing, full 0-100 range should be reached
        Assert.Equal(0, min);
        Assert.Equal(100, max);
    }

    // ────────────────────────────────────────────────────
    // Streaming tick engine integration (with mock device)
    // ────────────────────────────────────────────────────

    [Fact]
    public async Task TickEngine_SendsEachKeyframeOnceToDevice()
    {
        var player = Substitute.For<IMediaPlayer>();
        var device = Substitute.For<IDeviceBackend>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger>();

        var sent = new System.Collections.Concurrent.ConcurrentQueue<(int Position, int Duration)>();
        device.SendPositionAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => sent.Enqueue((ci.ArgAt<int>(0), ci.ArgAt<int>(1))));

        // Media clock parked at the end of the script: the engine must jump straight to the last segment
        // (one resync send) and then stop, not replay every keyframe from zero.
        player.State.Returns(PlaybackState.Playing);
        player.PositionMs.Returns(2900);

        var pipeline = new ScriptTransformPipeline { Settings = TransformSettings.Default };
        using var engine = new StreamingTickEngine(player, device, TestScript, pipeline, logger, tickRateMs: 10);
        await engine.StartAsync(TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sent.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken); // room for any (wrong) extra sends
        await engine.StopAsync();

        var only = Assert.Single(sent);
        Assert.Equal(0, only.Position);   // last keyframe (3000 ms, pos 0)
        Assert.Equal(100, only.Duration); // time left until it
    }

    // ────────────────────────────────────────────────────
    // Interpolation edge cases
    // ────────────────────────────────────────────────────

    [Fact]
    public void Interpolator_AtActionTime_ReturnsExactPosition()
    {
        var actions = new List<FunscriptAction>
        {
            new(0, 0), new(500, 100), new(1000, 0)
        };

        Assert.Equal(0, FunscriptInterpolator.GetPositionAtTime(0, actions));
        Assert.Equal(100, FunscriptInterpolator.GetPositionAtTime(500, actions));
        Assert.Equal(0, FunscriptInterpolator.GetPositionAtTime(1000, actions));
    }

    [Fact]
    public void Interpolator_BetweenActions_LinearInterpolation()
    {
        var actions = new List<FunscriptAction>
        {
            new(0, 0), new(1000, 100)
        };

        Assert.Equal(25, FunscriptInterpolator.GetPositionAtTime(250, actions));
        Assert.Equal(50, FunscriptInterpolator.GetPositionAtTime(500, actions));
        Assert.Equal(75, FunscriptInterpolator.GetPositionAtTime(750, actions));
    }
}
