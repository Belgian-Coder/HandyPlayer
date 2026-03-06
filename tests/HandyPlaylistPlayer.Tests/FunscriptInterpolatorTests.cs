using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Services;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class FunscriptInterpolatorTests
{
    private static readonly List<FunscriptAction> SampleActions =
    [
        new(0, 0),
        new(1000, 100),
        new(2000, 50),
        new(3000, 0)
    ];

    [Fact]
    public void GetPositionAtTime_EmptyActions_Returns50()
    {
        Assert.Equal(50, FunscriptInterpolator.GetPositionAtTime(500, []));
    }

    [Fact]
    public void GetPositionAtTime_BeforeFirstAction_ReturnsFirstPos()
    {
        Assert.Equal(0, FunscriptInterpolator.GetPositionAtTime(-100, SampleActions));
    }

    [Fact]
    public void GetPositionAtTime_AfterLastAction_ReturnsLastPos()
    {
        Assert.Equal(0, FunscriptInterpolator.GetPositionAtTime(5000, SampleActions));
    }

    [Fact]
    public void GetPositionAtTime_ExactMatch_ReturnsExactPos()
    {
        Assert.Equal(100, FunscriptInterpolator.GetPositionAtTime(1000, SampleActions));
    }

    [Fact]
    public void GetPositionAtTime_Midpoint_Interpolates()
    {
        Assert.Equal(50, FunscriptInterpolator.GetPositionAtTime(500, SampleActions));
    }

    [Fact]
    public void GetPositionAtTime_QuarterPoint_InterpolatesCorrectly()
    {
        // Quarter between (1000,100) and (2000,50) => 100 + 0.25*(50-100) = 87.5 -> 88
        Assert.Equal(88, FunscriptInterpolator.GetPositionAtTime(1250, SampleActions));
    }

    [Fact]
    public void GetNextAction_ReturnsNextAfterTime()
    {
        var result = FunscriptInterpolator.GetNextAction(500, SampleActions);
        Assert.NotNull(result);
        Assert.Equal(1000, result.Value.Action.At);
        Assert.Equal(1, result.Value.Index);
    }

    [Fact]
    public void GetNextAction_PastEnd_ReturnsNull()
    {
        Assert.Null(FunscriptInterpolator.GetNextAction(5000, SampleActions));
    }

    [Fact]
    public void GetNextAction_ExactTime_ReturnsNext()
    {
        var result = FunscriptInterpolator.GetNextAction(1000, SampleActions);
        Assert.NotNull(result);
        Assert.Equal(2000, result.Value.Action.At);
    }

    // --- GetNormalizedPosition ---

    [Fact]
    public void GetNormalizedPosition_EmptyActions_ReturnsZero()
    {
        Assert.Equal(0.0, FunscriptInterpolator.GetNormalizedPosition(500, []));
    }

    [Fact]
    public void GetNormalizedPosition_BeforeFirstAction_ReturnsNormalizedFirstPos()
    {
        // First action is pos=0 → 0.0
        Assert.Equal(0.0, FunscriptInterpolator.GetNormalizedPosition(-100, SampleActions));
    }

    [Fact]
    public void GetNormalizedPosition_AfterLastAction_ReturnsNormalizedLastPos()
    {
        // Last action is pos=0 → 0.0
        Assert.Equal(0.0, FunscriptInterpolator.GetNormalizedPosition(5000, SampleActions));
    }

    [Fact]
    public void GetNormalizedPosition_ExactMatch_ReturnsNormalizedPos()
    {
        // At t=1000, pos=100 → 1.0
        Assert.Equal(1.0, FunscriptInterpolator.GetNormalizedPosition(1000, SampleActions));
    }

    [Fact]
    public void GetNormalizedPosition_Midpoint_ReturnsInterpolatedNormalized()
    {
        // Midpoint between (0,0) and (1000,100) → pos=50 → 0.5
        Assert.Equal(0.5, FunscriptInterpolator.GetNormalizedPosition(500, SampleActions));
    }

    [Fact]
    public void GetNormalizedPosition_ResultIsAlwaysClamped()
    {
        // Actions with extreme positions should not produce values outside [0,1]
        var extremeActions = new List<FunscriptAction> { new(0, 0), new(1000, 100) };
        var lo = FunscriptInterpolator.GetNormalizedPosition(-9999, extremeActions);
        var hi = FunscriptInterpolator.GetNormalizedPosition(9999, extremeActions);
        Assert.InRange(lo, 0.0, 1.0);
        Assert.InRange(hi, 0.0, 1.0);
    }

    [Fact]
    public void GetNormalizedPosition_SingleAction_ReturnsNormalizedSinglePos()
    {
        var single = new List<FunscriptAction> { new(500, 75) };
        // Any time query → 75 / 100.0 = 0.75
        Assert.Equal(0.75, FunscriptInterpolator.GetNormalizedPosition(0, single));
        Assert.Equal(0.75, FunscriptInterpolator.GetNormalizedPosition(500, single));
        Assert.Equal(0.75, FunscriptInterpolator.GetNormalizedPosition(9999, single));
    }
}
