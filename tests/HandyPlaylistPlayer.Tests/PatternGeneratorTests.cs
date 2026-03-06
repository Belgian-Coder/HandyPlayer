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
}
