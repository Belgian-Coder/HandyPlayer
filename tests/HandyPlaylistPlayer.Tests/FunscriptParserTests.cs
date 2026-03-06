using System.Text;
using HandyPlaylistPlayer.Core.Services;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class FunscriptParserTests
{
    private readonly FunscriptParser _parser = new();

    [Fact]
    public async Task ParseAsync_ValidJson_ReturnsDocument()
    {
        var json = """
            {
                "version": "1.0",
                "inverted": false,
                "range": 90,
                "actions": [
                    {"at": 100, "pos": 0},
                    {"at": 500, "pos": 100},
                    {"at": 1000, "pos": 50}
                ]
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var doc = await _parser.ParseAsync(stream);

        Assert.Equal("1.0", doc.Version);
        Assert.False(doc.Inverted);
        Assert.Equal(90, doc.Range);
        Assert.Equal(3, doc.Actions.Count);
        Assert.Equal(100, doc.Actions[0].At);
        Assert.Equal(0, doc.Actions[0].Pos);
        Assert.Equal(50, doc.Actions[2].Pos);
    }

    [Fact]
    public async Task ParseAsync_SortsActionsByTime()
    {
        var json = """{"actions": [{"at": 500, "pos": 50}, {"at": 100, "pos": 0}, {"at": 300, "pos": 100}]}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var doc = await _parser.ParseAsync(stream);

        Assert.Equal(100, doc.Actions[0].At);
        Assert.Equal(300, doc.Actions[1].At);
        Assert.Equal(500, doc.Actions[2].At);
    }

    [Fact]
    public async Task ParseAsync_ClampsPositionValues()
    {
        var json = """{"actions": [{"at": 100, "pos": -10}, {"at": 200, "pos": 150}]}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var doc = await _parser.ParseAsync(stream);

        Assert.Equal(0, doc.Actions[0].Pos);
        Assert.Equal(100, doc.Actions[1].Pos);
    }

    [Fact]
    public async Task ParseAsync_FilterNegativeTime()
    {
        var json = """{"actions": [{"at": -100, "pos": 50}, {"at": 100, "pos": 0}]}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var doc = await _parser.ParseAsync(stream);

        Assert.Single(doc.Actions);
        Assert.Equal(100, doc.Actions[0].At);
    }

    [Fact]
    public async Task ParseAsync_EmptyActions_ReturnsEmptyList()
    {
        var json = """{"actions": []}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var doc = await _parser.ParseAsync(stream);

        Assert.Empty(doc.Actions);
        Assert.Equal(0, doc.DurationMs);
    }

    [Fact]
    public async Task ParseAsync_DefaultValues_WhenMissing()
    {
        var json = """{"actions": [{"at": 100, "pos": 50}]}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var doc = await _parser.ParseAsync(stream);

        Assert.Equal("1.0", doc.Version);
        Assert.False(doc.Inverted);
        Assert.Equal(100, doc.Range);
    }

    [Fact]
    public async Task ParseAsync_ClampsRangeValue()
    {
        var json = """{"range": 200, "actions": [{"at": 100, "pos": 50}]}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var doc = await _parser.ParseAsync(stream);

        Assert.Equal(100, doc.Range);
    }
}
