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
    public async Task TickEngine_SendsActionTargetsToDevice()
    {
        var player = Substitute.For<IMediaPlayer>();
        var device = Substitute.For<IDeviceBackend>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger>();

        var sentPositions = new List<(int Position, int Duration)>();
        device.SendPositionAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => sentPositions.Add((ci.ArgAt<int>(0), ci.ArgAt<int>(1))));

        // Simulate playing media
        player.State.Returns(PlaybackState.Playing);
        long currentTime = 0;
        player.PositionMs.Returns(_ => currentTime);

        var pipeline = new ScriptTransformPipeline
        {
            Settings = TransformSettings.Default
        };

        var engine = new StreamingTickEngine(player, device, TestScript, pipeline, logger, tickRateMs: 50);
        _ = engine.StartAsync();

        // Simulate 3 seconds of playback to cover all actions
        for (int i = 0; i < 60; i++)
        {
            currentTime = i * 50;
            await Task.Delay(55);
        }

        await engine.StopAsync();
        engine.Dispose();

        // Action-based: one send per action interval (6 intervals in 3 seconds)
        Assert.True(sentPositions.Count >= 4,
            $"Expected >=4 action-based sends, got {sentPositions.Count}");

        // All positions should be in valid 0-100 range with positive duration
        Assert.All(sentPositions, p =>
        {
            Assert.InRange(p.Position, 0, 100);
            Assert.True(p.Duration > 0, $"Duration should be > 0, was {p.Duration}");
        });

        // Positions should be action targets: 100 and 0 (alternating)
        var distinctPositions = sentPositions.Select(p => p.Position).Distinct().OrderBy(p => p).ToList();
        Assert.Contains(0, distinctPositions);
        Assert.Contains(100, distinctPositions);
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

    // ────────────────────────────────────────────────────
    // Deterministic tick engine simulation (no async, no timing)
    // Replays the exact logic the tick engine uses, step by step
    // ────────────────────────────────────────────────────

    [Fact]
    public void DeterministicSim_RealScript_SendsCorrectTargets()
    {
        // First 20 actions from the user's real funscript
        var realActions = new List<FunscriptAction>
        {
            new(0, 50), new(162, 42), new(325, 25), new(487, 7), new(650, 0),
            new(983, 13), new(1316, 44), new(1649, 76), new(1983, 90),
            new(2358, 79), new(2733, 55), new(3108, 30), new(3483, 20),
            new(3587, 25), new(3691, 40), new(3795, 54), new(3900, 60),
            new(3966, 55), new(4033, 45), new(4099, 34)
        };

        var pipeline = new ScriptTransformPipeline(); // default: no smoothing

        // Simulate what the tick engine does every 50ms
        // Track every (position, duration) that WOULD be sent to the device
        var sent = new List<(long Time, int Pos, int Dur, int ActionIndex)>();

        for (long t = 0; t <= 4100; t += 50)
        {
            var next = FunscriptInterpolator.GetNextAction(t, realActions);
            if (next == null) continue;

            var target = next.Value.Action;
            var dur = Math.Max((int)(target.At - t), 20);
            var pos = pipeline.Transform(target.Pos, t);

            sent.Add((t, pos, dur, next.Value.Index));
        }

        // === Verify the tick engine logic produces correct results ===

        // 1. Should have sent many positions (one per tick that has a next action)
        Assert.True(sent.Count > 50, $"Expected >50 tick outputs, got {sent.Count}");

        // 2. First tick at t=0: next action is index 1 (at=162, pos=42)
        Assert.Equal(0, sent[0].Time);
        Assert.Equal(42, sent[0].Pos);
        Assert.Equal(162, sent[0].Dur);
        Assert.Equal(1, sent[0].ActionIndex);

        // 3. Positions should vary — NOT all the same value
        var uniquePositions = sent.Select(s => s.Pos).Distinct().ToList();
        Assert.True(uniquePositions.Count >= 10,
            $"Expected >=10 unique positions, got {uniquePositions.Count}: [{string.Join(", ", uniquePositions)}]");

        // 4. Should include low AND high values (not stuck at one extreme)
        var minPos = sent.Min(s => s.Pos);
        var maxPos = sent.Max(s => s.Pos);
        Assert.True(minPos <= 10, $"Min position should be <=10, was {minPos}");
        Assert.True(maxPos >= 80, $"Max position should be >=80, was {maxPos}");

        // 5. All durations should be positive and reasonable
        Assert.All(sent, s =>
        {
            Assert.InRange(s.Dur, 20, 5000);
            Assert.InRange(s.Pos, 0, 100);
        });

        // 6. At t=0, target changes at each action boundary
        //    Verify we see different action indices as time advances
        var firstActionIdx = sent.First(s => s.Time == 0).ActionIndex;
        var midActionIdx = sent.First(s => s.Time >= 2000).ActionIndex;
        Assert.True(midActionIdx > firstActionIdx,
            $"Action index should advance: first={firstActionIdx}, at 2000ms={midActionIdx}");
    }

    [Fact]
    public void DeterministicSim_SameTargetSentMultipleTicks_UntilNextAction()
    {
        // Simple script: action at 0,500,1000
        var actions = new List<FunscriptAction>
        {
            new(0, 0), new(500, 100), new(1000, 0)
        };

        var sent = new List<(long Time, int Pos, int Dur, int Index)>();
        var pipeline = new ScriptTransformPipeline();

        for (long t = 0; t <= 1000; t += 50)
        {
            var next = FunscriptInterpolator.GetNextAction(t, actions);
            if (next == null) continue;

            var target = next.Value.Action;
            var dur = Math.Max((int)(target.At - t), 20);
            var pos = pipeline.Transform(target.Pos, t);
            sent.Add((t, pos, dur, next.Value.Index));
        }

        // From t=0 to t=450, next action is always index 1 (at=500, pos=100)
        // The tick engine sends pos=100 with decreasing duration each tick
        var firstPhase = sent.Where(s => s.Time < 500).ToList();
        Assert.True(firstPhase.Count >= 9, $"Expected >=9 ticks before t=500, got {firstPhase.Count}");
        Assert.All(firstPhase, s =>
        {
            Assert.Equal(100, s.Pos);  // ALL ticks send pos=100 (the target)
            Assert.Equal(1, s.Index);   // ALL point to action index 1
        });

        // Duration decreases as we approach the target time
        Assert.True(firstPhase.First().Dur > firstPhase.Last().Dur,
            $"Duration should decrease: first={firstPhase.First().Dur}, last={firstPhase.Last().Dur}");

        // From t=500 to t=950, next action is index 2 (at=1000, pos=0)
        var secondPhase = sent.Where(s => s.Time >= 500).ToList();
        Assert.True(secondPhase.Count >= 9, $"Expected >=9 ticks after t=500, got {secondPhase.Count}");
        Assert.All(secondPhase, s =>
        {
            Assert.Equal(0, s.Pos);    // ALL ticks send pos=0 (the target)
            Assert.Equal(2, s.Index);   // ALL point to action index 2
        });
    }

    [Fact]
    public void DeterministicSim_WithHttpLatency_DeviceGetsCorrectPositions()
    {
        // Simulate _hdspSendInFlight: only 1 in every 4 ticks actually sends
        // (HTTP round-trip ~200ms with 50ms ticks = every 4th tick gets through)
        var actions = new List<FunscriptAction>
        {
            new(0, 0), new(500, 100), new(1000, 0), new(1500, 100), new(2000, 0)
        };

        var pipeline = new ScriptTransformPipeline();
        var actuallySent = new List<(long Time, int Pos, int Dur)>();
        int ticksSinceLastSend = 4; // start ready to send

        for (long t = 0; t <= 2000; t += 50)
        {
            var next = FunscriptInterpolator.GetNextAction(t, actions);
            if (next == null) continue;

            var target = next.Value.Action;
            var dur = Math.Max((int)(target.At - t), 20);
            var pos = pipeline.Transform(target.Pos, t);

            ticksSinceLastSend++;

            // Simulate: can only send every 4th tick (HTTP in flight for ~200ms)
            if (ticksSinceLastSend >= 4)
            {
                actuallySent.Add((t, pos, dur));
                ticksSinceLastSend = 0;
            }
        }

        // Even with 75% of sends dropped, device should still get correct targets
        Assert.True(actuallySent.Count >= 8,
            $"Expected >=8 actual sends, got {actuallySent.Count}");

        // Should have both 0 and 100 in the sent positions
        var sentPositions = actuallySent.Select(s => s.Pos).Distinct().ToList();
        Assert.Contains(0, sentPositions);
        Assert.Contains(100, sentPositions);

        // All positions should be valid action targets (0 or 100), never interpolated
        Assert.All(actuallySent, s =>
            Assert.True(s.Pos == 0 || s.Pos == 100,
                $"Position should be 0 or 100 (action target), was {s.Pos} at t={s.Time}ms"));
    }
}
