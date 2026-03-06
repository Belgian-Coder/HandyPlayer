using HandyPlaylistPlayer.Core.Features.Device.UpdateTransformSettings;
using HandyPlaylistPlayer.Core.Features.Library.AddLibraryRoot;
using HandyPlaylistPlayer.Core.Features.PatternMode.GeneratePattern;
using HandyPlaylistPlayer.Core.Features.Playback.LoadMedia;
using HandyPlaylistPlayer.Core.Features.Playback.Seek;
using HandyPlaylistPlayer.Core.Features.Playlists.CreatePlaylist;
using HandyPlaylistPlayer.Core.Features.Presets.CreatePreset;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Services;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class CreatePlaylistValidatorTests
{
    private readonly CreatePlaylistValidator _validator = new();

    [Fact]
    public void ValidCommand_Passes()
    {
        var result = _validator.Validate(new CreatePlaylistCommand("My Playlist", "static"));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyName_Fails(string? name)
    {
        var result = _validator.Validate(new CreatePlaylistCommand(name!, "static"));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("name"));
    }

    [Fact]
    public void NameTooLong_Fails()
    {
        var result = _validator.Validate(new CreatePlaylistCommand(new string('A', 101), "static"));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("100"));
    }

    [Fact]
    public void InvalidType_Fails()
    {
        var result = _validator.Validate(new CreatePlaylistCommand("Test", "invalid"));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("type"));
    }

    [Theory]
    [InlineData("static")]
    [InlineData("folder")]
    [InlineData("smart")]
    public void ValidTypes_Pass(string type)
    {
        var result = _validator.Validate(new CreatePlaylistCommand("Test", type));
        Assert.True(result.IsValid);
    }
}

public class CreatePresetValidatorTests
{
    private readonly CreatePresetValidator _validator = new();

    [Fact]
    public void ValidPreset_Passes()
    {
        var result = _validator.Validate(new CreatePresetCommand(new Preset { Name = "Default" }));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyName_Fails(string? name)
    {
        var result = _validator.Validate(new CreatePresetCommand(new Preset { Name = name! }));
        Assert.False(result.IsValid);
    }
}

public class AddLibraryRootValidatorTests
{
    private readonly AddLibraryRootValidator _validator = new();

    [Fact]
    public void ValidPath_Passes()
    {
        var result = _validator.Validate(new AddLibraryRootCommand("/home/user/videos"));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPath_Fails(string? path)
    {
        var result = _validator.Validate(new AddLibraryRootCommand(path!));
        Assert.False(result.IsValid);
    }
}

public class LoadMediaValidatorTests
{
    private readonly LoadMediaValidator _validator = new();

    [Fact]
    public void ValidPath_Passes()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var result = _validator.Validate(new LoadMediaCommand(tempFile, null));
            Assert.True(result.IsValid);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void NonExistentFile_Fails()
    {
        var result = _validator.Validate(new LoadMediaCommand("/nonexistent/video.mp4", null));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("not found"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyVideoPath_Fails(string? path)
    {
        var result = _validator.Validate(new LoadMediaCommand(path!, null));
        Assert.False(result.IsValid);
    }
}

public class SeekValidatorTests
{
    private readonly SeekValidator _validator = new();

    [Fact]
    public void ValidPosition_Passes()
    {
        var result = _validator.Validate(new SeekCommand(5000));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ZeroPosition_Passes()
    {
        var result = _validator.Validate(new SeekCommand(0));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void NegativePosition_Fails()
    {
        var result = _validator.Validate(new SeekCommand(-1));
        Assert.False(result.IsValid);
    }
}

public class UpdateTransformSettingsValidatorTests
{
    private readonly UpdateTransformSettingsValidator _validator = new();

    private static TransformSettings ValidSettings() => new()
    {
        RangeMin = 0,
        RangeMax = 100
    };

    [Fact]
    public void ValidSettings_Passes()
    {
        var result = _validator.Validate(new UpdateTransformSettingsCommand(ValidSettings()));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void RangeMinNegative_Fails()
    {
        var s = ValidSettings();
        s.RangeMin = -1;
        var result = _validator.Validate(new UpdateTransformSettingsCommand(s));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void RangeMaxOver100_Fails()
    {
        var s = ValidSettings();
        s.RangeMax = 101;
        var result = _validator.Validate(new UpdateTransformSettingsCommand(s));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void RangeMinExceedsMax_Fails()
    {
        var s = ValidSettings();
        s.RangeMin = 80;
        s.RangeMax = 20;
        var result = _validator.Validate(new UpdateTransformSettingsCommand(s));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void SpeedLimitNegative_Fails()
    {
        var s = ValidSettings();
        s.SpeedLimit = -5;
        var result = _validator.Validate(new UpdateTransformSettingsCommand(s));
        Assert.False(result.IsValid);
    }

}

public class GeneratePatternValidatorTests
{
    private readonly GeneratePatternValidator _validator = new();

    [Fact]
    public void ValidCommand_Passes()
    {
        var result = _validator.Validate(new GeneratePatternCommand(PatternType.Sine, 1.0, 0, 100, 60000));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ZeroFrequency_Fails()
    {
        var result = _validator.Validate(new GeneratePatternCommand(PatternType.Sine, 0, 0, 100, 60000));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void NegativeDuration_Fails()
    {
        var result = _validator.Validate(new GeneratePatternCommand(PatternType.Sine, 1.0, 0, 100, -1));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void AmplitudeMinOutOfRange_Fails()
    {
        var result = _validator.Validate(new GeneratePatternCommand(PatternType.Sine, 1.0, -1, 100, 60000));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void AmplitudeMaxOutOfRange_Fails()
    {
        var result = _validator.Validate(new GeneratePatternCommand(PatternType.Sine, 1.0, 0, 101, 60000));
        Assert.False(result.IsValid);
    }
}
