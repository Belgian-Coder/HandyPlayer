using HandyPlaylistPlayer.Core.Services;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class FilenameNormalizerTests
{
    private readonly FilenameNormalizer _normalizer = new();

    [Fact]
    public void Normalize_RemovesExtension()
    {
        Assert.Equal("myvideo", _normalizer.Normalize("MyVideo.mp4"));
    }

    [Fact]
    public void Normalize_RemovesBracketGroups()
    {
        Assert.Equal("myvideo", _normalizer.Normalize("MyVideo [1080p] (HQ).mp4"));
    }

    [Fact]
    public void Normalize_RemovesKnownTags()
    {
        Assert.Equal("myvideo", _normalizer.Normalize("MyVideo.1080p.x264.mp4"));
    }

    [Fact]
    public void Normalize_ReplacesUnderscoresAndDashes()
    {
        Assert.Equal("my video title", _normalizer.Normalize("My_Video-Title.mp4"));
    }

    [Fact]
    public void Normalize_CollapsesWhitespace()
    {
        Assert.Equal("my video title", _normalizer.Normalize("My   Video    Title.mp4"));
    }

    [Fact]
    public void Normalize_MatchesVideoAndScript()
    {
        var videoNorm = _normalizer.Normalize("Scene_Name.1080p.x264.mp4");
        var scriptNorm = _normalizer.Normalize("Scene_Name.funscript");
        Assert.Equal(videoNorm, scriptNorm);
    }

    [Fact]
    public void Normalize_HandlesMixedTags()
    {
        Assert.Equal("cool scene", _normalizer.Normalize("Cool.Scene.4k.60fps.hevc.webm"));
    }

    [Fact]
    public void Normalize_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", _normalizer.Normalize(""));
    }
}
