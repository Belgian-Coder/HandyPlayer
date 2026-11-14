using HandyPlaylistPlayer.Core.Services;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class PatternGeneratorTests
{
    [Theory]
    [InlineData(PatternType.Sine)]
    [InlineData(PatternType.Sawtooth)]
    [InlineData(PatternType.Square)]
    [InlineData(PatternType.Triangle)]
    [InlineData(PatternType.Random)]
    public void Generate_AllPatterns_ProducesActions(PatternType type)
    {
        var doc = PatternGenerator.Generate(type, durationMs: 5000);

        Assert.NotEmpty(doc.Actions);
        Assert.All(doc.Actions, a =>
        {
            Assert.InRange(a.Pos, 0, 100);
            Assert.True(a.At >= 0);
        });
        Assert.Equal("1.0", doc.Version);
    }

    [Fact]
    public void Generate_Sine_PositionsSpanRange()
    {
        var doc = PatternGenerator.Generate(PatternType.Sine, frequencyHz: 1.0, durationMs: 2000);

        var positions = doc.Actions.Select(a => a.Pos).ToList();
        Assert.True(positions.Min() <= 5);
        Assert.True(positions.Max() >= 95);
    }

    [Fact]
    public void Generate_Square_AlternatesBetweenMinMax()
    {
        var doc = PatternGenerator.Generate(PatternType.Square, amplitudeMin: 10, amplitudeMax: 90, durationMs: 2000);

        var positions = doc.Actions.Select(a => a.Pos).Distinct().ToList();
        Assert.All(positions, p => Assert.True(p == 10 || p == 90, $"Position {p} is not 10 or 90"));
    }

    [Fact]
    public void Generate_Random_WithSeed_IsReproducible()
    {
        var doc1 = PatternGenerator.Generate(PatternType.Random, seed: 42, durationMs: 1000);
        var doc2 = PatternGenerator.Generate(PatternType.Random, seed: 42, durationMs: 1000);

        Assert.Equal(doc1.Actions.Count, doc2.Actions.Count);
        for (int i = 0; i < doc1.Actions.Count; i++)
        {
            Assert.Equal(doc1.Actions[i].At, doc2.Actions[i].At);
            Assert.Equal(doc1.Actions[i].Pos, doc2.Actions[i].Pos);
        }
    }

    [Fact]
    public void Generate_CustomRange_StaysWithinBounds()
    {
        var doc = PatternGenerator.Generate(PatternType.Sine, amplitudeMin: 20, amplitudeMax: 80, durationMs: 2000);

        Assert.All(doc.Actions, a => Assert.InRange(a.Pos, 20, 80));
    }

    [Fact]
    public void Generate_HighFrequency_ProducesMoreActions()
    {
        var lowFreq = PatternGenerator.Generate(PatternType.Sine, frequencyHz: 0.5, durationMs: 5000);
        var highFreq = PatternGenerator.Generate(PatternType.Sine, frequencyHz: 3.0, durationMs: 5000);

        Assert.True(highFreq.Actions.Count > lowFreq.Actions.Count);
    }

    [Theory]
    [InlineData(PatternType.Sine)]
    [InlineData(PatternType.Sawtooth)]
    [InlineData(PatternType.Square)]
    [InlineData(PatternType.Triangle)]
    public void Generate_ReachesBothExtremes(PatternType type)
    {
        var actions = PatternGenerator.Generate(type, 1.0, 10, 90, 4000).Actions;

        Assert.Equal(10, actions.Min(a => a.Pos));
        Assert.Equal(90, actions.Max(a => a.Pos));
    }

    [Theory]
    [InlineData(PatternType.Sine)]
    [InlineData(PatternType.Sawtooth)]
    [InlineData(PatternType.Square)]
    [InlineData(PatternType.Triangle)]
    [InlineData(PatternType.Random)]
    public void Generate_TimestampsStrictlyIncreasing_WithinDuration(PatternType type)
    {
        var actions = PatternGenerator.Generate(type, 2.5, 0, 100, 3000, seed: 1).Actions;

        Assert.All(actions.Zip(actions.Skip(1)), p => Assert.True(p.Second.At > p.First.At,
            $"{p.First.At} -> {p.Second.At}"));
        Assert.All(actions, a => Assert.InRange(a.At, 0, 3000));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Generate_Triangle_OnePeakPerPeriod(double hz)
    {
        var actions = PatternGenerator.Generate(PatternType.Triangle, hz, 0, 100, 4000).Actions;

        Assert.Equal((int)(4 * hz), actions.Count(a => a.Pos == 100));
    }

    [Fact]
    public void Generate_Sawtooth_RampsUpSlowly_DropsQuickly()
    {
        var actions = PatternGenerator.Generate(PatternType.Sawtooth, 1.0, 0, 100, 3000).Actions;

        // Interpolated position rises through the cycle and is back at min at each period boundary.
        Assert.Equal(0, FunscriptInterpolator.GetPositionAtTime(1000, actions));
        Assert.InRange(FunscriptInterpolator.GetPositionAtTime(1250, actions), 20, 35);
        Assert.InRange(FunscriptInterpolator.GetPositionAtTime(1500, actions), 45, 60);
        Assert.InRange(FunscriptInterpolator.GetPositionAtTime(1850, actions), 90, 100);
        // Exactly one rise and one drop per second.
        Assert.Equal(3, actions.Count(a => a.Pos == 100));
    }

    [Fact]
    public void Generate_Square_HoldsEachLevelForHalfAPeriod()
    {
        var actions = PatternGenerator.Generate(PatternType.Square, 1.0, 10, 90, 2000).Actions;

        // Mid-way through each half period the interpolated position is flat at the level, not ramping.
        Assert.Equal(90, FunscriptInterpolator.GetPositionAtTime(250, actions));
        Assert.Equal(10, FunscriptInterpolator.GetPositionAtTime(750, actions));
        Assert.Equal(90, FunscriptInterpolator.GetPositionAtTime(1250, actions));
        Assert.Equal(10, FunscriptInterpolator.GetPositionAtTime(1750, actions));
    }
}
