using Avalonia;
using Avalonia.Media;

namespace HandyPlaylistPlayer.App.Themes;

public enum AppThemeOption
{
    DarkNavy,
    AmoledBlack,
    DarkGray,
    Dracula,
    Slate,
}

public static class AppThemes
{
    // 11 mutable brushes — mutated in-place so DynamicResource bindings update instantly.
    // Initialize() must be called before any controls are created (in OnFrameworkInitializationCompleted)
    // so the mutable instances are anchored as the resource values from the start.
    private static readonly SolidColorBrush _appBg          = new(Color.Parse("#0D0D1A"));
    private static readonly SolidColorBrush _surface        = new(Color.Parse("#1A1A2E"));
    private static readonly SolidColorBrush _cardBg         = new(Color.Parse("#2A2A3E"));
    private static readonly SolidColorBrush _cardBorder     = new(Color.Parse("#3A3A50"));
    private static readonly SolidColorBrush _nowPlaying     = new(Color.Parse("#30FFFFFF"));
    private static readonly SolidColorBrush _rowAlt         = new(Color.Parse("#18FFFFFF"));
    private static readonly SolidColorBrush _selectionBg    = new(Color.Parse("#1A3055"));
    private static readonly SolidColorBrush _selectionHover = new(Color.Parse("#141430"));
    private static readonly SolidColorBrush _textFg         = new(Color.Parse("#E0E0FF"));
    private static readonly SolidColorBrush _highlightText  = new(Color.Parse("#FFFFFF"));
    private static readonly SolidColorBrush _buttonBg       = new(Color.Parse("#252540"));

    /// <summary>
    /// Registers the mutable brush instances in Application.Resources.
    /// Must be called once before any UI elements are created.
    /// </summary>
    public static void Initialize()
    {
        var res = Application.Current!.Resources;
        res["AppBgBrush"]          = _appBg;
        res["SurfaceBrush"]        = _surface;
        res["CardBgBrush"]         = _cardBg;
        res["CardBorderBrush"]     = _cardBorder;
        res["NowPlayingBgBrush"]   = _nowPlaying;
        res["RowAltBrush"]         = _rowAlt;
        res["SelectionBgBrush"]    = _selectionBg;
        res["SelectionHoverBrush"] = _selectionHover;
        // Text color overrides — registered as our keys and as FluentTheme system keys
        // so all TextBlocks in the app pick up the custom foreground color.
        res["TextFgBrush"]                          = _textFg;
        res["HighlightTextBrush"]                   = _highlightText;
        res["SystemControlForegroundBaseHighBrush"] = _textFg;
        res["SystemControlHighlightAltHighBrush"]   = _highlightText;
        // Button background brush — consumed by the App.axaml Button style via DynamicResource
        res["ButtonBgBrush"] = _buttonBg;
    }

    public static AppThemeOption GetByName(string? name) =>
        Enum.TryParse<AppThemeOption>(name, ignoreCase: true, out var option) ? option : AppThemeOption.DarkNavy;

    /// <summary>Returns the default accent hex for a given theme (matches the accentName in Apply()).</summary>
    public static string GetDefaultAccentHex(AppThemeOption theme) => theme switch
    {
        AppThemeOption.AmoledBlack => "#CE93D8", // Purple
        AppThemeOption.DarkGray    => "#4DD0E1", // Teal
        AppThemeOption.Dracula     => "#F48FB1", // Pink
        AppThemeOption.Slate       => "#4FC3F7", // Blue
        _                          => "#4FC3F7", // Blue (DarkNavy)
    };

    /// <summary>
    /// Applies a theme preset by mutating the 6 shared brushes in-place.
    /// Also applies the theme's default accent color (can be overridden afterward by the user's accent setting).
    /// </summary>
    public static void Apply(AppThemeOption theme)
    {
        var (appBg, surface, cardBg, cardBorder, nowPlaying, rowAlt, selBg, selHover, textFg, highlightText, buttonBg, accentName) = theme switch
        {
            AppThemeOption.AmoledBlack => (
                "#000000", "#0A0A0A", "#111111", "#222222", "#20FFFFFF", "#0FFFFFFF",
                "#151530", "#0D0D1A", "#FFFFFF", "#FFFFFF", "#1C1C1C", "Purple"),
            AppThemeOption.DarkGray => (
                "#1A1A1A", "#242424", "#2D2D2D", "#3D3D3D", "#25FFFFFF", "#12FFFFFF",
                "#2A2A40", "#222230", "#EBEBEB", "#FFFFFF", "#2D2D2D", "Teal"),
            AppThemeOption.Dracula => (
                "#282A36", "#21222C", "#313345", "#44475A", "#22BD93FF", "#12BD93FF",
                "#44475A", "#383B49", "#F8F8F2", "#FFFFFF", "#313345", "Pink"),
            AppThemeOption.Slate => (
                "#1A2033", "#202840", "#253047", "#344060", "#25FFFFFF", "#12FFFFFF",
                "#243060", "#1F2A48", "#E0E8FF", "#FFFFFF", "#253047", "Blue"),
            _ => ( // DarkNavy (default)
                "#0D0D1A", "#1A1A2E", "#2A2A3E", "#3A3A50", "#30FFFFFF", "#18FFFFFF",
                "#1A3055", "#141430", "#E0E0FF", "#FFFFFF", "#252540", "Blue"),
        };

        _appBg.Color          = Color.Parse(appBg);
        _surface.Color        = Color.Parse(surface);
        _cardBg.Color         = Color.Parse(cardBg);
        _cardBorder.Color     = Color.Parse(cardBorder);
        _nowPlaying.Color     = Color.Parse(nowPlaying);
        _rowAlt.Color         = Color.Parse(rowAlt);
        _selectionBg.Color    = Color.Parse(selBg);
        _selectionHover.Color = Color.Parse(selHover);
        _textFg.Color         = Color.Parse(textFg);
        _highlightText.Color  = Color.Parse(highlightText);
        _buttonBg.Color       = Color.Parse(buttonBg);

        AccentColors.Apply(AccentColors.GetByName(accentName));
    }

    /// <summary>Returns the current hex values of the three user-customisable colors.</summary>
    public static (string bg, string textFg, string highlightText) GetCurrentColors() => (
        $"#{_appBg.Color.R:X2}{_appBg.Color.G:X2}{_appBg.Color.B:X2}",
        $"#{_textFg.Color.R:X2}{_textFg.Color.G:X2}{_textFg.Color.B:X2}",
        $"#{_highlightText.Color.R:X2}{_highlightText.Color.G:X2}{_highlightText.Color.B:X2}");

    public static string GetCurrentButtonHex() =>
        $"#{_buttonBg.Color.R:X2}{_buttonBg.Color.G:X2}{_buttonBg.Color.B:X2}";

    /// <summary>Applies individual color overrides on top of the currently active theme.
    /// Pass null for any component you do not want to change.</summary>
    public static void ApplyCustomColors(Color? bg, Color? textFg, Color? highlightText, Color? buttonBg = null)
    {
        if (bg.HasValue)            _appBg.Color         = bg.Value;
        if (textFg.HasValue)        _textFg.Color        = textFg.Value;
        if (highlightText.HasValue) _highlightText.Color = highlightText.Value;
        if (buttonBg.HasValue)      _buttonBg.Color      = buttonBg.Value;
    }
}
