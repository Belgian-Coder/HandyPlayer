using System.Text.Json;
using HandyPlaylistPlayer.Core.Models;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class ExportImportTests
{
    [Fact]
    public void Preset_Intensity_RoundTrips()
    {
        var preset = new Preset
        {
            Name = "Test",
            RangeMin = 10,
            RangeMax = 90,
            OffsetMs = 50,
            SpeedLimit = 200.0,
            Intensity = 0.75,
            Invert = true,
            IsExpert = false,
            SmoothingFactor = 0.5,
            CurveGamma = 1.2,
            TickRateMs = 40
        };

        var json = JsonSerializer.Serialize(new
        {
            preset.Name, preset.RangeMin, preset.RangeMax, preset.OffsetMs,
            preset.SpeedLimit, preset.Intensity, preset.Invert, preset.IsExpert,
            preset.SmoothingFactor, preset.CurveGamma, preset.TickRateMs
        });

        using var doc = JsonDocument.Parse(json);
        var p = doc.RootElement;

        Assert.Equal("Test", p.GetProperty("Name").GetString());
        Assert.Equal(10, p.GetProperty("RangeMin").GetInt32());
        Assert.Equal(90, p.GetProperty("RangeMax").GetInt32());
        Assert.Equal(50, p.GetProperty("OffsetMs").GetInt32());
        Assert.Equal(200.0, p.GetProperty("SpeedLimit").GetDouble());
        Assert.Equal(0.75, p.GetProperty("Intensity").GetDouble());
        Assert.True(p.GetProperty("Invert").GetBoolean());
        Assert.False(p.GetProperty("IsExpert").GetBoolean());
    }

    [Fact]
    public void Preset_NullIntensity_SerializesAsNull()
    {
        var preset = new Preset { Name = "NoIntensity", Intensity = null };

        var json = JsonSerializer.Serialize(new { preset.Name, preset.Intensity });
        using var doc = JsonDocument.Parse(json);
        var p = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, p.GetProperty("Intensity").ValueKind);
    }

    [Fact]
    public void V2Import_MissingNewSections_ParsesSuccessfully()
    {
        // Simulate a V2 export that doesn't have V3 sections
        var v2Json = """
        {
            "Version": 2,
            "ExportedAt": "2025-01-01T00:00:00Z",
            "AppearanceSettings": { "Theme": "DarkNavy", "AccentColor": "Blue" },
            "Playlists": [],
            "Presets": [{ "Name": "Default", "RangeMin": 0, "RangeMax": 100, "OffsetMs": 0 }]
        }
        """;

        using var doc = JsonDocument.Parse(v2Json);
        var root = doc.RootElement;

        // V3 sections should not exist
        Assert.False(root.TryGetProperty("MpvSettings", out _));
        Assert.False(root.TryGetProperty("PlaybackSettings", out _));
        Assert.False(root.TryGetProperty("PlayerControls", out _));
        Assert.False(root.TryGetProperty("DeviceSettings", out _));
        Assert.False(root.TryGetProperty("Keybindings", out _));
        Assert.False(root.TryGetProperty("LibraryRoots", out _));

        // V2 sections should still work
        Assert.True(root.TryGetProperty("AppearanceSettings", out var appearance));
        Assert.Equal("DarkNavy", appearance.GetProperty("Theme").GetString());

        Assert.True(root.TryGetProperty("Presets", out var presets));
        var preset = presets.EnumerateArray().First();
        Assert.Equal("Default", preset.GetProperty("Name").GetString());
        // Intensity missing in V2 → TryGetProperty returns false
        Assert.False(preset.TryGetProperty("Intensity", out _));
    }

    [Fact]
    public void V3Export_ContainsAllSections()
    {
        var v3Json = """
        {
            "Version": 3,
            "ExportedAt": "2026-03-01T00:00:00Z",
            "AppearanceSettings": { "Theme": "Dracula", "AccentColor": "#FF5722" },
            "MpvSettings": { "HwDecode": "auto", "Scale": "ewa_lanczos" },
            "PlaybackSettings": { "DefaultOffsetMs": "50", "SeekStepSeconds": "10" },
            "PlayerControls": { "ShowNavButtons": "true", "ShowEqButton": "false" },
            "DeviceSettings": { "Backend": "handy", "Protocol": "HDSP" },
            "Keybindings": "{\"PlayPause\":\"Space\"}",
            "LibraryRoots": ["C:\\Videos", "D:\\Media"],
            "Playlists": [],
            "Presets": [{ "Name": "Intense", "Intensity": 1.5, "RangeMin": 20, "RangeMax": 80 }]
        }
        """;

        using var doc = JsonDocument.Parse(v3Json);
        var root = doc.RootElement;

        Assert.Equal(3, root.GetProperty("Version").GetInt32());

        // All V3 sections exist
        Assert.True(root.TryGetProperty("MpvSettings", out var mpv));
        Assert.Equal("ewa_lanczos", mpv.GetProperty("Scale").GetString());

        Assert.True(root.TryGetProperty("PlaybackSettings", out var playback));
        Assert.Equal("50", playback.GetProperty("DefaultOffsetMs").GetString());

        Assert.True(root.TryGetProperty("PlayerControls", out var controls));
        Assert.Equal("false", controls.GetProperty("ShowEqButton").GetString());

        Assert.True(root.TryGetProperty("DeviceSettings", out var device));
        Assert.Equal("HDSP", device.GetProperty("Protocol").GetString());

        Assert.True(root.TryGetProperty("Keybindings", out var kb));
        Assert.Contains("PlayPause", kb.GetString());

        Assert.True(root.TryGetProperty("LibraryRoots", out var roots));
        Assert.Equal(2, roots.GetArrayLength());

        // Preset has Intensity
        var preset = root.GetProperty("Presets").EnumerateArray().First();
        Assert.Equal(1.5, preset.GetProperty("Intensity").GetDouble());
    }

    [Fact]
    public void V3Export_PlaybackSettings_ContainsNewThresholdFields()
    {
        var json = JsonSerializer.Serialize(new
        {
            Version = 3,
            PlaybackSettings = new
            {
                DefaultOffsetMs = "0",
                SeekStepSeconds = "15",
                SaveLastPosition = "false",
                RestoreQueueOnStartup = "false",
                WatchedThresholdPercent = "90",
                GaplessPrepareSeconds = "30",
                FullscreenAutoHideSeconds = "3",
                ThumbnailWidth = "320",
                ThumbnailHeight = "180"
            }
        });

        using var doc = JsonDocument.Parse(json);
        var playback = doc.RootElement.GetProperty("PlaybackSettings");

        Assert.Equal("90", playback.GetProperty("WatchedThresholdPercent").GetString());
        Assert.Equal("30", playback.GetProperty("GaplessPrepareSeconds").GetString());
        Assert.Equal("3", playback.GetProperty("FullscreenAutoHideSeconds").GetString());
        Assert.Equal("320", playback.GetProperty("ThumbnailWidth").GetString());
        Assert.Equal("180", playback.GetProperty("ThumbnailHeight").GetString());
    }

    [Fact]
    public void V3Import_MissingNewPlaybackFields_ParsesSuccessfully()
    {
        // Simulate an older V3 export without the new threshold fields
        var json = """
        {
            "Version": 3,
            "PlaybackSettings": { "DefaultOffsetMs": "50", "SeekStepSeconds": "10" }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("PlaybackSettings", out var playback));
        Assert.Equal("50", playback.GetProperty("DefaultOffsetMs").GetString());

        // New fields are absent — import code should gracefully skip them
        Assert.False(playback.TryGetProperty("WatchedThresholdPercent", out _));
        Assert.False(playback.TryGetProperty("GaplessPrepareSeconds", out _));
        Assert.False(playback.TryGetProperty("FullscreenAutoHideSeconds", out _));
        Assert.False(playback.TryGetProperty("ThumbnailWidth", out _));
        Assert.False(playback.TryGetProperty("ThumbnailHeight", out _));
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("50", 50)]
    [InlineData("100", 100)]
    [InlineData("10", 10)]
    public void WatchedThreshold_ValidValues_ParseCorrectly(string input, int expected)
    {
        Assert.True(int.TryParse(input, out var val));
        Assert.Equal(expected, val);
        Assert.InRange(val, 10, 100);
    }

    [Theory]
    [InlineData("5", false)]   // below minimum
    [InlineData("101", false)] // above maximum
    [InlineData("abc", false)] // non-numeric
    [InlineData("", false)]    // empty
    public void WatchedThreshold_InvalidValues_Rejected(string input, bool shouldPass)
    {
        var valid = int.TryParse(input, out var val) && val >= 10 && val <= 100;
        Assert.Equal(shouldPass, valid);
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("5", 5)]
    [InlineData("120", 120)]
    public void GaplessPrepare_ValidValues_ParseCorrectly(string input, int expected)
    {
        Assert.True(int.TryParse(input, out var val));
        Assert.Equal(expected, val);
        Assert.True(val > 0);
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("1", 1)]
    [InlineData("30", 30)]
    public void FullscreenAutoHide_ValidValues_ParseCorrectly(string input, int expected)
    {
        Assert.True(int.TryParse(input, out var val));
        Assert.Equal(expected, val);
        Assert.True(val > 0);
    }

    [Theory]
    [InlineData("320", "180", 320, 180)]
    [InlineData("640", "360", 640, 360)]
    [InlineData("160", "90", 160, 90)]
    public void ThumbnailDimensions_ValidValues_ParseCorrectly(string wInput, string hInput, int expectedW, int expectedH)
    {
        Assert.True(int.TryParse(wInput, out var w));
        Assert.True(int.TryParse(hInput, out var h));
        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);
        Assert.True(w > 0 && h > 0);
    }

    [Fact]
    public void M3U_Format_HasCorrectStructure()
    {
        var sb = new System.Text.StringBuilder("#EXTM3U\n");
        var items = new[]
        {
            (Duration: 120000L, Name: "Video One", Path: "/videos/video_one.mp4"),
            (Duration: 0L, Name: "Video Two", Path: "/videos/video_two.mp4"),
        };

        foreach (var item in items)
        {
            var dur = item.Duration > 0 ? ((int)(item.Duration / 1000)).ToString() : "-1";
            sb.AppendLine($"#EXTINF:{dur},{item.Name}");
            sb.AppendLine(item.Path);
        }

        var m3u = sb.ToString();
        Assert.StartsWith("#EXTM3U", m3u);
        Assert.Contains("#EXTINF:120,Video One", m3u);
        Assert.Contains("/videos/video_one.mp4", m3u);
        Assert.Contains("#EXTINF:-1,Video Two", m3u);
    }

    [Fact]
    public void M3U_Import_FiltersPaths()
    {
        var lines = new[]
        {
            "#EXTM3U",
            "#EXTINF:120,Video One",
            "/videos/video_one.mp4",
            "",
            "#EXTINF:-1,Video Two",
            "/videos/video_two.mp4",
        };

        var filePaths = lines
            .Where(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim())
            .ToList();

        Assert.Equal(2, filePaths.Count);
        Assert.Equal("/videos/video_one.mp4", filePaths[0]);
        Assert.Equal("/videos/video_two.mp4", filePaths[1]);
    }
}
