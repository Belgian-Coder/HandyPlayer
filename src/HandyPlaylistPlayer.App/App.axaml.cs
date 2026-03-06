using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HandyPlaylistPlayer.App.ViewModels;
using HandyPlaylistPlayer.App.Views;
using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Features.Device.ConnectDevice;
using HandyPlaylistPlayer.Core.Features.Device.DisconnectDevice;
using HandyPlaylistPlayer.Core.Features.Device.UpdateTransformSettings;
using HandyPlaylistPlayer.Core.Features.Library.AddLibraryRoot;
using HandyPlaylistPlayer.Core.Features.Library.FindScriptForVideo;
using HandyPlaylistPlayer.Core.Features.Library.GetLibraryItems;
using HandyPlaylistPlayer.Core.Features.Library.GetLibraryRoots;
using HandyPlaylistPlayer.Core.Features.Library.RemoveLibraryRoot;
using HandyPlaylistPlayer.Core.Features.Library.RunAutoPairing;
using HandyPlaylistPlayer.Core.Features.Library.DeleteMediaFiles;
using HandyPlaylistPlayer.Core.Features.Library.ScanLibrary;
using HandyPlaylistPlayer.Core.Features.Library.ToggleWatched;
using HandyPlaylistPlayer.Core.Features.Library.FindDuplicates;
using HandyPlaylistPlayer.Core.Features.Library.UpdatePairingOffset;
using HandyPlaylistPlayer.Core.Features.Library.GetMissingFiles;
using HandyPlaylistPlayer.Core.Features.Library.RelocateMediaFile;
using HandyPlaylistPlayer.Core.Features.PatternMode.GeneratePattern;
using HandyPlaylistPlayer.Core.Features.Playback.EmergencyStop;
using HandyPlaylistPlayer.Core.Features.Playback.LoadMedia;
using HandyPlaylistPlayer.Core.Features.Playback.PlayPause;
using HandyPlaylistPlayer.Core.Features.Playback.Seek;
using HandyPlaylistPlayer.Core.Features.Playback.Stop;
using HandyPlaylistPlayer.Core.Features.Playlists.AddPlaylistItem;
using HandyPlaylistPlayer.Core.Features.Playlists.CreatePlaylist;
using HandyPlaylistPlayer.Core.Features.Playlists.DeletePlaylist;
using HandyPlaylistPlayer.Core.Features.Playlists.GetAllPlaylists;
using HandyPlaylistPlayer.Core.Features.Playlists.GetPlaylistItems;
using HandyPlaylistPlayer.Core.Features.Playlists.RemovePlaylistItem;
using HandyPlaylistPlayer.Core.Features.Presets.CreatePreset;
using HandyPlaylistPlayer.Core.Features.Presets.DeletePreset;
using HandyPlaylistPlayer.Core.Features.Presets.GetAllPresets;
using HandyPlaylistPlayer.Core.Features.Queue.ClearQueue;
using HandyPlaylistPlayer.Core.Features.Queue.EnqueueItems;
using HandyPlaylistPlayer.Core.Features.Queue.EnqueueNext;
using HandyPlaylistPlayer.Core.Features.Queue.NextTrack;
using HandyPlaylistPlayer.Core.Features.Queue.PreviousTrack;
using HandyPlaylistPlayer.Core.Features.Queue.RemoveFromQueue;
using HandyPlaylistPlayer.Core.Features.Queue.ReorderQueue;
using HandyPlaylistPlayer.Core.Features.Queue.ShuffleQueue;
using HandyPlaylistPlayer.Core.Features.Settings.GetSetting;
using HandyPlaylistPlayer.Core.Features.Settings.SaveSetting;
using HandyPlaylistPlayer.Core;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Runtime;
using HandyPlaylistPlayer.Core.Services;
using HandyPlaylistPlayer.Devices.HandyApi;
using HandyPlaylistPlayer.Media.Mpv;
using HandyPlaylistPlayer.Storage;
using HandyPlaylistPlayer.Storage.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System.IO.Abstractions;

namespace HandyPlaylistPlayer.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        var provider = services.BuildServiceProvider();
        Services = provider;

        // Run migrations synchronously on thread pool to avoid SynchronizationContext deadlock.
        // This must complete before ViewModels access the DB, and must be synchronous
        // because Avalonia requires MainWindow to be set before this method returns.
        Task.Run(() => provider.GetRequiredService<DatabaseMigrator>().MigrateAsync()).GetAwaiter().GetResult();

        // Wire thumbnail directory into QueuePanelViewModel for hover-preview tooltips
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HandyPlayer");
        provider.GetRequiredService<QueuePanelViewModel>().SetThumbnailDir(Path.Combine(appDataDir, "thumbnails"));

        // Register shared brush instances BEFORE any controls are created.
        // This ensures all DynamicResource bindings are anchored to our mutable instances
        // so Apply() / ApplyCustomColor() / AppThemes.Apply() can mutate Color in-place for instant updates.
        Themes.AccentColors.Initialize();
        Themes.AppThemes.Initialize();

        // Apply saved theme + accent synchronously so the window renders with the correct colors immediately.
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HandyPlayer", "handy.db");
        LoadAndApplyAppearance(dbPath);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>()
            };

            desktop.MainWindow.Closing += (_, e) =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                // Persist queue state
                try
                {
                    var queue = provider.GetService<IQueueService>();
                    var dispatcher = provider.GetService<Core.Dispatching.IDispatcher>();
                    if (queue != null && dispatcher != null && queue.Items.Count > 0)
                    {
                        var state = new { VideoIds = queue.Items.Select(i => i.Video.Id).ToArray(), queue.CurrentIndex };
                        var json = System.Text.Json.JsonSerializer.Serialize(state);
                        Task.Run(() => dispatcher.SendAsync(new SaveSettingCommand(SettingKeys.QueueState, json)), cts.Token).Wait(cts.Token);
                    }
                }
                catch (Exception ex) { Log.Debug(ex, "Failed to persist queue state on shutdown"); }

                try
                {
                    var coordinator = provider.GetService<IPlaybackCoordinator>();
                    if (coordinator != null)
                        Task.Run(() => coordinator.StopAsync(), cts.Token).Wait(cts.Token);
                }
                catch (Exception ex) { Log.Debug(ex, "Failed to stop playback on shutdown"); }

                try
                {
                    Task.Run(() => provider.DisposeAsync().AsTask(), cts.Token).Wait(cts.Token);
                }
                catch (Exception ex) { Log.Debug(ex, "Failed to dispose services on shutdown"); }

                Log.CloseAndFlush();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dataDir = Path.Combine(appData, "HandyPlayer");

        // Migrate from old name if needed
        var oldDir = Path.Combine(appData, "HandyPlaylistPlayer");
        if (!Directory.Exists(dataDir) && Directory.Exists(oldDir))
            Directory.Move(oldDir, dataDir);

        Directory.CreateDirectory(dataDir);

        // Logging
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(dataDir, "logs", "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(logger);
        });

        // Storage
        var dbPath = Path.Combine(dataDir, "handy.db");

        services.AddSingleton(new DatabaseConfig(dbPath));
        services.AddSingleton(LoadMpvSettings(dbPath));
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<AppSettingsRepository>();
        services.AddSingleton<IAppSettingsRepository>(sp => sp.GetRequiredService<AppSettingsRepository>());
        services.AddSingleton<ScriptCacheRepository>();
        services.AddSingleton<IScriptCacheService>(sp => sp.GetRequiredService<ScriptCacheRepository>());
        services.AddSingleton<LibraryRootRepository>();
        services.AddSingleton<ILibraryRootRepository>(sp => sp.GetRequiredService<LibraryRootRepository>());
        services.AddSingleton<MediaFileRepository>();
        services.AddSingleton<IMediaFileRepository>(sp => sp.GetRequiredService<MediaFileRepository>());
        services.AddSingleton<PairingRepository>();
        services.AddSingleton<IPairingRepository>(sp => sp.GetRequiredService<PairingRepository>());
        services.AddSingleton<PlaylistRepository>();
        services.AddSingleton<IPlaylistRepository>(sp => sp.GetRequiredService<PlaylistRepository>());
        services.AddSingleton<PresetRepository>();
        services.AddSingleton<IPresetRepository>(sp => sp.GetRequiredService<PresetRepository>());
        services.AddSingleton<PlaybackHistoryRepository>();
        services.AddSingleton<IPlaybackHistoryRepository>(sp => sp.GetRequiredService<PlaybackHistoryRepository>());

        // FileSystem
        services.AddSingleton<IFileSystem>(new FileSystem());

        // Core services
        services.AddSingleton<IFunscriptParser, FunscriptParser>();
        services.AddSingleton<IFilenameNormalizer, FilenameNormalizer>();
        services.AddSingleton<IAutoPairingEngine, AutoPairingEngine>();
        services.AddSingleton<ILibraryIndexer, LibraryIndexer>();
        services.AddSingleton<IQueueService, QueueService>();
        services.AddSingleton<IPlaybackCoordinator, PlaybackCoordinator>();
        services.AddSingleton<IEmergencyStopService, EmergencyStopService>();
        services.AddSingleton<LibraryWatcher>();

        // Media player
        services.AddSingleton<MpvMediaPlayerAdapter>();
        var thumbnailDir = Path.Combine(dataDir, "thumbnails");
        var (thumbW, thumbH) = LoadThumbnailDimensions(dbPath);
        services.AddSingleton<IThumbnailService>(sp =>
            new MpvThumbnailGenerator(thumbnailDir, sp.GetRequiredService<ILogger<MpvThumbnailGenerator>>(), thumbW, thumbH));

        // Device backends
        services.AddSingleton<HandyApiClient>();
        services.AddSingleton<HandyHostingClient>();
        services.AddSingleton<IScriptHostingService>(sp => sp.GetRequiredService<HandyHostingClient>());
        services.AddSingleton<ServerTimeSyncService>();

        // Dispatcher
        services.AddDispatcher();

        // Command handlers — Playlists
        services.AddCommandHandler<CreatePlaylistCommand, int, CreatePlaylistHandler>();
        services.AddCommandHandler<DeletePlaylistCommand, Unit, DeletePlaylistHandler>();
        services.AddCommandHandler<AddPlaylistItemCommand, Unit, AddPlaylistItemHandler>();
        services.AddCommandHandler<RemovePlaylistItemCommand, Unit, RemovePlaylistItemHandler>();

        // Query handlers — Playlists
        services.AddQueryHandler<GetAllPlaylistsQuery, List<Playlist>, GetAllPlaylistsHandler>();
        services.AddQueryHandler<GetPlaylistItemsQuery, List<MediaItem>, GetPlaylistItemsHandler>();

        // Command handlers — Presets
        services.AddCommandHandler<CreatePresetCommand, int, CreatePresetHandler>();
        services.AddCommandHandler<DeletePresetCommand, Unit, DeletePresetHandler>();

        // Query handlers — Presets
        services.AddQueryHandler<GetAllPresetsQuery, List<Preset>, GetAllPresetsHandler>();

        // Command handlers — Settings
        services.AddCommandHandler<SaveSettingCommand, Unit, SaveSettingHandler>();
        // Query handlers — Settings
        services.AddQueryHandler<GetSettingQuery, string?, GetSettingHandler>();

        // Command handlers — Library
        services.AddCommandHandler<ScanLibraryCommand, Unit, ScanLibraryHandler>();
        services.AddCommandHandler<ScanLibraryRootCommand, Unit, ScanLibraryRootHandler>();
        services.AddCommandHandler<AddLibraryRootCommand, int, AddLibraryRootHandler>();
        services.AddCommandHandler<RemoveLibraryRootCommand, Unit, RemoveLibraryRootHandler>();
        services.AddCommandHandler<RunAutoPairingCommand, Unit, RunAutoPairingHandler>();
        services.AddCommandHandler<DeleteMediaFilesCommand, Unit, DeleteMediaFilesHandler>();
        services.AddCommandHandler<ToggleWatchedCommand, Unit, ToggleWatchedHandler>();
        services.AddCommandHandler<UpdatePairingOffsetCommand, Unit, UpdatePairingOffsetHandler>();
        services.AddCommandHandler<RelocateMediaFileCommand, Unit, RelocateMediaFileHandler>();

        // Query handlers — Library
        services.AddQueryHandler<GetLibraryItemsQuery, List<MediaItem>, GetLibraryItemsHandler>();
        services.AddQueryHandler<GetLibraryRootsQuery, List<LibraryRoot>, GetLibraryRootsHandler>();
        services.AddQueryHandler<FindScriptForVideoQuery, MediaItem?, FindScriptForVideoHandler>();
        services.AddQueryHandler<FindDuplicatesQuery, List<DuplicateGroup>, FindDuplicatesHandler>();
        services.AddQueryHandler<GetMissingFilesQuery, List<MediaItem>, GetMissingFilesHandler>();

        // Command handlers — Playback
        services.AddCommandHandler<LoadMediaCommand, Unit, LoadMediaHandler>();
        services.AddCommandHandler<PlayPauseCommand, Unit, PlayPauseHandler>();
        services.AddCommandHandler<StopPlaybackCommand, Unit, StopPlaybackHandler>();
        services.AddCommandHandler<SeekCommand, Unit, SeekHandler>();
        services.AddCommandHandler<EmergencyStopCommand, Unit, EmergencyStopHandler>();

        // Command handlers — Queue
        services.AddCommandHandler<EnqueueItemsCommand, Unit, EnqueueItemsHandler>();
        services.AddCommandHandler<EnqueueNextCommand, Unit, EnqueueNextHandler>();
        services.AddCommandHandler<RemoveFromQueueCommand, Unit, RemoveFromQueueHandler>();
        services.AddCommandHandler<ReorderQueueCommand, Unit, ReorderQueueHandler>();
        services.AddCommandHandler<ClearQueueCommand, Unit, ClearQueueHandler>();
        services.AddCommandHandler<NextTrackCommand, QueueItem?, NextTrackHandler>();
        services.AddCommandHandler<PreviousTrackCommand, QueueItem?, PreviousTrackHandler>();
        services.AddCommandHandler<ShuffleQueueCommand, Unit, ShuffleQueueHandler>();

        // Command handlers — Device
        services.AddCommandHandler<ConnectDeviceCommand, Unit, ConnectDeviceHandler>();
        services.AddCommandHandler<DisconnectDeviceCommand, Unit, DisconnectDeviceHandler>();
        services.AddCommandHandler<UpdateTransformSettingsCommand, Unit, UpdateTransformSettingsHandler>();

        // Command handlers — PatternMode
        services.AddCommandHandler<GeneratePatternCommand, Unit, GeneratePatternHandler>();

        // Validators
        services.AddValidator<CreatePlaylistCommand, CreatePlaylistValidator>();
        services.AddValidator<CreatePresetCommand, CreatePresetValidator>();
        services.AddValidator<AddLibraryRootCommand, AddLibraryRootValidator>();
        services.AddValidator<LoadMediaCommand, LoadMediaValidator>();
        services.AddValidator<SeekCommand, SeekValidator>();
        services.AddValidator<UpdateTransformSettingsCommand, UpdateTransformSettingsValidator>();
        services.AddValidator<GeneratePatternCommand, GeneratePatternValidator>();

        // ViewModels (Singleton — each VM subscribes to singleton service events in its constructor)
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<LibrarySettingsViewModel>();
        services.AddSingleton<PlaylistListViewModel>();
        services.AddSingleton<PlaylistDetailViewModel>();
        services.AddSingleton<PatternModeViewModel>();
        services.AddSingleton<OverrideControlsViewModel>();
        services.AddSingleton<QueuePanelViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<StatsViewModel>();
    }

    private static MpvSettings LoadMpvSettings(string dbPath)
    {
        if (!File.Exists(dbPath))
            return new MpvSettings();

        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            string hwDecode     = "auto";
            string gpuApi       = "auto";
            bool   deband       = false;
            int    cacheMb      = 150;
            string scale        = "spline36";
            bool   loudnorm     = false;
            bool   iccProfile   = false;
            string toneMapping  = "auto";
            int    volumeMax    = 100;
            string voBackend    = "gpu";
            var    shaderPreset = HandyPlaylistPlayer.Core.Models.GlslShaderPreset.None;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM app_settings WHERE key LIKE 'mpv_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key   = reader.GetString(0);
                var value = reader.GetString(1);
                switch (key)
                {
                    case SettingKeys.MpvHwDecode:       hwDecode = value; break;
                    case SettingKeys.MpvGpuApi:         gpuApi = value; break;
                    case SettingKeys.MpvDeband:         deband = value == "true"; break;
                    case SettingKeys.MpvDemuxerCacheMb:
                        if (int.TryParse(value, out var mb)) cacheMb = mb; break;
                    case SettingKeys.MpvScale:          scale = value; break;
                    case SettingKeys.MpvLoudnorm:       loudnorm = value == "true"; break;
                    case SettingKeys.MpvIccProfile:     iccProfile = value == "true"; break;
                    case SettingKeys.MpvToneMapping:    toneMapping = value; break;
                    case SettingKeys.MpvVolumeMax:
                        if (int.TryParse(value, out var vmax) && vmax >= 100) volumeMax = vmax; break;
                    case SettingKeys.MpvVoBackend:      voBackend = value; break;
                    case SettingKeys.MpvGlslShaderPreset:
                        if (Enum.TryParse<HandyPlaylistPlayer.Core.Models.GlslShaderPreset>(value, true, out var sp))
                            shaderPreset = sp;
                        break;
                }
            }

            // MetalFX Spatial is macOS-only; fall back to None on other platforms
            if (shaderPreset == HandyPlaylistPlayer.Core.Models.GlslShaderPreset.MetalFxSpatial
                && !OperatingSystem.IsMacOS())
                shaderPreset = HandyPlaylistPlayer.Core.Models.GlslShaderPreset.None;

            return new MpvSettings(hwDecode, gpuApi, deband, cacheMb,
                scale, loudnorm, iccProfile, toneMapping, volumeMax, voBackend, shaderPreset);
        }
        catch
        {
            return new MpvSettings();
        }
    }

    private static (int width, int height) LoadThumbnailDimensions(string dbPath)
    {
        int w = 320, h = 180;
        if (!File.Exists(dbPath)) return (w, h);
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM app_settings WHERE key IN ('thumbnail_width','thumbnail_height')";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                if (int.TryParse(reader.GetString(1), out var val) && val > 0)
                {
                    if (key == "thumbnail_width") w = val;
                    else if (key == "thumbnail_height") h = val;
                }
            }
        }
        catch { /* use defaults */ }
        return (w, h);
    }

    private static void LoadAndApplyAppearance(string dbPath)
    {
        if (!File.Exists(dbPath)) return;
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT key, value FROM app_settings WHERE key IN ('{SettingKeys.AppTheme}', '{SettingKeys.AccentColor}')";
            using var reader = cmd.ExecuteReader();

            string? themeName = null, accentName = null;
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var val = reader.GetString(1);
                if (key == SettingKeys.AppTheme)   themeName  = val;
                if (key == SettingKeys.AccentColor) accentName = val;
            }

            // Apply theme first (also sets default accent); then override with saved accent if any
            var theme = Themes.AppThemes.GetByName(themeName);
            Themes.AppThemes.Apply(theme);

            if (accentName?.StartsWith('#') == true
                && Avalonia.Media.Color.TryParse(accentName, out var customColor))
            {
                Themes.AccentColors.ApplyCustomColor(customColor);
            }
            else if (accentName != null)
            {
                Themes.AccentColors.Apply(Themes.AccentColors.GetByName(accentName));
            }
        }
        catch { /* non-fatal — defaults will be used */ }
    }
}
