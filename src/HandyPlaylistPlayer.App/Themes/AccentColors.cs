using Avalonia;
using Avalonia.Media;

namespace HandyPlaylistPlayer.App.Themes;

public record AccentColorOption(string Name, Color Primary, Color Dark)
{
    /// <summary>Pre-built brush for AXAML compiled-binding use (Border.Background etc.).</summary>
    public Avalonia.Media.IBrush PrimaryBrush { get; } = new SolidColorBrush(Primary);
}

public static class AccentColors
{
    public static readonly AccentColorOption[] Options =
    [
        new("Blue",   Color.Parse("#4FC3F7"), Color.Parse("#0288D1")),
        new("Purple", Color.Parse("#CE93D8"), Color.Parse("#7B1FA2")),
        new("Green",  Color.Parse("#81C784"), Color.Parse("#388E3C")),
        new("Orange", Color.Parse("#FFB74D"), Color.Parse("#F57C00")),
        new("Pink",   Color.Parse("#F48FB1"), Color.Parse("#C2185B")),
        new("Red",    Color.Parse("#EF5350"), Color.Parse("#D32F2F")),
        new("Gold",   Color.Parse("#FFD54F"), Color.Parse("#F9A825")),
        new("Teal",   Color.Parse("#4DD0E1"), Color.Parse("#00838F")),
    ];

    // Mutable brushes registered in Application.Resources at startup.
    // Initialized lazily in Initialize() (which runs on the UI thread) rather than
    // as static field initializers, because SolidColorBrush is an AvaloniaObject
    // whose constructor calls VerifyAccess() and will throw on non-UI threads.
    // Apply() mutates Color in-place so all DynamicResource bindings update instantly
    // without needing to replace the brush object in the resource dictionary.
    private static SolidColorBrush? _primaryBrush;
    private static SolidColorBrush? _darkBrush;

    // FluentTheme's ListBoxItem template reads these three resource keys for
    // the selection highlight background (:selected, :selected:pointerover, :selected:pressed).
    // Overriding them globally here means all ListBox controls in the app automatically
    // reflect the current accent without per-ListBox ItemContainerTheme hacks.
    private static SolidColorBrush? _listAccentLow;    // :selected (unfocused)
    private static SolidColorBrush? _listAccentMedium; // :selected:pointerover
    private static SolidColorBrush? _listAccentHigh;   // :selected:pressed

    /// <summary>
    /// Registers the shared brush instances in Application.Resources.
    /// Must be called once before any UI elements are created (in OnFrameworkInitializationCompleted)
    /// so DynamicResource bindings are anchored to these instances from the start.
    /// </summary>
    public static void Initialize()
    {
        _primaryBrush    ??= new SolidColorBrush(Color.Parse("#4FC3F7"));
        _darkBrush       ??= new SolidColorBrush(Color.Parse("#0288D1"));
        _listAccentLow   ??= new SolidColorBrush(Color.Parse("#4FC3F7"));
        _listAccentMedium ??= new SolidColorBrush(Color.Parse("#4FC3F7"));
        _listAccentHigh  ??= new SolidColorBrush(Color.Parse("#0288D1"));
        var res = Application.Current!.Resources;
        res["AccentBrush"]     = _primaryBrush;
        res["AccentDarkBrush"] = _darkBrush;
        res["SystemControlHighlightListAccentLowBrush"]    = _listAccentLow;
        res["SystemControlHighlightListAccentMediumBrush"] = _listAccentMedium;
        res["SystemControlHighlightListAccentHighBrush"]   = _listAccentHigh;
    }

    public static AccentColorOption GetByName(string? name) =>
        Options.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Options[0];

    public static void Apply(AccentColorOption accent)
    {
        _primaryBrush!.Color     = accent.Primary;
        _darkBrush!.Color        = accent.Dark;
        _listAccentLow!.Color    = accent.Primary;
        _listAccentMedium!.Color = accent.Primary;
        _listAccentHigh!.Color   = accent.Dark;
        var res = Application.Current!.Resources;
        res["AccentColor"]     = accent.Primary;
        res["AccentColorDark"] = accent.Dark;
    }

    /// <summary>
    /// Applies a fully custom color. Automatically generates a 60%-brightness dark variant
    /// used for hover/pressed states.
    /// </summary>
    public static void ApplyCustomColor(Color primary)
    {
        var dark = Color.FromArgb(primary.A,
            (byte)(primary.R * 0.6),
            (byte)(primary.G * 0.6),
            (byte)(primary.B * 0.6));
        _primaryBrush!.Color     = primary;
        _darkBrush!.Color        = dark;
        _listAccentLow!.Color    = primary;
        _listAccentMedium!.Color = primary;
        _listAccentHigh!.Color   = dark;
        var res = Application.Current!.Resources;
        res["AccentColor"]     = primary;
        res["AccentColorDark"] = dark;
    }
}
