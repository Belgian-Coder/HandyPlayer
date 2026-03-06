using HandyPlaylistPlayer.Media.Mpv;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class MpvTrackTests
{
    [Fact]
    public void DisplayName_WithAllFields_FormatsCorrectly()
    {
        var track = new MpvTrack("audio", 1, "English Commentary", "en", "aac", false);
        Assert.Equal("Track 1: English Commentary · en · aac", track.DisplayName);
    }

    [Fact]
    public void DisplayName_WithTitleAndLang_OmitsCodec()
    {
        var track = new MpvTrack("audio", 2, "Director's Cut", "ja", "", true);
        Assert.Equal("Track 2: Director's Cut · ja", track.DisplayName);
    }

    [Fact]
    public void DisplayName_WithOnlyCodec_ShowsCodecOnly()
    {
        var track = new MpvTrack("audio", 1, "", "", "opus", false);
        Assert.Equal("Track 1: opus", track.DisplayName);
    }

    [Fact]
    public void DisplayName_WithNoFields_ShowsTrackIdOnly()
    {
        var track = new MpvTrack("sub", 3, "", "", "", false);
        Assert.Equal("Track 3", track.DisplayName);
    }

    [Fact]
    public void DisplayName_WithNullFields_ShowsTrackIdOnly()
    {
        var track = new MpvTrack("audio", 5, null!, null!, null!, false);
        Assert.Equal("Track 5", track.DisplayName);
    }

    [Fact]
    public void DisplayName_WithLangAndCodec_OmitsTitle()
    {
        var track = new MpvTrack("audio", 1, "", "en", "flac", false);
        Assert.Equal("Track 1: en · flac", track.DisplayName);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("sub")]
    [InlineData("video")]
    public void Type_PreservesValue(string type)
    {
        var track = new MpvTrack(type, 1, "", "", "", false);
        Assert.Equal(type, track.Type);
    }

    [Fact]
    public void IsSelected_PreservesValue()
    {
        var selected = new MpvTrack("audio", 1, "", "", "", true);
        var notSelected = new MpvTrack("audio", 2, "", "", "", false);
        Assert.True(selected.IsSelected);
        Assert.False(notSelected.IsSelected);
    }
}
