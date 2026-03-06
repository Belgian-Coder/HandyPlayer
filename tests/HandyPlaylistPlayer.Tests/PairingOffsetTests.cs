using HandyPlaylistPlayer.Core.Models;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class PairingOffsetTests
{
    [Fact]
    public void Pairing_DefaultOffsetMs_IsZero()
    {
        var pairing = new Pairing();
        Assert.Equal(0, pairing.OffsetMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-250)]
    [InlineData(500)]
    public void Pairing_OffsetMs_CanBeSetToAnyValue(int offset)
    {
        var pairing = new Pairing { OffsetMs = offset };
        Assert.Equal(offset, pairing.OffsetMs);
    }

    [Fact]
    public void Pairing_DefaultConfidence_IsOne()
    {
        var pairing = new Pairing();
        Assert.Equal(1.0, pairing.Confidence);
    }
}
