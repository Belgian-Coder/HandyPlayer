using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandyPlaylistPlayer.App.Themes;
using HandyPlaylistPlayer.Core;
using HandyPlaylistPlayer.Core.Dispatching;
using IDispatcher = HandyPlaylistPlayer.Core.Dispatching.IDispatcher;
using HandyPlaylistPlayer.Core.Features.Library.AddLibraryRoot;
using HandyPlaylistPlayer.Core.Features.Library.GetLibraryRoots;
using HandyPlaylistPlayer.Core.Features.Library.RemoveLibraryRoot;
using HandyPlaylistPlayer.Core.Features.Library.ScanLibrary;
using HandyPlaylistPlayer.Core.Features.Settings.GetSetting;
using HandyPlaylistPlayer.Core.Features.Settings.SaveSetting;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Devices.HandyApi;
using HandyPlaylistPlayer.Media.Mpv;
using HandyPlaylistPlayer.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.App.ViewModels;

public record NamedColor(string Name, string Hex);

public partial class SettingsViewModel : ObservableObject
{
    private readonly IDispatcher _dispatcher;
    private readonly HandyApiClient _handyClient;
    private readonly PlayerViewModel _playerViewModel;
    private readonly MpvMediaPlayerAdapter _mpvAdapter;
    private readonly DatabaseConfig _dbConfig;
    private readonly IPlaylistRepository _playlistRepo;
    private readonly IPresetRepository _presetRepo;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty] private string _statusText = "Settings";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHandyBackend))]
    private int _selectedBackendIndex; // 0 = Handy, 1 = Intiface
    [ObservableProperty] private string _connectionKey = string.Empty;
    [ObservableProperty] private string _intifaceUrl = "ws://localhost:12345";

    // Handy protocol: 0 = HDSP (default, streaming), 1 = HSSP (server-sync)
    [ObservableProperty] private int _selectedProtocolIndex;

    // Device info (populated when connected)
    [ObservableProperty] private string _deviceInfoText = "Not connected";
    [ObservableProperty] private string _deviceStatusText = "";
    [ObservableProperty] private string _deviceSlideText = "";
    [ObservableProperty] private bool _isDeviceConnected;

    // Default sync offset (ms) for HDSP latency compensation
    [ObservableProperty] private int _defaultOffsetMs = -50;

    // Arrow key seek step in seconds
    [ObservableProperty] private int _seekStepSeconds = 15;

    // Sync calibration
    [ObservableProperty] private bool _isCalibrationRunning;
    [ObservableProperty] private double _calibrationIndicatorTop = 90; // 0=top, 180=bottom
    [ObservableProperty] private string _calibrationStatusText = "Connect device, then click Start.";
    private DispatcherTimer? _calibrationTimer;
    private Stopwatch? _calibrationStopwatch;
    private bool _calibrationGoingUp;
    private const int CalibrationCycleMs = 3000; // 3s per full up-down cycle
    private const int CalibrationHalfCycleMs = CalibrationCycleMs / 2;

    // Theme palette — index matches ComboBox order: 0=DarkNavy, 1=AmoledBlack, 2=DarkGray, 3=Dracula, 4=Slate
    [ObservableProperty] private int _selectedThemeIndex = 0;

    // Accent color (internal state; synced with SelectedAccentColor)
    [ObservableProperty] private string _customAccentHex = "#4FC3F7";

    // Custom theme colors — synced with ListBox selections
    [ObservableProperty] private string _customBgHex            = "#0D0D1A";
    [ObservableProperty] private string _customTextHex          = "#E0E0FF";
    [ObservableProperty] private string _customHighlightTextHex = "#FFFFFF";
    [ObservableProperty] private string _customButtonHex        = "#252540";

    // Search text for each color picker ListBox (filters by name or hex, case-insensitive)
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FilteredAccentColors))]    private string _accentSearch    = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FilteredBgColors))]        private string _bgSearch        = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FilteredTextColors))]      private string _textSearch      = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FilteredHighlightColors))] private string _highlightSearch = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FilteredButtonColors))]    private string _buttonSearch    = "";

    // Selected color in each ListBox — applied immediately on change
    [ObservableProperty] private NamedColor? _selectedAccentColor;
    [ObservableProperty] private NamedColor? _selectedBgColor;
    [ObservableProperty] private NamedColor? _selectedTextColor;
    [ObservableProperty] private NamedColor? _selectedHighlightColor;
    [ObservableProperty] private NamedColor? _selectedButtonColor;
    private bool _syncingColors;

    // MPV engine settings (some apply immediately, others require restart)
    [ObservableProperty] private bool   _mpvHardwareDecode = true;
    [ObservableProperty] private int    _mpvGpuApiIndex = 0;   // 0=auto,1=d3d11,2=vulkan,3=opengl
    [ObservableProperty] private bool   _mpvDeband = false;
    [ObservableProperty] private int    _mpvDemuxerCacheMb = 150;

    // Unified upscaling: 0-4 = standard scale filter, 5 = AI mode (shows shader sub-selector)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAiUpscalingMode))]
    [NotifyPropertyChangedFor(nameof(UpscalingDescription))]
    private int _mpvScaleIndex = 0;
    private int _lastStandardScaleIndex = 0; // remembered when entering AI mode

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpscalingDescription))]
    private int _mpvShaderPresetIndex = 0;  // GlslShaderPreset index; 0=None

    [ObservableProperty] private bool   _mpvLoudnorm = false;
    [ObservableProperty] private bool   _mpvIccProfile = false;
    [ObservableProperty] private int    _mpvToneMappingIndex = 0; // 0=auto,1=bt.2446a,2=hable,3=reinhard,4=clip
    [ObservableProperty] private int    _mpvVolumeMax = 100;
    [ObservableProperty] private string _detectedHwDecoder = "Detecting...";
    // VO backend (restart required)
    [ObservableProperty] private int    _mpvVoBackendIndex = 0;     // 0=gpu, 1=gpu-next

    // Additional MPV settings
    [ObservableProperty] private bool _mpvVideoSync       = false;
    [ObservableProperty] private bool _mpvDither          = true;
    [ObservableProperty] private bool _mpvSubAuto         = true;
    [ObservableProperty] private bool _mpvPitchCorrection = true;
    [ObservableProperty] private bool _saveLastPosition        = false;
    [ObservableProperty] private bool _restoreQueueOnStartup   = false;

    // Configurable thresholds
    [ObservableProperty] private int _watchedThresholdPercent    = 90;
    [ObservableProperty] private int _gaplessPrepareSeconds      = 30;
    [ObservableProperty] private int _fullscreenAutoHideSeconds  = 3;
    [ObservableProperty] private int _thumbnailWidth             = 320;
    [ObservableProperty] private int _thumbnailHeight            = 180;

    // Player control visibility
    [ObservableProperty] private bool _showNavButtons    = true;
    [ObservableProperty] private bool _showFullscreenBtn = true;
    [ObservableProperty] private bool _showAbLoopButtons = true;
    [ObservableProperty] private bool _showSpeedControls = true;
    [ObservableProperty] private bool _showQueueButton   = true;
    [ObservableProperty] private bool _showOverridesBtn  = true;
    [ObservableProperty] private bool _showEqButton      = true;
    [ObservableProperty] private bool _showTracksButton  = true;
    [ObservableProperty] private bool _showVolumeControl = true;

    /// <summary>True when the scale ComboBox is set to "AI Upscaling →" (index 5).</summary>
    public bool IsAiUpscalingMode => MpvScaleIndex == 5;

    public string UpscalingDescription => IsAiUpscalingMode
        ? MpvShaderPresetIndex switch
        {
            0  => "Reverts to the selected standard filter.",
            1  => "CAS: Contrast-adaptive sharpening. Adds crispness without upscaling. Minimal GPU cost, any GPU.",
            2  => "FSR 1.0: AMD FidelityFX spatial upscaling + sharpening. Best when video is much smaller than your display. Any GPU.",
            3  => "NIS: 6-tap Lanczos + adaptive sharpening. Slightly sharper edges than FSR. Any GPU.",
            4  => "RAVU Lite: CNN prescaler for real-world video. Any GPU. Only activates when upscaling.",
            5  => "FSRCNNX: Best-quality CNN upscaler for live-action. Only activates at ≥1.5× upscale ratio — if your video resolution is close to your display resolution, GPU impact will be low.",
            6  => "nlmeans: Adaptive denoising and sharpening. Reduces compression grain. Any GPU.",
            7  => "Anime4K Fast (Mode C): Upscale-only for animated content. Mid-range GPU. Not for live-action.",
            8  => "Anime4K Quality (Mode A): Restore + upscale for anime. High-end GPU required. Not for live-action.",
            9  => "MetalFX Spatial: Apple Metal hardware upscaling. Requires macOS and GPU-Next renderer.",
            10 => "Auto Fast: nlmeans denoising → FSR spatial upscale → CAS sharpen. Good quality/performance balance for all content. Any GPU.",
            11 => "Auto Quality: nlmeans denoising → FSRCNNX CNN upscale → CAS sharpen. Best combined result for all content. Mid-range GPU recommended.",
            _  => ""
        }
        : MpvScaleIndex switch
        {
            1 => "Bilinear: Fastest filter, lowest quality. Use only on very low-end hardware.",
            2 => "Lanczos: Sharp edges with minimal ringing. Good general-purpose upscaler.",
            3 => "EWA Lanczos: Highest-quality standard filter. Heavier GPU load.",
            4 => "Mitchell: Smooth bicubic. Good balance between sharpness and softness.",
            _ => "Spline36: Balanced quality and performance. Best general-purpose upscaler. Default."
        };

    public bool IsHandyBackend => SelectedBackendIndex == 0;
    private string SelectedBackend => SelectedBackendIndex == 0 ? BackendTypes.HandyApi : BackendTypes.Intiface;

    /// <summary>True only on macOS — MetalFX Spatial is unavailable on other platforms.</summary>
    public bool IsMetalFxSupported { get; } = OperatingSystem.IsMacOS();

    public ObservableCollection<LibraryRoot> LibraryRoots { get; } = [];
    public ObservableCollection<KeybindingEntry> Keybindings { get; } = [];

    [ObservableProperty] private KeybindingEntry? _editingKeybinding;
    [ObservableProperty] private bool _isCapturingKey;
    private KeybindingMap _keybindingMap = new();

    /// <summary>Called from code-behind when a key is pressed during capture mode.</summary>
    public Func<Window?>? GetWindow { get; set; }

    private static readonly NamedColor[] _allPresetColors =
    [
        // Reds
        new("Red", "#FF0000"),
        new("Dark Red", "#8B0000"),
        new("Crimson", "#DC143C"),
        new("Firebrick", "#B22222"),
        new("Indian Red", "#CD5C5C"),
        new("Light Coral", "#F08080"),
        new("Salmon", "#FA8072"),
        new("Dark Salmon", "#E9967A"),
        new("Light Salmon", "#FFA07A"),
        new("Tomato", "#FF6347"),
        new("Coral", "#FF7F50"),
        // Oranges
        new("Orange Red", "#FF4500"),
        new("Dark Orange", "#FF8C00"),
        new("Orange", "#FFA500"),
        // Yellows
        new("Gold", "#FFD700"),
        new("Yellow", "#FFFF00"),
        new("Light Yellow", "#FFFFE0"),
        new("Lemon Chiffon", "#FFFACD"),
        new("Goldenrod", "#DAA520"),
        new("Dark Goldenrod", "#B8860B"),
        new("Khaki", "#F0E68C"),
        new("Yellow Green", "#9ACD32"),
        // Greens
        new("Lime", "#00FF00"),
        new("Lime Green", "#32CD32"),
        new("Lawn Green", "#7CFC00"),
        new("Chartreuse", "#7FFF00"),
        new("Green", "#008000"),
        new("Dark Green", "#006400"),
        new("Forest Green", "#228B22"),
        new("Sea Green", "#2E8B57"),
        new("Medium Sea Green", "#3CB371"),
        new("Spring Green", "#00FF7F"),
        new("Medium Spring Green", "#00FA9A"),
        new("Olive", "#808000"),
        new("Olive Drab", "#6B8E23"),
        new("Dark Olive Green", "#556B2F"),
        // Teals & Cyans
        new("Teal", "#008080"),
        new("Dark Cyan", "#008B8B"),
        new("Cyan", "#00FFFF"),
        new("Light Cyan", "#E0FFFF"),
        new("Aquamarine", "#7FFFD4"),
        new("Medium Aquamarine", "#66CDAA"),
        new("Turquoise", "#40E0D0"),
        new("Medium Turquoise", "#48D1CC"),
        new("Dark Turquoise", "#00CED1"),
        new("Cadet Blue", "#5F9EA0"),
        // Blues
        new("Light Blue", "#ADD8E6"),
        new("Powder Blue", "#B0E0E6"),
        new("Sky Blue", "#87CEEB"),
        new("Light Sky Blue", "#87CEFA"),
        new("Deep Sky Blue", "#00BFFF"),
        new("Dodger Blue", "#1E90FF"),
        new("Cornflower Blue", "#6495ED"),
        new("Steel Blue", "#4682B4"),
        new("Light Steel Blue", "#B0C4DE"),
        new("Royal Blue", "#4169E1"),
        new("Blue", "#0000FF"),
        new("Medium Blue", "#0000CD"),
        new("Dark Blue", "#00008B"),
        new("Navy", "#000080"),
        new("Midnight Blue", "#191970"),
        new("Slate Blue", "#6A5ACD"),
        new("Medium Slate Blue", "#7B68EE"),
        new("Dark Slate Blue", "#483D8B"),
        new("Indigo", "#4B0082"),
        // Purples & Violets
        new("Blue Violet", "#8A2BE2"),
        new("Dark Violet", "#9400D3"),
        new("Purple", "#800080"),
        new("Dark Magenta", "#8B008B"),
        new("Medium Purple", "#9370DB"),
        new("Medium Orchid", "#BA55D3"),
        new("Orchid", "#DA70D6"),
        new("Violet", "#EE82EE"),
        new("Plum", "#DDA0DD"),
        new("Thistle", "#D8BFD8"),
        new("Lavender", "#E6E6FA"),
        // Pinks & Magentas
        new("Magenta", "#FF00FF"),
        new("Deep Pink", "#FF1493"),
        new("Hot Pink", "#FF69B4"),
        new("Medium Violet Red", "#C71585"),
        new("Pale Violet Red", "#DB7093"),
        new("Pink", "#FFC0CB"),
        new("Light Pink", "#FFB6C1"),
        new("Lavender Blush", "#FFF0F5"),
        new("Misty Rose", "#FFE4E1"),
        // Browns & Earth Tones
        new("Maroon", "#800000"),
        new("Brown", "#A52A2A"),
        new("Sienna", "#A0522D"),
        new("Saddle Brown", "#8B4513"),
        new("Chocolate", "#D2691E"),
        new("Peru", "#CD853F"),
        new("Sandy Brown", "#F4A460"),
        new("Burlywood", "#DEB887"),
        new("Tan", "#D2B48C"),
        new("Rosy Brown", "#BC8F8F"),
        new("Wheat", "#F5DEB3"),
        new("Moccasin", "#FFE4B5"),
        new("Navajo White", "#FFDEAD"),
        new("Bisque", "#FFE4C4"),
        new("Peach Puff", "#FFDAB9"),
        // Whites & Near-Whites
        new("White", "#FFFFFF"),
        new("Snow", "#FFFAFA"),
        new("Ivory", "#FFFFF0"),
        new("Floral White", "#FFFAF0"),
        new("Ghost White", "#F8F8FF"),
        new("White Smoke", "#F5F5F5"),
        new("Seashell", "#FFF5EE"),
        new("Beige", "#F5F5DC"),
        new("Old Lace", "#FDF5E6"),
        new("Linen", "#FAF0E6"),
        new("Antique White", "#FAEBD7"),
        new("Alice Blue", "#F0F8FF"),
        new("Azure", "#F0FFFF"),
        new("Honeydew", "#F0FFF0"),
        new("Mint Cream", "#F5FFFA"),
        // Grays
        new("Gainsboro", "#DCDCDC"),
        new("Light Gray", "#D3D3D3"),
        new("Silver", "#C0C0C0"),
        new("Dark Gray", "#A9A9A9"),
        new("Gray", "#808080"),
        new("Dim Gray", "#696969"),
        new("Light Slate Gray", "#778899"),
        new("Slate Gray", "#708090"),
        new("Dark Slate Gray", "#2F4F4F"),
        // Blacks
        new("Black", "#000000"),
    ];

    // Theme-prefixed source arrays — theme defaults at the top, full CSS list below.
    private static readonly NamedColor[] _accentSourceColors =
    [
        new("Blue · DarkNavy / Slate", "#4FC3F7"),
        new("Purple · AmoledBlack",    "#CE93D8"),
        new("Teal · DarkGray",         "#4DD0E1"),
        new("Pink · Dracula",          "#F48FB1"),
        new("Green",                   "#81C784"),
        new("Orange",                  "#FFB74D"),
        new("Red",                     "#EF5350"),
        new("Gold",                    "#FFD54F"),
        .._allPresetColors
    ];

    private static readonly NamedColor[] _bgSourceColors =
    [
        new("Dark Navy · DarkNavy",    "#0D0D1A"),
        new("AMOLED Black · Amoled",   "#000000"),
        new("Dark Gray · DarkGray",    "#1A1A1A"),
        new("Dracula · Dracula",       "#282A36"),
        new("Slate · Slate",           "#1A2033"),
        .._allPresetColors
    ];

    private static readonly NamedColor[] _textSourceColors =
    [
        new("Soft Blue · DarkNavy",    "#E0E0FF"),
        new("White · AmoledBlack",     "#FFFFFF"),
        new("Light Gray · DarkGray",   "#EBEBEB"),
        new("Ghost White · Dracula",   "#F8F8F2"),
        new("Pale Blue · Slate",       "#E0E8FF"),
        .._allPresetColors
    ];

    private static readonly NamedColor[] _highlightSourceColors =
    [
        new("White · All Themes",      "#FFFFFF"),
        .._allPresetColors
    ];

    private static readonly NamedColor[] _buttonSourceColors =
    [
        new("Navy Button · DarkNavy",    "#252540"),
        new("Dark Button · AmoledBlack", "#1C1C1C"),
        new("Gray Button · DarkGray",    "#2D2D2D"),
        new("Dracula Button · Dracula",  "#313345"),
        new("Slate Button · Slate",      "#253047"),
        .._allPresetColors
    ];

    private static NamedColor[] FilterColors(string search, NamedColor[] source) =>
        string.IsNullOrWhiteSpace(search) ? source :
        Array.FindAll(source, c =>
            c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            c.Hex.Contains(search, StringComparison.OrdinalIgnoreCase));

    public NamedColor[] FilteredAccentColors    => FilterColors(AccentSearch,    _accentSourceColors);
    public NamedColor[] FilteredBgColors        => FilterColors(BgSearch,        _bgSourceColors);
    public NamedColor[] FilteredTextColors      => FilterColors(TextSearch,       _textSourceColors);
    public NamedColor[] FilteredHighlightColors => FilterColors(HighlightSearch, _highlightSourceColors);
    public NamedColor[] FilteredButtonColors    => FilterColors(ButtonSearch,    _buttonSourceColors);

    public SettingsViewModel(
        IDispatcher dispatcher,
        HandyApiClient handyClient,
        PlayerViewModel playerViewModel,
        MpvMediaPlayerAdapter mpvAdapter,
        DatabaseConfig dbConfig,
        IPlaylistRepository playlistRepo,
        IPresetRepository presetRepo,
        ILogger<SettingsViewModel> logger)
    {
        _dispatcher = dispatcher;
        _handyClient = handyClient;
        _playerViewModel = playerViewModel;
        _mpvAdapter = mpvAdapter;
        _dbConfig = dbConfig;
        _playlistRepo = playlistRepo;
        _presetRepo = presetRepo;
        _logger = logger;

        // Show detected hardware decoder
        DetectedHwDecoder = mpvAdapter.DetectedHwDecoder;

        // Listen for device connection changes to refresh info
        _handyClient.ConnectionStateChanged += (_, state) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsDeviceConnected = state == Core.Models.DeviceConnectionState.Connected;
                RefreshDeviceInfo();
            });
        };

        LoadSettingsAsync().ContinueWith(t =>
            _logger.LogWarning(t.Exception!.InnerException, "LoadSettingsAsync failed"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var roots = await _dispatcher.QueryAsync(new GetLibraryRootsQuery());
            var savedKey = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.ConnectionKey));
            var savedUrl = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.IntifaceUrl));
            var savedBackend = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.Backend));
            var savedProtocol = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.HandyProtocol));
            var savedOffset = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.DefaultOffsetMs));
            var savedSeekStep = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.SeekStepSeconds));
            var savedAccent        = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.AccentColor));
            var savedTheme         = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.AppTheme));
            var savedCustomBg      = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.CustomBgColor));
            var savedCustomText    = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.CustomTextColor));
            var savedCustomHlText  = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.CustomHighlightTextColor));
            var savedCustomButton  = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.CustomButtonColor));

            // MPV settings
            var mpvHw          = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvHwDecode));
            var mpvGpuApi      = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvGpuApi));
            var mpvDeband      = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvDeband));
            var mpvCacheMb     = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvDemuxerCacheMb));
            var mpvScale       = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvScale));
            var mpvLoudnorm    = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvLoudnorm));
            var mpvIccProfile  = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvIccProfile));
            var mpvToneMapping = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvToneMapping));
            var mpvVolumeMax   = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvVolumeMax));
            var mpvVoBackend   = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvVoBackend));
            var mpvShaderPreset= await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvGlslShaderPreset));
            var mpvVideoSync      = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvVideoSync));
            var mpvDither         = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvDither));
            var mpvSubAuto        = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvSubAuto));
            var mpvPitchCorrection= await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.MpvPitchCorrection));
            var saveLastPosition       = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.SaveLastPosition));
            var restoreQueueOnStartup  = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.RestoreQueueOnStartup));
            var watchedThreshold       = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.WatchedThresholdPercent));
            var gaplessPrepare         = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.GaplessPrepareSeconds));
            var fullscreenAutoHide     = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.FullscreenAutoHideSeconds));
            var thumbnailWidth         = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.ThumbnailWidth));
            var thumbnailHeight        = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.ThumbnailHeight));
            var showNavButtons    = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.ShowNavButtons));
            var showFullscreenBtn = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.ShowFullscreenBtn));
            var showAbLoopButtons = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.ShowAbLoopButtons));
            var showSpeedControls = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.ShowSpeedControls));
            var showQueueButton   = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.ShowQueueButton));
            var showOverridesBtn  = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.ShowOverridesBtn));
            var showEqButton      = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.ShowEqButton));
            var showTracksButton  = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.ShowTracksButton));
            var showVolumeControl = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.ShowVolumeControl));

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                LibraryRoots.Clear();
                foreach (var r in roots) LibraryRoots.Add(r);

                if (savedKey != null) ConnectionKey = savedKey;
                if (savedUrl != null) IntifaceUrl = savedUrl;
                if (savedBackend == BackendTypes.Intiface) SelectedBackendIndex = 1;
                if (savedProtocol == ProtocolNames.HSSP) SelectedProtocolIndex = 1;
                if (int.TryParse(savedOffset, out var offset)) DefaultOffsetMs = offset;
                if (int.TryParse(savedSeekStep, out var seekStep) && seekStep > 0) SeekStepSeconds = seekStep;
                // Theme palette
                if (savedTheme != null)
                {
                    var theme = AppThemes.GetByName(savedTheme);
                    SelectedThemeIndex = (int)theme;
                }

                // Accent color — either a preset name or a custom "#RRGGBB" hex
                if (savedAccent?.StartsWith('#') == true
                    && Color.TryParse(savedAccent, out var customColor))
                {
                    CustomAccentHex = savedAccent;
                    AccentColors.ApplyCustomColor(customColor);
                }
                else
                {
                    var accent = AccentColors.GetByName(savedAccent);
                    AccentColors.Apply(accent);
                    CustomAccentHex = $"#{accent.Primary.R:X2}{accent.Primary.G:X2}{accent.Primary.B:X2}";
                }

                // Custom theme colors — applied on top of the active theme preset
                {
                    Color? customBg = null, customFg = null, customHt = null, customBtn = null;
                    if (!string.IsNullOrEmpty(savedCustomBg)     && Color.TryParse(savedCustomBg,     out var bgColor))  customBg  = bgColor;
                    if (!string.IsNullOrEmpty(savedCustomText)   && Color.TryParse(savedCustomText,   out var fgColor))  customFg  = fgColor;
                    if (!string.IsNullOrEmpty(savedCustomHlText) && Color.TryParse(savedCustomHlText, out var htColor))  customHt  = htColor;
                    if (!string.IsNullOrEmpty(savedCustomButton) && Color.TryParse(savedCustomButton, out var btnColor)) customBtn = btnColor;
                    AppThemes.ApplyCustomColors(customBg, customFg, customHt, customBtn);
                    // Sync hex fields from actual current state (theme defaults + any overrides)
                    var (curBg, curFg, curHt) = AppThemes.GetCurrentColors();
                    CustomBgHex            = curBg;
                    CustomTextHex          = curFg;
                    CustomHighlightTextHex = curHt;
                    CustomButtonHex        = AppThemes.GetCurrentButtonHex();
                }

                // MPV settings
                if (mpvHw != null) MpvHardwareDecode = mpvHw != "no";
                MpvGpuApiIndex = mpvGpuApi switch { "d3d11" => 1, "vulkan" => 2, "opengl" => 3, _ => 0 };
                if (mpvDeband != null) MpvDeband = mpvDeband == "true";
                if (int.TryParse(mpvCacheMb, out var cacheMb) && cacheMb > 0) MpvDemuxerCacheMb = cacheMb;
                if (mpvLoudnorm   != null) MpvLoudnorm   = mpvLoudnorm   == "true";
                if (mpvIccProfile != null) MpvIccProfile = mpvIccProfile == "true";
                MpvToneMappingIndex = mpvToneMapping switch
                {
                    "bt.2446a" => 1, "hable" => 2, "reinhard" => 3, "clip" => 4, _ => 0
                };
                if (int.TryParse(mpvVolumeMax, out var vmax) && vmax >= 100) MpvVolumeMax = vmax;
                MpvVoBackendIndex = mpvVoBackend == "gpu-next" ? 1 : 0;
                MpvVideoSync       = mpvVideoSync       == "true";
                MpvDither          = mpvDither          != "false"; // default true
                MpvSubAuto         = mpvSubAuto         != "false"; // default true
                MpvPitchCorrection = mpvPitchCorrection != "false"; // default true
                SaveLastPosition        = saveLastPosition       == "true";
                RestoreQueueOnStartup  = restoreQueueOnStartup  == "true";
                if (int.TryParse(watchedThreshold, out var wt) && wt >= 10 && wt <= 100) WatchedThresholdPercent = wt;
                if (int.TryParse(gaplessPrepare, out var gp) && gp > 0) GaplessPrepareSeconds = gp;
                if (int.TryParse(fullscreenAutoHide, out var ah) && ah > 0) FullscreenAutoHideSeconds = ah;
                if (int.TryParse(thumbnailWidth, out var tw) && tw > 0) ThumbnailWidth = tw;
                if (int.TryParse(thumbnailHeight, out var th) && th > 0) ThumbnailHeight = th;
                ShowNavButtons    = showNavButtons    != "false";
                ShowFullscreenBtn = showFullscreenBtn != "false";
                ShowAbLoopButtons = showAbLoopButtons != "false";
                ShowSpeedControls = showSpeedControls != "false";
                ShowQueueButton   = showQueueButton   != "false";
                ShowOverridesBtn  = showOverridesBtn  != "false";
                ShowEqButton      = showEqButton      != "false";
                ShowTracksButton  = showTracksButton  != "false";
                ShowVolumeControl = showVolumeControl != "false";

                // Load shader preset first (applying it live; mpv may not be initialized yet, that's fine)
                var metalFxIncompatible = mpvShaderPreset == "MetalFxSpatial" && !OperatingSystem.IsMacOS();
                // Suppress MVVMTK0034: intentional backing-field writes during init to avoid triggering
                // the property-change handlers (which would clear the shader we're in the process of loading).
#pragma warning disable MVVMTK0034
                _mpvShaderPresetIndex = mpvShaderPreset switch
                {
                    "Sharpen"        => 1,
                    "FsrSpatial"     => 2,
                    "NIS"            => 3,
                    "RavuLite"       => 4,
                    "FSRCNNX"        => 5,
                    "NLMeans"        => 6,
                    "Anime4KFast"    => 7,
                    "Anime4KQuality" => 8,
                    "MetalFxSpatial" => OperatingSystem.IsMacOS() ? 9 : 0,
                    "AutoFast"       => 10,
                    "AutoQuality"    => 11,
                    _                => 0,
                };
                // Persist the corrected value so the incompatible setting doesn't re-appear next launch
                if (metalFxIncompatible)
                    _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvGlslShaderPreset, "None"));

                // Compute scale index — bypass the property setter to avoid triggering the handler
                // (which would clear the shader we just loaded above).
                var savedScaleIdx = mpvScale switch
                {
                    "bilinear" => 1, "lanczos" => 2, "ewa_lanczossharp" => 3, "mitchell" => 4, _ => 0
                };
                _lastStandardScaleIndex = savedScaleIdx;
                _mpvScaleIndex = _mpvShaderPresetIndex != 0 ? 5 : savedScaleIdx; // 5 = AI mode
#pragma warning restore MVVMTK0034
                OnPropertyChanged(nameof(MpvScaleIndex));
                OnPropertyChanged(nameof(MpvShaderPresetIndex));
                OnPropertyChanged(nameof(IsAiUpscalingMode));
                OnPropertyChanged(nameof(UpscalingDescription));

                // Apply protocol to client
                _handyClient.SetProtocol(SelectedProtocolIndex == 1 ? HandyProtocol.HSSP : HandyProtocol.HDSP);

                RefreshDeviceInfo();
            });

            // Keybindings
            var keybindingsJson = await _dispatcher.QueryAsync(new GetSettingQuery(SettingKeys.Keybindings));
            _keybindingMap = !string.IsNullOrEmpty(keybindingsJson) ? KeybindingMap.FromJson(keybindingsJson) : new();
            RefreshKeybindingEntries();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
        }
    }

    partial void OnSelectedProtocolIndexChanged(int value)
    {
        var protocol = value == 1 ? HandyProtocol.HSSP : HandyProtocol.HDSP;
        _handyClient.SetProtocol(protocol);
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.HandyProtocol, value == 1 ? ProtocolNames.HSSP : ProtocolNames.HDSP));
    }

    partial void OnDefaultOffsetMsChanged(int value)
    {
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.DefaultOffsetMs, value.ToString()));
        // Also update live playback immediately
        _playerViewModel.OverrideControls.OffsetMs = value;
    }

    partial void OnSeekStepSecondsChanged(int value)
    {
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.SeekStepSeconds, value.ToString()));
        _playerViewModel.UpdateSeekStep(value);
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        var theme = (AppThemeOption)value;
        AppThemes.Apply(theme); // also applies the theme's default accent via AccentColors.Apply()
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.AppTheme, theme.ToString()));
        // Reset custom color overrides — sync hex fields from the new theme's defaults
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomBgColor, ""));
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomTextColor, ""));
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomHighlightTextColor, ""));
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomButtonColor, ""));
        var (bg, fg, ht) = AppThemes.GetCurrentColors();
        CustomBgHex            = bg;
        CustomTextHex          = fg;
        CustomHighlightTextHex = ht;
        CustomButtonHex        = AppThemes.GetCurrentButtonHex();
        // Sync accent hex → triggers OnCustomAccentHexChanged → updates SelectedAccentColor ListBox
        var accentHex = AppThemes.GetDefaultAccentHex(theme);
        CustomAccentHex = accentHex;
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.AccentColor, accentHex));
    }

    // ── Hex ↔ ListBox selection sync ─────────────────────────────────────

    private static NamedColor? FindInSource(NamedColor[] source, string hex)
    {
        var h = hex.Trim();
        if (!h.StartsWith('#')) h = "#" + h;
        return Array.Find(source, c => string.Equals(c.Hex, h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns true if both hex strings refer to the same color,
    /// guarding against jumping between two items that share the same hex value.
    /// </summary>
    private static bool HexEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        static string N(string h) => h.TrimStart('#').Trim().ToUpperInvariant();
        return N(a) == N(b);
    }

    partial void OnCustomAccentHexChanged(string value)
    {
        if (_syncingColors) return;
        if (HexEqual(SelectedAccentColor?.Hex, value)) return;
        var match = FindInSource(_accentSourceColors, value);
        if (SelectedAccentColor != match) { _syncingColors = true; SelectedAccentColor = match; _syncingColors = false; }
    }

    partial void OnCustomBgHexChanged(string value)
    {
        if (_syncingColors) return;
        if (HexEqual(SelectedBgColor?.Hex, value)) return;
        var match = FindInSource(_bgSourceColors, value);
        if (SelectedBgColor != match) { _syncingColors = true; SelectedBgColor = match; _syncingColors = false; }
    }

    partial void OnCustomTextHexChanged(string value)
    {
        if (_syncingColors) return;
        if (HexEqual(SelectedTextColor?.Hex, value)) return;
        var match = FindInSource(_textSourceColors, value);
        if (SelectedTextColor != match) { _syncingColors = true; SelectedTextColor = match; _syncingColors = false; }
    }

    partial void OnCustomHighlightTextHexChanged(string value)
    {
        if (_syncingColors) return;
        if (HexEqual(SelectedHighlightColor?.Hex, value)) return;
        var match = FindInSource(_highlightSourceColors, value);
        if (SelectedHighlightColor != match) { _syncingColors = true; SelectedHighlightColor = match; _syncingColors = false; }
    }

    partial void OnCustomButtonHexChanged(string value)
    {
        if (_syncingColors) return;
        if (HexEqual(SelectedButtonColor?.Hex, value)) return;
        var match = FindInSource(_buttonSourceColors, value);
        if (SelectedButtonColor != match) { _syncingColors = true; SelectedButtonColor = match; _syncingColors = false; }
    }

    partial void OnSelectedBgColorChanged(NamedColor? value)
    {
        if (value == null || _syncingColors) return;
        _syncingColors = true;
        CustomBgHex = value.Hex;
        _syncingColors = false;
        if (Color.TryParse(value.Hex, out var c))
        {
            AppThemes.ApplyCustomColors(c, null, null);
            _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomBgColor, value.Hex));
        }
    }

    partial void OnSelectedTextColorChanged(NamedColor? value)
    {
        if (value == null || _syncingColors) return;
        _syncingColors = true;
        CustomTextHex = value.Hex;
        _syncingColors = false;
        if (Color.TryParse(value.Hex, out var c))
        {
            AppThemes.ApplyCustomColors(null, c, null);
            _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomTextColor, value.Hex));
        }
    }

    partial void OnSelectedHighlightColorChanged(NamedColor? value)
    {
        if (value == null || _syncingColors) return;
        _syncingColors = true;
        CustomHighlightTextHex = value.Hex;
        _syncingColors = false;
        if (Color.TryParse(value.Hex, out var c))
        {
            AppThemes.ApplyCustomColors(null, null, c);
            _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomHighlightTextColor, value.Hex));
        }
    }

    partial void OnSelectedAccentColorChanged(NamedColor? value)
    {
        if (value == null || _syncingColors) return;
        if (Color.TryParse(value.Hex, out var c))
        {
            _syncingColors = true;
            CustomAccentHex = value.Hex;
            _syncingColors = false;
            AccentColors.ApplyCustomColor(c);
            _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.AccentColor, value.Hex));
        }
    }

    partial void OnSelectedButtonColorChanged(NamedColor? value)
    {
        if (value == null || _syncingColors) return;
        _syncingColors = true;
        CustomButtonHex = value.Hex;
        _syncingColors = false;
        if (Color.TryParse(value.Hex, out var c))
        {
            AppThemes.ApplyCustomColors(null, null, null, c);
            _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomButtonColor, value.Hex));
        }
    }

    [RelayCommand]
    private void ApplyAllColors()
    {
        static bool TryNorm(string input, out string normalized, out Color color)
        {
            normalized = input.Trim();
            if (!normalized.StartsWith('#')) normalized = "#" + normalized;
            return Color.TryParse(normalized, out color);
        }

        if (!TryNorm(CustomAccentHex, out var accentHex, out var accentColor))
            { StatusText = "Invalid accent color — use #RRGGBB"; return; }
        if (!TryNorm(CustomBgHex, out var bgHex, out var bgColor))
            { StatusText = "Invalid background color — use #RRGGBB"; return; }
        if (!TryNorm(CustomTextHex, out var fgHex, out var fgColor))
            { StatusText = "Invalid text color — use #RRGGBB"; return; }
        if (!TryNorm(CustomHighlightTextHex, out var htHex, out var htColor))
            { StatusText = "Invalid highlight color — use #RRGGBB"; return; }
        if (!TryNorm(CustomButtonHex, out var btnHex, out var btnColor))
            { StatusText = "Invalid button color — use #RRGGBB"; return; }

        CustomAccentHex        = accentHex;
        CustomBgHex            = bgHex;
        CustomTextHex          = fgHex;
        CustomHighlightTextHex = htHex;
        CustomButtonHex        = btnHex;

        AccentColors.ApplyCustomColor(accentColor);
        AppThemes.ApplyCustomColors(bgColor, fgColor, htColor, btnColor);
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.AccentColor, accentHex));
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomBgColor, bgHex));
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomTextColor, fgHex));
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomHighlightTextColor, htHex));
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomButtonColor, btnHex));
        StatusText = "All colors applied.";
    }

    [RelayCommand]
    private void ResetCustomColors()
    {
        // Re-apply the current theme to restore default bg/text brushes
        var theme = (AppThemeOption)SelectedThemeIndex;
        AppThemes.Apply(theme);
        // Re-apply the user's accent color so it isn't reset to the theme's default
        if (Color.TryParse(CustomAccentHex, out var accentColor))
            AccentColors.ApplyCustomColor(accentColor);
        // Sync hex fields from theme defaults
        var (bg, fg, ht) = AppThemes.GetCurrentColors();
        CustomBgHex            = bg;
        CustomTextHex          = fg;
        CustomHighlightTextHex = ht;
        CustomButtonHex        = AppThemes.GetCurrentButtonHex();
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomBgColor, ""));
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomTextColor, ""));
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomHighlightTextColor, ""));
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomButtonColor, ""));
        StatusText = "Custom colors reset to theme defaults.";
    }

    // MPV settings — saved to DB, applied on next restart
    partial void OnMpvHardwareDecodeChanged(bool value)
    {
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvHwDecode, value ? "auto" : "no"));
        _mpvAdapter.SetLiveProperty("hwdec", value ? "auto" : "no");
    }

    partial void OnMpvGpuApiIndexChanged(int value)
    {
        var api = value switch { 1 => "d3d11", 2 => "vulkan", 3 => "opengl", _ => "auto" };
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvGpuApi, api));
        _ = _mpvAdapter.ReinitVoAsync("gpu-api", api);
    }

    partial void OnMpvDebandChanged(bool value)
    {
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvDeband, value ? "true" : "false"));
        _mpvAdapter.SetLiveProperty("deband", value ? "yes" : "no");
    }

    partial void OnMpvDemuxerCacheMbChanged(int value)
    {
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvDemuxerCacheMb, value.ToString()));
        _mpvAdapter.SetLiveProperty("demuxer-max-bytes", (value * 1024L * 1024L).ToString());
    }

    partial void OnMpvScaleIndexChanged(int value)
    {
        if (value == 5) // AI mode selected
        {
            // Default to CAS Sharpen if no AI shader was previously chosen
            if (MpvShaderPresetIndex == 0)
                MpvShaderPresetIndex = 1;
            return;
        }

        // Standard filter selected
        _lastStandardScaleIndex = value;
        var scale = value switch { 1 => "bilinear", 2 => "lanczos", 3 => "ewa_lanczossharp", 4 => "mitchell", _ => "spline36" };
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvScale, scale));
        _mpvAdapter.UpdateScale(scale);

        // Clear any active AI shader
        if (MpvShaderPresetIndex != 0)
            MpvShaderPresetIndex = 0; // triggers OnMpvShaderPresetIndexChanged → UpdateShadersLive(None)
        else
            _mpvAdapter.UpdateShadersLive(GlslShaderPreset.None);
    }

    partial void OnMpvLoudnormChanged(bool value)
    {
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvLoudnorm, value ? "true" : "false"));
        _mpvAdapter.SetLiveProperty("af", value ? "loudnorm" : "");
    }

    partial void OnMpvIccProfileChanged(bool value)
    {
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvIccProfile, value ? "true" : "false"));
        _ = _mpvAdapter.ReinitVoAsync("icc-profile-auto", value ? "yes" : "no");
    }

    partial void OnMpvToneMappingIndexChanged(int value)
    {
        var tm = value switch { 1 => "bt.2446a", 2 => "hable", 3 => "reinhard", 4 => "clip", _ => "auto" };
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvToneMapping, tm));
        _mpvAdapter.SetLiveProperty("tone-mapping", tm);
    }

    partial void OnMpvVolumeMaxChanged(int value)
    {
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvVolumeMax, value.ToString()));
        _mpvAdapter.UpdateVolumeMax(value);
    }

    partial void OnMpvVoBackendIndexChanged(int value)
    {
        var backend = value == 1 ? "gpu-next" : "gpu";
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvVoBackend, backend));
        StatusText = "VO backend change requires restart.";
    }

    partial void OnMpvVideoSyncChanged(bool value) =>
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvVideoSync, value.ToString().ToLower()));

    partial void OnMpvDitherChanged(bool value) =>
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvDither, value.ToString().ToLower()));

    partial void OnMpvSubAutoChanged(bool value) =>
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvSubAuto, value.ToString().ToLower()));

    partial void OnMpvPitchCorrectionChanged(bool value)
    {
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvPitchCorrection, value.ToString().ToLower()));
        _mpvAdapter.SetPitchCorrection(value);
    }

    partial void OnSaveLastPositionChanged(bool value) =>
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.SaveLastPosition, value.ToString().ToLower()));

    partial void OnRestoreQueueOnStartupChanged(bool value) =>
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.RestoreQueueOnStartup, value.ToString().ToLower()));

    partial void OnWatchedThresholdPercentChanged(int value)
    {
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.WatchedThresholdPercent, value.ToString()));
        _playerViewModel.UpdateWatchedThreshold(value);
    }

    partial void OnGaplessPrepareSecondsChanged(int value)
    {
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.GaplessPrepareSeconds, value.ToString()));
        _playerViewModel.UpdateGaplessPrepareSeconds(value);
    }

    partial void OnFullscreenAutoHideSecondsChanged(int value)
    {
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.FullscreenAutoHideSeconds, value.ToString()));
        _playerViewModel.AutoHideSeconds = Math.Max(1, value);
    }

    partial void OnThumbnailWidthChanged(int value) =>
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.ThumbnailWidth, value.ToString()));

    partial void OnThumbnailHeightChanged(int value) =>
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.ThumbnailHeight, value.ToString()));

    partial void OnShowNavButtonsChanged(bool value)    => SaveBoolAndPushToPlayer(SettingKeys.ShowNavButtons,    value, p => p.ShowNavButtons    = value);
    partial void OnShowFullscreenBtnChanged(bool value) => SaveBoolAndPushToPlayer(SettingKeys.ShowFullscreenBtn, value, p => p.ShowFullscreenBtn = value);
    partial void OnShowAbLoopButtonsChanged(bool value) => SaveBoolAndPushToPlayer(SettingKeys.ShowAbLoopButtons, value, p => p.ShowAbLoopButtons = value);
    partial void OnShowSpeedControlsChanged(bool value) => SaveBoolAndPushToPlayer(SettingKeys.ShowSpeedControls, value, p => p.ShowSpeedControls = value);
    partial void OnShowQueueButtonChanged(bool value)   => SaveBoolAndPushToPlayer(SettingKeys.ShowQueueButton,   value, p => p.ShowQueueButton   = value);
    partial void OnShowOverridesBtnChanged(bool value)  => SaveBoolAndPushToPlayer(SettingKeys.ShowOverridesBtn,  value, p => p.ShowOverridesBtn  = value);
    partial void OnShowEqButtonChanged(bool value)      => SaveBoolAndPushToPlayer(SettingKeys.ShowEqButton,      value, p => p.ShowEqButton      = value);
    partial void OnShowTracksButtonChanged(bool value)  => SaveBoolAndPushToPlayer(SettingKeys.ShowTracksButton,  value, p => p.ShowTracksButton  = value);
    partial void OnShowVolumeControlChanged(bool value) => SaveBoolAndPushToPlayer(SettingKeys.ShowVolumeControl, value, p => p.ShowVolumeControl = value);

    private void SaveBoolAndPushToPlayer(string key, bool value, Action<PlayerViewModel> apply)
    {
        _ = _dispatcher.SendAsync(new SaveSettingCommand(key, value.ToString().ToLower()));
        if (_playerViewModel != null) apply(_playerViewModel);
    }

    partial void OnMpvShaderPresetIndexChanged(int value)
    {
        if (value == 0 && MpvScaleIndex == 5)
        {
            // User chose "None" in the AI sub-selector — exit AI mode entirely
            _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvGlslShaderPreset, "None"));
            MpvScaleIndex = _lastStandardScaleIndex; // triggers OnMpvScaleIndexChanged which clears shaders
            return;
        }

        var presetEnum = (GlslShaderPreset)value;
        _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.MpvGlslShaderPreset, presetEnum.ToString()));
        _mpvAdapter.UpdateShadersLive(presetEnum);
        StatusText = presetEnum switch
        {
            GlslShaderPreset.None           => "AI upscaling disabled.",
            GlslShaderPreset.Sharpen        => "CAS sharpening applied.",
            GlslShaderPreset.FsrSpatial     => "FSR 1.0 spatial upscaling applied.",
            GlslShaderPreset.NIS            => "NVIDIA Image Scaling applied.",
            GlslShaderPreset.RavuLite       => "RAVU Lite applied.",
            GlslShaderPreset.FSRCNNX        => "FSRCNNX applied. Note: only activates at ≥1.5× upscale ratio.",
            GlslShaderPreset.NLMeans        => "nlmeans denoising applied.",
            GlslShaderPreset.Anime4KFast    => "Anime4K Fast applied.",
            GlslShaderPreset.Anime4KQuality => "Anime4K Quality applied.",
            GlslShaderPreset.MetalFxSpatial => "MetalFX Spatial applied.",
            GlslShaderPreset.AutoFast       => "Auto Fast: Anime4K + CAS sharpening applied.",
            GlslShaderPreset.AutoQuality    => "Auto Quality: Anime4K full restore + CAS applied.",
            _ => "",
        };
    }

    private void RefreshDeviceInfo()
    {
        var info = _handyClient.DeviceInfo;
        var status = _handyClient.DeviceStatus;
        var slide = _handyClient.DeviceSlide;

        if (info != null)
        {
            DeviceInfoText = $"Model: {info.Model}  FW: {info.FwVersion}  HW: {info.HwVersion}  Branch: {info.Branch}";
        }
        else
        {
            DeviceInfoText = "Not connected";
        }

        if (status != null)
        {
            var modeName = HandyDeviceModes.GetDisplayName(status.Mode);
            var rtt = _handyClient.AvgRoundTripMs;
            var rttInfo = rtt > 0 ? $"  RTT: {rtt}ms" : "";
            DeviceStatusText = $"Mode: {modeName}  State: {status.State}{rttInfo}";
        }
        else
        {
            DeviceStatusText = "";
        }

        if (slide != null)
        {
            DeviceSlideText = $"Slide range: {slide.Min:F0}% - {slide.Max:F0}%";
        }
        else
        {
            DeviceSlideText = "";
        }
    }

    // ── Sync Calibration ──────────────────────────────────────────

    [RelayCommand]
    private void StartCalibration()
    {
        if (_handyClient.ConnectionState != Core.Models.DeviceConnectionState.Connected)
        {
            CalibrationStatusText = "Device not connected.";
            return;
        }

        // Stop any existing calibration before starting a new one
        if (_calibrationTimer != null)
        {
            _calibrationTimer.Stop();
            _calibrationTimer.Tick -= OnCalibrationTick;
            _calibrationTimer = null;
        }

        _calibrationGoingUp = true;
        _calibrationStopwatch = Stopwatch.StartNew();

        // Send first movement: go to 100 over half-cycle duration
        _ = _handyClient.SendPositionAsync(100, CalibrationHalfCycleMs);

        _calibrationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _calibrationTimer.Tick += OnCalibrationTick;
        _calibrationTimer.Start();
        IsCalibrationRunning = true;
        CalibrationStatusText = "Adjust offset until visual matches device.";
    }

    [RelayCommand]
    private void StopCalibration()
    {
        if (_calibrationTimer != null)
        {
            _calibrationTimer.Stop();
            _calibrationTimer.Tick -= OnCalibrationTick;
        }
        _calibrationTimer = null;
        _calibrationStopwatch = null;
        IsCalibrationRunning = false;
        CalibrationIndicatorTop = 90;

        // Send device to bottom
        _ = _handyClient.SendPositionAsync(0, 500);

        CalibrationStatusText = $"Saved offset: {DefaultOffsetMs}ms";
    }

    [RelayCommand]
    private void NudgeCalibrationOffset(string amount)
    {
        if (int.TryParse(amount, out var delta))
            DefaultOffsetMs += delta;
    }

    private void OnCalibrationTick(object? sender, EventArgs e)
    {
        if (_calibrationStopwatch == null) return;

        var elapsedMs = _calibrationStopwatch.ElapsedMilliseconds;

        // Triangle wave: position oscillates 0→100→0 over CalibrationCycleMs
        var phase = (elapsedMs % CalibrationCycleMs) / (double)CalibrationCycleMs;
        var goingUp = phase < 0.5;
        var position = Math.Clamp(goingUp
            ? phase * 2.0 * 100.0        // 0→100 in first half
            : (1.0 - phase) * 2.0 * 100.0, 0, 100); // 100→0 in second half

        // Update visual indicator
        CalibrationIndicatorTop = 180.0 * (1.0 - position / 100.0);

        // Send one position command at each phase transition
        if (goingUp != _calibrationGoingUp)
        {
            _calibrationGoingUp = goingUp;
            var targetPos = goingUp ? 100 : 0;
            _ = _handyClient.SendPositionAsync(targetPos, CalibrationHalfCycleMs);
        }
    }

    // ── Device Info ──────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshDeviceInfoFromApi()
    {
        if (_handyClient.ConnectionState != Core.Models.DeviceConnectionState.Connected)
        {
            StatusText = "Device not connected";
            return;
        }

        try
        {
            await _handyClient.FetchDeviceInfoAsync();
            RefreshDeviceInfo();
            StatusText = "Device info refreshed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh device info");
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveConnectionKey()
    {
        await _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.ConnectionKey, ConnectionKey));
    }

    [RelayCommand]
    private async Task SaveIntifaceUrl()
    {
        await _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.IntifaceUrl, IntifaceUrl));
    }

    [RelayCommand]
    private async Task AddFolder(Window window)
    {
        try
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Library Folder",
                AllowMultiple = false
            });
            if (folders.Count > 0)
            {
                var rootId = await _dispatcher.SendAsync(new AddLibraryRootCommand(folders[0].Path.LocalPath));
                await LoadSettingsAsync();
                _ = Task.Run(() => _dispatcher.SendAsync(new ScanLibraryRootCommand(rootId)));
            }
        }
        catch (ValidationException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add library root");
        }
    }

    [RelayCommand]
    private async Task RemoveLibraryRoot(LibraryRoot root)
    {
        await _dispatcher.SendAsync(new RemoveLibraryRootCommand(root.Id));
        await LoadSettingsAsync();
    }

    [RelayCommand]
    private async Task ClearDatabaseCache()
    {
        try
        {
            await Task.Run(() =>
            {
                using var conn = new SqliteConnection($"Data Source={_dbConfig.DatabasePath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                // Clear library index, pairings, and script hosting cache.
                // Preserves: app_settings, library_roots, presets, playlists, playlist_items.
                cmd.CommandText = "DELETE FROM pairings; DELETE FROM media_files; DELETE FROM script_cache;";
                cmd.ExecuteNonQuery();
            });

            // Reload library roots to reflect cleared state
            await LoadSettingsAsync();
            StatusText = "Database cache cleared. Re-scan your library to rebuild.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear database cache");
            StatusText = $"Error clearing cache: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportData(Window window)
    {
        try
        {
            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Data",
                DefaultExtension = "json",
                SuggestedFileName = "handyplayer-export",
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
            });
            if (file == null) return;

            var playlists = await _playlistRepo.GetAllAsync();
            var presets   = await _presetRepo.GetAllAsync();
            var libraryRoots = await _dispatcher.QueryAsync(new GetLibraryRootsQuery());

            // Helper to read a setting with fallback
            async Task<string> Get(string key, string fallback = "") =>
                await _dispatcher.QueryAsync(new GetSettingQuery(key)) ?? fallback;

            var export = new
            {
                Version = 3,
                ExportedAt = DateTime.UtcNow.ToString("o"),
                AppearanceSettings = new
                {
                    Theme               = await Get(SettingKeys.AppTheme, "DarkNavy"),
                    AccentColor         = await Get(SettingKeys.AccentColor, "Blue"),
                    CustomBgColor       = await Get(SettingKeys.CustomBgColor),
                    CustomTextColor     = await Get(SettingKeys.CustomTextColor),
                    CustomHighlightText = await Get(SettingKeys.CustomHighlightTextColor),
                    CustomButtonColor   = await Get(SettingKeys.CustomButtonColor),
                },
                MpvSettings = new
                {
                    HwDecode       = await Get(SettingKeys.MpvHwDecode, "auto"),
                    GpuApi         = await Get(SettingKeys.MpvGpuApi, "auto"),
                    Deband         = await Get(SettingKeys.MpvDeband),
                    DemuxerCacheMb = await Get(SettingKeys.MpvDemuxerCacheMb, "128"),
                    Scale          = await Get(SettingKeys.MpvScale, "spline36"),
                    Loudnorm       = await Get(SettingKeys.MpvLoudnorm),
                    IccProfile     = await Get(SettingKeys.MpvIccProfile),
                    ToneMapping    = await Get(SettingKeys.MpvToneMapping),
                    VolumeMax      = await Get(SettingKeys.MpvVolumeMax, "100"),
                    VoBackend      = await Get(SettingKeys.MpvVoBackend, "gpu"),
                    GlslShaderPreset = await Get(SettingKeys.MpvGlslShaderPreset, "None"),
                    VideoSync      = await Get(SettingKeys.MpvVideoSync),
                    Dither         = await Get(SettingKeys.MpvDither),
                    SubAuto        = await Get(SettingKeys.MpvSubAuto),
                    PitchCorrection = await Get(SettingKeys.MpvPitchCorrection),
                },
                PlaybackSettings = new
                {
                    DefaultOffsetMs    = await Get(SettingKeys.DefaultOffsetMs, "0"),
                    SeekStepSeconds    = await Get(SettingKeys.SeekStepSeconds, "15"),
                    SaveLastPosition   = await Get(SettingKeys.SaveLastPosition),
                    RestoreQueueOnStartup = await Get(SettingKeys.RestoreQueueOnStartup),
                    WatchedThresholdPercent  = await Get(SettingKeys.WatchedThresholdPercent, "90"),
                    GaplessPrepareSeconds    = await Get(SettingKeys.GaplessPrepareSeconds, "30"),
                    FullscreenAutoHideSeconds = await Get(SettingKeys.FullscreenAutoHideSeconds, "3"),
                    ThumbnailWidth           = await Get(SettingKeys.ThumbnailWidth, "320"),
                    ThumbnailHeight          = await Get(SettingKeys.ThumbnailHeight, "180"),
                },
                PlayerControls = new
                {
                    ShowNavButtons    = await Get(SettingKeys.ShowNavButtons, "true"),
                    ShowFullscreenBtn = await Get(SettingKeys.ShowFullscreenBtn, "true"),
                    ShowAbLoopButtons = await Get(SettingKeys.ShowAbLoopButtons, "true"),
                    ShowSpeedControls = await Get(SettingKeys.ShowSpeedControls, "true"),
                    ShowQueueButton   = await Get(SettingKeys.ShowQueueButton, "true"),
                    ShowOverridesBtn  = await Get(SettingKeys.ShowOverridesBtn, "true"),
                    ShowEqButton      = await Get(SettingKeys.ShowEqButton, "true"),
                    ShowTracksButton  = await Get(SettingKeys.ShowTracksButton, "true"),
                    ShowVolumeControl = await Get(SettingKeys.ShowVolumeControl, "true"),
                },
                DeviceSettings = new
                {
                    Backend  = await Get(SettingKeys.Backend, "handy"),
                    Protocol = await Get(SettingKeys.HandyProtocol, "HDSP"),
                },
                Keybindings = await Get(SettingKeys.Keybindings),
                LibraryRoots = libraryRoots.Select(r => r.Path),
                Playlists = playlists.Select(p => new
                {
                    p.Name, p.Type, p.FolderPath, p.FilterJson, p.SortOrder
                }),
                Presets = presets.Select(p => new
                {
                    p.Name, p.RangeMin, p.RangeMax, p.OffsetMs, p.SpeedLimit,
                    p.Intensity, p.Invert, p.IsExpert, p.SmoothingFactor, p.CurveGamma, p.TickRateMs
                })
            };

            var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);

            StatusText = "Data exported successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed");
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportData(Window window)
    {
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Data",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
            });
            if (files.Count == 0) return;

            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int imported = 0;

            // Helper to save a setting if the JSON property exists and is non-empty
            async Task ImportSetting(JsonElement section, string jsonProp, string settingKey)
            {
                if (section.TryGetProperty(jsonProp, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    var s = val.GetString();
                    if (!string.IsNullOrEmpty(s))
                        await _dispatcher.SendAsync(new SaveSettingCommand(settingKey, s));
                }
            }

            // ── Appearance settings ──────────────────────────────────────────
            if (root.TryGetProperty("AppearanceSettings", out var appearance))
            {
                var themeName  = appearance.TryGetProperty("Theme",               out var th)  ? th.GetString()  : null;
                var accentVal  = appearance.TryGetProperty("AccentColor",         out var ac)  ? ac.GetString()  : null;
                var bgColor    = appearance.TryGetProperty("CustomBgColor",       out var bg)  ? bg.GetString()  : null;
                var textColor  = appearance.TryGetProperty("CustomTextColor",     out var tx)  ? tx.GetString()  : null;
                var hlText     = appearance.TryGetProperty("CustomHighlightText", out var hl)  ? hl.GetString()  : null;
                var btnColor   = appearance.TryGetProperty("CustomButtonColor",   out var btn) ? btn.GetString() : null;

                if (!string.IsNullOrEmpty(themeName))
                {
                    var theme = AppThemes.GetByName(themeName);
                    AppThemes.Apply(theme);
                    SelectedThemeIndex = (int)theme;
                    _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.AppTheme, themeName));
                }

                if (!string.IsNullOrEmpty(accentVal))
                {
                    if (accentVal.StartsWith('#') && Color.TryParse(accentVal, out var customAccent))
                    {
                        AccentColors.ApplyCustomColor(customAccent);
                        CustomAccentHex = accentVal; // triggers PickerAccentColor sync
                    }
                    else
                    {
                        var accent = AccentColors.GetByName(accentVal);
                        AccentColors.Apply(accent);
                        CustomAccentHex = $"#{accent.Primary.R:X2}{accent.Primary.G:X2}{accent.Primary.B:X2}";
                    }
                    _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.AccentColor, accentVal));
                }

                Color? importBg = null, importFg = null, importHl = null;
                if (!string.IsNullOrEmpty(bgColor)   && Color.TryParse(bgColor,   out var bc)) importBg = bc;
                if (!string.IsNullOrEmpty(textColor)  && Color.TryParse(textColor,  out var tc)) importFg = tc;
                if (!string.IsNullOrEmpty(hlText)     && Color.TryParse(hlText,     out var hc)) importHl = hc;

                if (importBg.HasValue || importFg.HasValue || importHl.HasValue)
                {
                    AppThemes.ApplyCustomColors(importBg, importFg, importHl);
                    var (curBg, curFg, curHt) = AppThemes.GetCurrentColors();
                    CustomBgHex            = curBg; // triggers PickerBgColor sync
                    CustomTextHex          = curFg;
                    CustomHighlightTextHex = curHt;
                    _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomBgColor,            curBg));
                    _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomTextColor,          curFg));
                    _ = _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomHighlightTextColor, curHt));
                }

                if (!string.IsNullOrEmpty(btnColor))
                    await _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.CustomButtonColor, btnColor));
            }

            // ── MPV Settings (V3) ────────────────────────────────────────────
            if (root.TryGetProperty("MpvSettings", out var mpv))
            {
                await ImportSetting(mpv, "HwDecode",         SettingKeys.MpvHwDecode);
                await ImportSetting(mpv, "GpuApi",           SettingKeys.MpvGpuApi);
                await ImportSetting(mpv, "Deband",           SettingKeys.MpvDeband);
                await ImportSetting(mpv, "DemuxerCacheMb",   SettingKeys.MpvDemuxerCacheMb);
                await ImportSetting(mpv, "Scale",            SettingKeys.MpvScale);
                await ImportSetting(mpv, "Loudnorm",         SettingKeys.MpvLoudnorm);
                await ImportSetting(mpv, "IccProfile",       SettingKeys.MpvIccProfile);
                await ImportSetting(mpv, "ToneMapping",      SettingKeys.MpvToneMapping);
                await ImportSetting(mpv, "VolumeMax",        SettingKeys.MpvVolumeMax);
                await ImportSetting(mpv, "VoBackend",        SettingKeys.MpvVoBackend);
                await ImportSetting(mpv, "GlslShaderPreset", SettingKeys.MpvGlslShaderPreset);
                await ImportSetting(mpv, "VideoSync",        SettingKeys.MpvVideoSync);
                await ImportSetting(mpv, "Dither",           SettingKeys.MpvDither);
                await ImportSetting(mpv, "SubAuto",          SettingKeys.MpvSubAuto);
                await ImportSetting(mpv, "PitchCorrection",  SettingKeys.MpvPitchCorrection);
            }

            // ── Playback Settings (V3) ──────────────────────────────────────
            if (root.TryGetProperty("PlaybackSettings", out var playback))
            {
                await ImportSetting(playback, "DefaultOffsetMs",          SettingKeys.DefaultOffsetMs);
                await ImportSetting(playback, "SeekStepSeconds",          SettingKeys.SeekStepSeconds);
                await ImportSetting(playback, "SaveLastPosition",         SettingKeys.SaveLastPosition);
                await ImportSetting(playback, "RestoreQueueOnStartup",    SettingKeys.RestoreQueueOnStartup);
                await ImportSetting(playback, "WatchedThresholdPercent",  SettingKeys.WatchedThresholdPercent);
                await ImportSetting(playback, "GaplessPrepareSeconds",    SettingKeys.GaplessPrepareSeconds);
                await ImportSetting(playback, "FullscreenAutoHideSeconds", SettingKeys.FullscreenAutoHideSeconds);
                await ImportSetting(playback, "ThumbnailWidth",           SettingKeys.ThumbnailWidth);
                await ImportSetting(playback, "ThumbnailHeight",          SettingKeys.ThumbnailHeight);
            }

            // ── Player Controls (V3) ────────────────────────────────────────
            if (root.TryGetProperty("PlayerControls", out var controls))
            {
                await ImportSetting(controls, "ShowNavButtons",    SettingKeys.ShowNavButtons);
                await ImportSetting(controls, "ShowFullscreenBtn", SettingKeys.ShowFullscreenBtn);
                await ImportSetting(controls, "ShowAbLoopButtons", SettingKeys.ShowAbLoopButtons);
                await ImportSetting(controls, "ShowSpeedControls", SettingKeys.ShowSpeedControls);
                await ImportSetting(controls, "ShowQueueButton",   SettingKeys.ShowQueueButton);
                await ImportSetting(controls, "ShowOverridesBtn",  SettingKeys.ShowOverridesBtn);
                await ImportSetting(controls, "ShowEqButton",      SettingKeys.ShowEqButton);
                await ImportSetting(controls, "ShowTracksButton",  SettingKeys.ShowTracksButton);
                await ImportSetting(controls, "ShowVolumeControl", SettingKeys.ShowVolumeControl);
            }

            // ── Device Settings (V3) ────────────────────────────────────────
            if (root.TryGetProperty("DeviceSettings", out var device))
            {
                await ImportSetting(device, "Backend",  SettingKeys.Backend);
                await ImportSetting(device, "Protocol", SettingKeys.HandyProtocol);
            }

            // ── Keybindings (V3) ────────────────────────────────────────────
            if (root.TryGetProperty("Keybindings", out var kb) && kb.ValueKind == JsonValueKind.String)
            {
                var kbJson = kb.GetString();
                if (!string.IsNullOrEmpty(kbJson))
                    await _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.Keybindings, kbJson));
            }

            // ── Library Roots (V3) ──────────────────────────────────────────
            if (root.TryGetProperty("LibraryRoots", out var roots) && roots.ValueKind == JsonValueKind.Array)
            {
                var existingRoots = await _dispatcher.QueryAsync(new GetLibraryRootsQuery());
                var existingPaths = new HashSet<string>(existingRoots.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var r in roots.EnumerateArray())
                {
                    var path = r.GetString();
                    if (!string.IsNullOrEmpty(path) && !existingPaths.Contains(path))
                        await _dispatcher.SendAsync(new AddLibraryRootCommand(path));
                }
            }

            // ── Playlists ────────────────────────────────────────────────────
            if (root.TryGetProperty("Playlists", out var playlists))
            {
                foreach (var p in playlists.EnumerateArray())
                {
                    var name = p.GetProperty("Name").GetString() ?? "Imported";
                    var type = p.TryGetProperty("Type", out var t) ? t.GetString() ?? PlaylistTypes.Static : PlaylistTypes.Static;
                    var folder = p.TryGetProperty("FolderPath", out var f) && f.ValueKind != JsonValueKind.Null ? f.GetString() : null;
                    await _playlistRepo.CreateAsync(name, type, folder);

                    if (p.TryGetProperty("FilterJson", out var fj) && fj.ValueKind != JsonValueKind.Null)
                    {
                        // Update filter on the newly created playlist
                        var allPlaylists = await _playlistRepo.GetAllAsync();
                        var created = allPlaylists.LastOrDefault(pl => pl.Name == name);
                        if (created != null)
                            await _playlistRepo.UpdateFilterAsync(created.Id, fj.GetString());
                    }
                    imported++;
                }
            }

            // ── Presets ──────────────────────────────────────────────────────
            if (root.TryGetProperty("Presets", out var presets))
            {
                foreach (var p in presets.EnumerateArray())
                {
                    var preset = new Preset
                    {
                        Name = p.GetProperty("Name").GetString() ?? "Imported",
                        RangeMin = p.TryGetProperty("RangeMin", out var rmin) ? rmin.GetInt32() : 0,
                        RangeMax = p.TryGetProperty("RangeMax", out var rmax) ? rmax.GetInt32() : 100,
                        OffsetMs = p.TryGetProperty("OffsetMs", out var off) ? off.GetInt32() : 0,
                        SpeedLimit = p.TryGetProperty("SpeedLimit", out var spd) && spd.ValueKind != JsonValueKind.Null ? spd.GetDouble() : null,
                        Intensity = p.TryGetProperty("Intensity", out var inten) && inten.ValueKind != JsonValueKind.Null ? inten.GetDouble() : null,
                        Invert = p.TryGetProperty("Invert", out var inv) && inv.GetBoolean(),
                        IsExpert = p.TryGetProperty("IsExpert", out var exp) && exp.GetBoolean(),
                        SmoothingFactor = p.TryGetProperty("SmoothingFactor", out var sf) ? sf.GetDouble() : 0.3,
                        CurveGamma = p.TryGetProperty("CurveGamma", out var cg) ? cg.GetDouble() : 1.0,
                        TickRateMs = p.TryGetProperty("TickRateMs", out var tr) ? tr.GetInt32() : 50
                    };
                    await _presetRepo.CreateAsync(preset);
                    imported++;
                }
            }

            StatusText = $"Imported successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import failed");
            StatusText = $"Import failed: {ex.Message}";
        }
    }

    // ── Logs ─────────────────────────────────────────────────────

    private static string LogsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HandyPlayer", "logs");

    [RelayCommand]
    private async Task SaveLogs(Window window)
    {
        try
        {
            var logsPath = LogsDir;
            if (!Directory.Exists(logsPath) || Directory.GetFiles(logsPath, "*.log").Length == 0)
            {
                StatusText = "No log files found.";
                return;
            }

            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Logs",
                DefaultExtension = "zip",
                SuggestedFileName = $"handyplayer-logs-{DateTime.Now:yyyyMMdd}",
                FileTypeChoices = [new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }]
            });
            if (file == null) return;

            await using var stream = await file.OpenWriteAsync();
            using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
            foreach (var logFile in Directory.GetFiles(logsPath, "*.log"))
            {
                using var logStream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var entry = archive.CreateEntry(Path.GetFileName(logFile));
                await using var entryStream = entry.Open();
                await logStream.CopyToAsync(entryStream);
            }

            StatusText = "Logs saved successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save logs");
            StatusText = $"Save logs failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            var logsPath = LogsDir;
            Directory.CreateDirectory(logsPath);
            Process.Start(new ProcessStartInfo(logsPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open logs folder");
            StatusText = $"Error: {ex.Message}";
        }
    }

    // ── Keybindings ──────────────────────────────────────────────

    private void RefreshKeybindingEntries()
    {
        Keybindings.Clear();
        var map = _keybindingMap;
        AddEntry(nameof(map.PlayPause), map.PlayPause);
        AddEntry(nameof(map.Escape), map.Escape);
        AddEntry(nameof(map.Fullscreen), map.Fullscreen);
        AddEntry(nameof(map.FullscreenAlt), map.FullscreenAlt);
        AddEntry(nameof(map.SeekForward), map.SeekForward);
        AddEntry(nameof(map.SeekBackward), map.SeekBackward);
        AddEntry(nameof(map.NextTrack), map.NextTrack);
        AddEntry(nameof(map.PrevTrack), map.PrevTrack);
        AddEntry(nameof(map.NudgePlus), map.NudgePlus);
        AddEntry(nameof(map.NudgeMinus), map.NudgeMinus);
        AddEntry(nameof(map.ToggleOverrides), map.ToggleOverrides);
        AddEntry(nameof(map.VolumeUp), map.VolumeUp);
        AddEntry(nameof(map.VolumeDown), map.VolumeDown);
        AddEntry(nameof(map.ToggleMute), map.ToggleMute);
        AddEntry(nameof(map.ShowHelp), map.ShowHelp);

        void AddEntry(string action, string key) =>
            Keybindings.Add(new KeybindingEntry
            {
                Action = action,
                DisplayName = KeybindingMap.ActionDisplayNames.GetValueOrDefault(action, action),
                Key = key
            });
    }

    [RelayCommand]
    private void StartKeyCapture(KeybindingEntry? entry)
    {
        if (entry == null) return;
        EditingKeybinding = entry;
        IsCapturingKey = true;
        entry.Key = "Press a key...";
    }

    public void ApplyCapturedKey(string keyName)
    {
        if (EditingKeybinding == null) return;

        EditingKeybinding.Key = keyName;
        IsCapturingKey = false;

        // Update the map
        var prop = typeof(KeybindingMap).GetProperty(EditingKeybinding.Action);
        prop?.SetValue(_keybindingMap, keyName);

        EditingKeybinding = null;
        _ = SaveKeybindingsAsync();
    }

    [RelayCommand]
    private void CancelKeyCapture()
    {
        if (EditingKeybinding != null)
        {
            // Restore original key from map
            var prop = typeof(KeybindingMap).GetProperty(EditingKeybinding.Action);
            EditingKeybinding.Key = (string?)prop?.GetValue(_keybindingMap) ?? "";
        }
        EditingKeybinding = null;
        IsCapturingKey = false;
    }

    [RelayCommand]
    private async Task ResetKeybindings()
    {
        _keybindingMap = new KeybindingMap();
        RefreshKeybindingEntries();
        await SaveKeybindingsAsync();
    }

    private async Task SaveKeybindingsAsync()
    {
        try
        {
            var json = _keybindingMap.ToJson();
            await _dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.Keybindings, json));

            // Hot-reload into MainWindow
            if (GetWindow != null)
            {
                var window = GetWindow();
                if (window is Views.MainWindow mw)
                    mw.ReloadKeybindings(_keybindingMap);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save keybindings");
        }
    }
}

public partial class KeybindingEntry : ObservableObject
{
    public string Action { get; set; } = "";
    public string DisplayName { get; set; } = "";
    [ObservableProperty] private string _key = "";
}
