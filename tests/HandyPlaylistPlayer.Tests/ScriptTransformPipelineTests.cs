using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Runtime;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class ScriptTransformPipelineTests
{
    [Fact]
    public void Transform_DefaultSettings_ReturnsInputClamped()
    {
        var pipeline = new ScriptTransformPipeline();
        Assert.Equal(50, pipeline.Transform(50, 100));
    }

    [Fact]
    public void Transform_Invert_FlipsPosition()
    {
        var pipeline = new ScriptTransformPipeline
        {
            Settings = new TransformSettings { Invert = true }
        };
        Assert.Equal(80, pipeline.Transform(20, 100));
    }

    [Fact]
    public void Transform_RangeScaling_MapsCorrectly()
    {
        var pipeline = new ScriptTransformPipeline
        {
            Settings = new TransformSettings
            {
                RangeMin = 20, RangeMax = 80
            }
        };
        // 0 -> 20, 100 -> 80, 50 -> 50
        Assert.Equal(20, pipeline.Transform(0, 100));
        pipeline.Reset();
        Assert.Equal(80, pipeline.Transform(100, 100));
        pipeline.Reset();
        Assert.Equal(50, pipeline.Transform(50, 100));
    }

    [Fact]
    public void Transform_ClampsTo0_100()
    {
        var pipeline = new ScriptTransformPipeline
        {
            Settings = new TransformSettings
            {
                RangeMin = -10, RangeMax = 110
            }
        };
        var result = pipeline.Transform(100, 100);
        Assert.InRange(result, 0, 100);
    }

    [Fact]
    public void Reset_ClearsPreviousState()
    {
        var pipeline = new ScriptTransformPipeline();
        pipeline.Transform(80, 100);
        pipeline.Reset();
        var result = pipeline.Transform(50, 200);
        Assert.InRange(result, 0, 100);
    }

    [Fact]
    public void Transform_SpeedLimit_ClampsLargeJumps()
    {
        var pipeline = new ScriptTransformPipeline
        {
            Settings = new TransformSettings
            {
                SpeedLimit = 50,
            }
        };

        pipeline.Transform(0, 100);
        var result = pipeline.Transform(100, 1100);
        Assert.Equal(50, result);
    }

    [Fact]
    public void Transform_SpeedLimit_AllowsSmallMoves()
    {
        var pipeline = new ScriptTransformPipeline
        {
            Settings = new TransformSettings
            {
                SpeedLimit = 100,
            }
        };

        pipeline.Transform(50, 100);
        var result = pipeline.Transform(60, 1100);
        Assert.Equal(60, result);
    }

    [Fact]
    public void Transform_SpeedLimit_WorksInBothDirections()
    {
        var pipeline = new ScriptTransformPipeline
        {
            Settings = new TransformSettings
            {
                SpeedLimit = 20,
            }
        };

        pipeline.Transform(50, 100);
        var result = pipeline.Transform(0, 1100);
        Assert.Equal(30, result);
    }

    [Fact]
    public void Transform_SpeedLimit_NoLimitOnFirstCall()
    {
        var pipeline = new ScriptTransformPipeline
        {
            Settings = new TransformSettings
            {
                SpeedLimit = 10,
            }
        };

        var result = pipeline.Transform(80, 0);
        Assert.Equal(80, result);
    }

    [Fact]
    public void Transform_CombinedTransforms_AppliedInOrder()
    {
        var pipeline = new ScriptTransformPipeline
        {
            Settings = new TransformSettings
            {
                Invert = true,
                RangeMin = 20,
                RangeMax = 80,
            }
        };

        // Input: 20 -> Invert: 80 -> Range scale: 20 + (80/100)*(80-20) = 68
        var result = pipeline.Transform(20, 100);
        Assert.Equal(68, result);
    }

    [Fact]
    public void Transform_InvertedRange_ClampsProperly()
    {
        // RangeMin > RangeMax — should still produce valid output in 0-100
        var pipeline = new ScriptTransformPipeline
        {
            Settings = new TransformSettings
            {
                RangeMin = 80, RangeMax = 20
            }
        };

        var result = pipeline.Transform(50, 100);
        Assert.InRange(result, 0, 100);
    }

    [Fact]
    public void Transform_SpeedLimit_ZeroDeltaTime_NoLimit()
    {
        var pipeline = new ScriptTransformPipeline
        {
            Settings = new TransformSettings { SpeedLimit = 10 }
        };

        pipeline.Transform(0, 100);
        // Same timestamp — no speed limiting should apply
        var result = pipeline.Transform(100, 100);
        Assert.Equal(100, result);
    }

    [Fact]
    public void Transform_BoundaryValues_HandledCorrectly()
    {
        var pipeline = new ScriptTransformPipeline();
        Assert.Equal(0, pipeline.Transform(0, 0));
        pipeline.Reset();
        Assert.Equal(100, pipeline.Transform(100, 0));
    }
}
