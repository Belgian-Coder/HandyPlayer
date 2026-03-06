using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using HandyPlaylistPlayer.App.Themes;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

/// <summary>
/// Tests for the AccentColors mutable-brush mechanism.
///
/// [Fact]                              = pure logic, no Avalonia runtime needed
/// IClassFixture&lt;AvaloniaTestFixture&gt; = tests that need Application.Current
///
/// These verify the foundation of the accent-color system: that Initialize()
/// anchors the correct mutable brush in resources, and that Apply() mutates it
/// in-place so DynamicResource bindings (including the nav ItemContainerTheme)
/// update without needing to re-resolve the resource key.
/// </summary>
public class AccentColorsLogicTests
{
    [Fact]
    public void Options_ContainsAllExpectedAccentNames()
    {
        var names = AccentColors.Options.Select(o => o.Name).ToArray();
        Assert.Contains("Blue",   names);
        Assert.Contains("Purple", names);
        Assert.Contains("Green",  names);
        Assert.Contains("Orange", names);
        Assert.Contains("Pink",   names);
        Assert.Contains("Red",    names);
        Assert.Contains("Gold",   names);
        Assert.Contains("Teal",   names);
    }

    [Fact]
    public void GetByName_ExactMatch_ReturnsMatchingOption()
    {
        var result = AccentColors.GetByName("Gold");
        Assert.Equal("Gold", result.Name);
        Assert.Equal(Color.Parse("#FFD54F"), result.Primary);
    }

    [Fact]
    public void GetByName_CaseInsensitive_ReturnsMatchingOption()
    {
        Assert.Equal("Purple", AccentColors.GetByName("purple").Name);
        Assert.Equal("Teal",   AccentColors.GetByName("TEAL").Name);
    }

    [Fact]
    public void GetByName_UnknownName_ReturnsFallbackFirstOption()
        => Assert.Equal(AccentColors.Options[0], AccentColors.GetByName("Magenta"));

    [Fact]
    public void GetByName_Null_ReturnsFallbackFirstOption()
        => Assert.Equal(AccentColors.Options[0], AccentColors.GetByName(null));

    [Fact]
    public void EachOption_PrimaryAndDarkAreFullyOpaque()
    {
        foreach (var opt in AccentColors.Options)
        {
            Assert.Equal(255, opt.Primary.A);
            Assert.Equal(255, opt.Dark.A);
        }
    }
}

/// <summary>
/// Tests that require a live Application.Current (resource dictionary).
/// The AvaloniaTestFixture starts a headless Avalonia runtime once for the class.
/// </summary>
[Collection("Avalonia")]
public class AccentColorsResourceTests : IClassFixture<AvaloniaTestFixture>
{
    private readonly AvaloniaTestFixture _avalonia;
    public AccentColorsResourceTests(AvaloniaTestFixture avalonia) => _avalonia = avalonia;

    [Fact]
    public void Initialize_RegistersAccentBrushInApplicationResources()
    {
        _avalonia.RunOnUiThread(() =>
        {
            AccentColors.Initialize();
            var found = Application.Current!.Resources.TryGetResource(
                "AccentBrush", ThemeVariant.Default, out var value);

            Assert.True(found, "AccentBrush key must be present after Initialize()");
            Assert.IsType<SolidColorBrush>(value);
        });
    }

    [Fact]
    public void Initialize_RegistersAccentDarkBrushInApplicationResources()
    {
        _avalonia.RunOnUiThread(() =>
        {
            AccentColors.Initialize();
            var found = Application.Current!.Resources.TryGetResource(
                "AccentDarkBrush", ThemeVariant.Default, out var value);

            Assert.True(found);
            Assert.IsType<SolidColorBrush>(value);
        });
    }

    [Fact]
    public void Apply_MutatesRegisteredBrushToExpectedColor()
    {
        _avalonia.RunOnUiThread(() =>
        {
            AccentColors.Initialize();
            var gold = AccentColors.GetByName("Gold");
            AccentColors.Apply(gold);

            Application.Current!.Resources.TryGetResource(
                "AccentBrush", ThemeVariant.Default, out var value);
            var brush = Assert.IsType<SolidColorBrush>(value);
            Assert.Equal(gold.Primary, brush.Color);
        });
    }

    [Fact]
    public void Apply_ReusesTheSameBrushInstance_SoDynamicResourceBindingsUpdate()
    {
        // DynamicResource bindings hold a reference to the brush OBJECT registered
        // by Initialize().  If Apply() replaced the object in the dict rather than
        // mutating it, live bindings would lose their reference and stop updating.
        _avalonia.RunOnUiThread(() =>
        {
            AccentColors.Initialize();
            Application.Current!.Resources.TryGetResource(
                "AccentBrush", ThemeVariant.Default, out var before);

            AccentColors.Apply(AccentColors.GetByName("Purple"));

            Application.Current!.Resources.TryGetResource(
                "AccentBrush", ThemeVariant.Default, out var after);

            Assert.Same(before, after);
        });
    }

    [Fact]
    public void ApplyCustomColor_MutatesBrushToCustomColor()
    {
        _avalonia.RunOnUiThread(() =>
        {
            AccentColors.Initialize();
            var custom = Color.FromArgb(255, 200, 100, 50);
            AccentColors.ApplyCustomColor(custom);

            Application.Current!.Resources.TryGetResource(
                "AccentBrush", ThemeVariant.Default, out var value);
            var brush = Assert.IsType<SolidColorBrush>(value);
            Assert.Equal(custom, brush.Color);
        });
    }

    [Fact]
    public void ApplyCustomColor_DarkVariantIs60PercentBrightness()
    {
        _avalonia.RunOnUiThread(() =>
        {
            AccentColors.Initialize();
            var primary = Color.FromArgb(255, 100, 200, 50);
            var expectedDark = Color.FromArgb(255,
                (byte)(100 * 0.6), (byte)(200 * 0.6), (byte)(50 * 0.6));

            AccentColors.ApplyCustomColor(primary);

            Application.Current!.Resources.TryGetResource(
                "AccentDarkBrush", ThemeVariant.Default, out var value);
            var brush = Assert.IsType<SolidColorBrush>(value);
            Assert.Equal(expectedDark, brush.Color);
        });
    }
}
