using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Runtime;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

/// <summary>
/// Tick-by-tick tests for the streaming (HDSP / Intiface) keyframe planner. No timers: each test drives
/// <see cref="TickPlanner.Plan"/> with explicit media times, the way <see cref="StreamingTickEngine"/> does.
/// </summary>
public class TickPlannerTests
{
    private static readonly FunscriptAction[] Strokes =
        [new(0, 0), new(1000, 100), new(2000, 0), new(3000, 100), new(4000, 0), new(5000, 100), new(6000, 0)];

    /// <summary>Simulates the engine loop: plans every tick and marks every Send as delivered.</summary>
    private static List<(long Time, TickDecision Decision)> Run(
        TickPlanner planner, FunscriptAction[] actions, long from, long to, long step = 10)
    {
        var sent = new List<(long, TickDecision)>();
        for (var t = from; t <= to; t += step)
        {
            var d = planner.Plan(t, actions);
            if (d.Outcome != TickOutcome.Send) continue;
            planner.MarkSent(d.Index);
            sent.Add((t, d));
        }
        return sent;
    }

    [Fact]
    public void SteadyPlayback_SendsEachKeyframeExactlyOnce_InOrder()
    {
        var sent = Run(new TickPlanner(), Strokes, 0, 6500);

        Assert.Equal([0, 1, 2, 3, 4, 5, 6], sent.Select(s => s.Decision.Index));
    }

    [Fact]
    public void SteadyPlayback_DurationIsTheScriptGap()
    {
        var sent = Run(new TickPlanner(), Strokes, 0, 6500);

        // First keyframe: move there quickly (min duration); then each move takes the scripted gap.
        Assert.Equal(TickPlanner.MinDurationMs, sent[0].Decision.DurationMs);
        Assert.All(sent.Skip(1), s => Assert.Equal(1000, s.Decision.DurationMs));
        Assert.DoesNotContain(sent, s => s.Decision.Resynced);
    }

    [Fact]
    public void NextKeyframeIsSentOnlyAfterTheMediaCrossesThePreviousOne()
    {
        var sent = Run(new TickPlanner(), Strokes, 0, 6500);

        foreach (var (time, d) in sent.Skip(1))
            Assert.InRange(time, Strokes[d.Index - 1].At, Strokes[d.Index - 1].At + 10);
    }

    [Fact]
    public void StartMidScript_JumpsToCurrentSegment_InsteadOfReplayingFromZero()
    {
        var planner = new TickPlanner();

        var d = planner.Plan(2600, Strokes);

        Assert.Equal(TickOutcome.Send, d.Outcome);
        Assert.Equal(3, d.Index);              // heading for the keyframe at 3000 ms
        Assert.Equal(400, d.DurationMs);       // time left until it, not the full 1000 ms gap
        Assert.True(d.Resynced);
    }

    [Fact]
    public void ResumeAfterLongPause_DoesNotReplayMissedKeyframes()
    {
        var planner = new TickPlanner();
        Run(planner, Strokes, 0, 1200);        // sent 0, 1, 2
        Assert.Equal(2, planner.LastSentIndex);

        var sent = Run(planner, Strokes, 4300, 6500);

        Assert.Equal([5, 6], sent.Select(s => s.Decision.Index));
        Assert.True(sent[0].Decision.Resynced);
        Assert.Equal(700, sent[0].Decision.DurationMs);
    }

    [Fact]
    public void BackwardSeek_RestartsFromTheNewSegment()
    {
        var planner = new TickPlanner();
        Run(planner, Strokes, 0, 4500);        // sent 0..5
        Assert.Equal(5, planner.LastSentIndex);

        var d = planner.Plan(1500, Strokes);

        Assert.Equal(TickOutcome.Send, d.Outcome);
        Assert.Equal(2, d.Index);
        Assert.Equal(500, d.DurationMs);
        Assert.True(d.Resynced);
    }

    [Fact]
    public void FastKeyframesWithinOneTick_AreAllSentSequentially()
    {
        // Vibration burst: 3 keyframes 4 ms apart, much faster than the 10 ms tick.
        FunscriptAction[] burst = [new(0, 0), new(1000, 50), new(1004, 60), new(1008, 40), new(1012, 60), new(2000, 0)];

        var sent = Run(new TickPlanner(), burst, 0, 2100);

        Assert.Equal([0, 1, 2, 3, 4, 5], sent.Select(s => s.Decision.Index));
        Assert.All(sent, s => Assert.InRange(s.Decision.DurationMs, TickPlanner.MinDurationMs, TickPlanner.MaxDurationMs));
    }

    [Fact]
    public void FailedSend_IsRetriedOnTheNextTick()
    {
        var planner = new TickPlanner();
        planner.MarkSent(0);

        var first = planner.Plan(10, Strokes);    // not marked: the device call failed
        var retry = planner.Plan(20, Strokes);

        Assert.Equal(1, first.Index);
        Assert.Equal(1, retry.Index);
    }

    [Fact]
    public void BeforeFirstAction_AndAfterLast_SendNothing()
    {
        FunscriptAction[] late = [new(500, 0), new(1000, 100)];
        var planner = new TickPlanner();

        Assert.Equal(TickOutcome.SkipBefore, planner.Plan(100, late).Outcome);
        Run(planner, late, 500, 1500);
        Assert.Equal(TickOutcome.SkipEnd, planner.Plan(1600, late).Outcome);
    }

    [Theory]
    [InlineData(5, TickPlanner.MinDurationMs)]
    [InlineData(60_000, TickPlanner.MaxDurationMs)]
    public void Duration_IsClamped(long gapMs, int expected)
    {
        FunscriptAction[] actions = [new(0, 0), new(gapMs, 100)];
        var planner = new TickPlanner();
        planner.MarkSent(0);

        Assert.Equal(expected, planner.Plan(0, actions).DurationMs);
    }

    [Fact]
    public void RealScript_EveryKeyframeSentOnceWithScriptedTargets()
    {
        FunscriptAction[] real =
        [
            new(0, 50), new(162, 42), new(325, 25), new(487, 7), new(650, 0),
            new(983, 13), new(1316, 44), new(1649, 76), new(1983, 90),
            new(2358, 79), new(2733, 55), new(3108, 30), new(3483, 20),
            new(3587, 25), new(3691, 40), new(3795, 54), new(3900, 60),
            new(3966, 55), new(4033, 45), new(4099, 34)
        ];

        var sent = Run(new TickPlanner(), real, 0, 4200, step: 50);

        Assert.Equal(Enumerable.Range(0, real.Length), sent.Select(s => s.Decision.Index));
    }
}
