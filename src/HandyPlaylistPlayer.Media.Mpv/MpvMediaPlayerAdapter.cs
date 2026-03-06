using System.Runtime.InteropServices;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Media.Mpv;

public record MpvTrack(string Type, int Id, string Title, string Lang, string Codec, bool IsSelected)
{
    public string DisplayName
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrEmpty(Title)) parts.Add(Title);
            if (!string.IsNullOrEmpty(Lang))  parts.Add(Lang);
            if (!string.IsNullOrEmpty(Codec)) parts.Add(Codec);
            if (parts.Count == 0) return $"Track {Id}";
            return $"Track {Id}: {string.Join(" · ", parts)}";
        }
    }
}

/// <summary>
/// IMediaPlayer implementation backed by libmpv.
///
/// Key behaviors that libmpv provides natively (no workarounds needed):
///   - Start paused at first frame: set "pause=yes" before loadfile
///   - Seek while paused with frame update: set "time-pos" directly
///   - Resume from paused: set "pause=no"
///
/// Initialization is two-phase:
///   1. Constructor — mpv_create() + set options
///   2. SetWindowHandle() — mpv_initialize() + start event loop
/// </summary>
public class MpvMediaPlayerAdapter : IMediaPlayer
{
    private readonly ILogger<MpvMediaPlayerAdapter> _logger;
    private readonly IntPtr _mpv;
    private readonly MpvSettings _settings;
    private GlslShaderPreset _shaderPreset;
    private string _baseScale;   // user's configured scale filter; restored when leaving MetalFX
    private int _volumeMax;
    private volatile PlaybackState _state = PlaybackState.Stopped;
    private volatile bool _disposed;
    private volatile bool _fileLoaded; // set true by FileLoaded event; gating flag for PlaybackRestart
    private TaskCompletionSource? _loadTcs;
    private Thread? _eventThread;
    private string? _currentFilePath;

    // ── Software render API (macOS) ──────────────────────────────────────
    private IntPtr _renderCtx;
    private MpvInterop.MpvRenderUpdateFn? _renderUpdateCallback; // prevent GC

    // Observable property IDs for the event loop
    private const ulong ObsTimePos   = 1;
    private const ulong ObsPause     = 2;
    private const ulong ObsDuration  = 3;
    private const ulong ObsEofReached = 4;

    public long PositionMs => (long)(MpvInterop.GetPropertyDouble(_mpv, "time-pos") * 1000);
    public long DurationMs => (long)(MpvInterop.GetPropertyDouble(_mpv, "duration") * 1000);
    public PlaybackState State => _state;

    /// <summary>
    /// Volume in [0, 1] range. Mapped linearly to [0, VolumeMax] in mpv so
    /// the full slider range is usable regardless of the boost setting.
    /// </summary>
    public double Volume
    {
        get => MpvInterop.GetPropertyDouble(_mpv, "volume") / _volumeMax;
        set => MpvInterop.SetPropertyString(_mpv, "volume",
            Math.Clamp(value * _volumeMax, 0, _volumeMax).ToString("F0"));
    }

    public string DetectedHwDecoder { get; private set; } = "Unknown";

    /// <summary>True on macOS where we use mpv's render API (vo=libmpv) instead of --wid.</summary>
    public bool UseSoftwareRendering { get; }

    /// <summary>Fired from mpv's render thread when a new video frame is ready to be rendered.</summary>
    public event Action? FrameAvailable;

    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;
    public event EventHandler<PositionChangedEventArgs>? PositionChanged;
    public event EventHandler? MediaEnded;

    public MpvMediaPlayerAdapter(MpvSettings settings, ILogger<MpvMediaPlayerAdapter> logger)
    {
        _logger = logger;
        _settings = settings;
        UseSoftwareRendering = OperatingSystem.IsMacOS();

        _mpv = MpvInterop.mpv_create();
        if (_mpv == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create() failed");

        // Core options — set before mpv_initialize()
        MpvInterop.SetOptionString(_mpv, "pause",                 "yes");   // always start paused
        MpvInterop.SetOptionString(_mpv, "keep-open",             "yes");   // stay on last frame at EOF
        MpvInterop.SetOptionString(_mpv, "hr-seek",               "yes");   // accurate seeks
        MpvInterop.SetOptionString(_mpv, "input-default-bindings","no");
        MpvInterop.SetOptionString(_mpv, "input-vo-keyboard",     "no");
        MpvInterop.SetOptionString(_mpv, "osc",                   "no");    // no on-screen controls
        MpvInterop.SetOptionString(_mpv, "osd-level",             "0");     // no OSD
        MpvInterop.SetOptionString(_mpv, "terminal",              "no");
        // Keep audio device open with silence so it doesn't pop on first play
        MpvInterop.SetOptionString(_mpv, "audio-stream-silence",  "yes");

        // User-configurable engine settings
        MpvInterop.SetOptionString(_mpv, "hwdec",    settings.HardwareDecode);

        // macOS: use render API (vo=libmpv) — the Metal 'mac' backend doesn't
        // support --wid embedding. gpu-api is irrelevant for vo=libmpv.
        if (UseSoftwareRendering)
        {
            MpvInterop.SetOptionString(_mpv, "vo", "libmpv");
        }
        else
        {
            MpvInterop.SetOptionString(_mpv, "gpu-api", settings.GpuApi);
            MpvInterop.SetOptionString(_mpv, "vo", settings.VoBackend);
        }

        MpvInterop.SetOptionString(_mpv, "deband",   settings.Deband ? "yes" : "no");
        // demuxer-max-bytes in bytes (MpvSettings stores MB)
        MpvInterop.SetOptionString(_mpv, "demuxer-max-bytes",
            (settings.DemuxerCacheMb * 1024L * 1024L).ToString());
        MpvInterop.SetOptionString(_mpv, "scale", settings.Scale);
        MpvInterop.SetOptionString(_mpv, "tone-mapping", settings.ToneMapping);
        MpvInterop.SetOptionString(_mpv, "volume-max", settings.VolumeMax.ToString());
        if (settings.LoudnormAf)
            MpvInterop.SetOptionString(_mpv, "af", "loudnorm");
        if (settings.IccProfileAuto)
            MpvInterop.SetOptionString(_mpv, "icc-profile-auto", "yes");

        // MetalFX Spatial: override scale filter; macOS + gpu-next only
        if (settings.ShaderPreset == GlslShaderPreset.MetalFxSpatial)
            MpvInterop.SetOptionString(_mpv, "scale", "metalfx-spatia");

        // Video sync
        if (settings.VideoSync)
            MpvInterop.SetOptionString(_mpv, "video-sync", "display-resample");

        // Dithering (reduces banding on 10-bit displays)
        if (settings.Dither)
        {
            MpvInterop.SetOptionString(_mpv, "dither-depth", "auto");
            MpvInterop.SetOptionString(_mpv, "dither", "fruit");
        }

        // Subtitle auto-load
        if (settings.SubAuto)
            MpvInterop.SetOptionString(_mpv, "sub-auto", "fuzzy");

        _volumeMax = settings.VolumeMax;
        _shaderPreset = settings.ShaderPreset;
        _baseScale = settings.Scale;
        DetectedHwDecoder = DetectHardwareDecoder(settings.HardwareDecode);
        _logger.LogInformation("mpv created. HW decode: {Hw}, Scale: {Scale}, VolumeMax: {VMax}, VO: {Vo}, Shaders: {Shaders}",
            settings.HardwareDecode, settings.Scale, settings.VolumeMax, settings.VoBackend, settings.ShaderPreset);
    }

    /// <summary>
    /// Called by MpvVideoView once its native window handle is available.
    /// This triggers mpv_initialize() and starts the event loop.
    /// The handle type: HWND on Windows, NSView* on macOS, X11 Window on Linux.
    /// </summary>
    /// <summary>
    /// Initializes mpv. On Windows/Linux, sets --wid for native embedding.
    /// On macOS, uses the render API (vo=libmpv) — hwnd is ignored.
    /// </summary>
    public void SetWindowHandle(IntPtr hwnd)
    {
        if (UseSoftwareRendering)
        {
            // macOS: no wid — we use the render API (vo=libmpv)
            _logger.LogInformation("Initializing mpv with software render API (macOS)");
        }
        else if (OperatingSystem.IsWindows())
        {
            _logger.LogInformation("SetWindowHandle: 0x{Handle:X}", hwnd.ToInt64());
            long wid = hwnd.ToInt64();
            var n = Marshal.StringToCoTaskMemUTF8("wid");
            try { MpvInterop.mpv_set_property(_mpv, n, MpvFormat.Int64, ref wid); }
            finally { Marshal.FreeCoTaskMem(n); }
        }
        else
        {
            // Linux
            _logger.LogInformation("SetWindowHandle: 0x{Handle:X}", hwnd.ToInt64());
            MpvInterop.SetOptionString(_mpv, "wid", hwnd.ToInt64().ToString());
        }

        int err = MpvInterop.mpv_initialize(_mpv);
        if (err < 0)
            _logger.LogError("mpv_initialize failed: {Error}", MpvInterop.GetErrorString(err));

        // Create software render context for macOS (must be after mpv_initialize)
        if (UseSoftwareRendering)
            CreateSoftwareRenderContext();

        // Apply pitch correction after initialization
        SetPitchCorrection(_settings.PitchCorrection);

        // Apply GLSL shader preset after initialization (MetalFX uses --scale set in constructor, not GLSL files)
        if (!UseSoftwareRendering)
        {
            var shaderPaths = GetShaderPaths(_shaderPreset);
            if (shaderPaths.Length > 0)
                SetGlslShaders(shaderPaths);
        }

        // Observe properties for state updates
        MpvInterop.ObserveProperty(_mpv, ObsTimePos,   "time-pos",    MpvFormat.Double);
        MpvInterop.ObserveProperty(_mpv, ObsPause,     "pause",       MpvFormat.Flag);
        MpvInterop.ObserveProperty(_mpv, ObsDuration,  "duration",    MpvFormat.Double);
        MpvInterop.ObserveProperty(_mpv, ObsEofReached, "eof-reached", MpvFormat.Flag);

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    // ── IMediaPlayer ──────────────────────────────────────────────────────

    /// <summary>
    /// Loads media. Ensures the player is paused before loading so the first
    /// frame is shown immediately when the file is ready. Waits for the
    /// PlaybackRestart event (not FileLoaded) before returning — PlaybackRestart fires
    /// after mpv has applied the --pause=yes option, so any subsequent pause=no command
    /// from PlayAsync is guaranteed to win and not be overridden by mpv's initialization.
    /// </summary>
    public async Task LoadAsync(string filePath)
    {
        _logger.LogInformation("LoadAsync: {Path}", filePath);
        _currentFilePath = filePath;

        // Ensure we start paused regardless of previous playback state.
        MpvInterop.SetPropertyString(_mpv, "pause", "yes");

        _fileLoaded = false;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _loadTcs, tcs);

        MpvInterop.Command(_mpv, "loadfile", filePath, "replace");

        // Wait for MPV_EVENT_PLAYBACK_RESTART (fires after FILE_LOADED and after --pause=yes is applied)
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        if (completed != tcs.Task)
            _logger.LogWarning("LoadAsync: timed out waiting for PlaybackRestart");

        Volatile.Write(ref _loadTcs, null);
    }

    public Task PlayAsync()
    {
        _logger.LogDebug("PlayAsync");
        MpvInterop.SetPropertyString(_mpv, "pause", "no");
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        _logger.LogDebug("PauseAsync");
        MpvInterop.SetPropertyString(_mpv, "pause", "yes");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _logger.LogDebug("StopAsync");
        MpvInterop.Command(_mpv, "stop");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Seeks to positionMs. Because mpv natively decodes and displays the
    /// frame at the target position even while paused, no resume-seek-pause
    /// dance is needed. This is the core advantage over LibVLC.
    /// </summary>
    public Task SeekAsync(long positionMs)
    {
        if (_state == PlaybackState.Stopped) return Task.CompletedTask;

        var secs = (positionMs / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        _logger.LogDebug("SeekAsync: {Pos}ms ({Secs}s)", positionMs, secs);
        // "absolute" seek mode, "exact" for precise positioning
        MpvInterop.Command(_mpv, "seek", secs, "absolute", "exact");
        return Task.CompletedTask;
    }

    // ── Event loop ────────────────────────────────────────────────────────

    private void EventLoop()
    {
        while (!_disposed)
        {
            var eventPtr = MpvInterop.mpv_wait_event(_mpv, -1); // block until event
            if (eventPtr == IntPtr.Zero) continue;

            var ev = Marshal.PtrToStructure<MpvEvent>(eventPtr);

            switch (ev.EventId)
            {
                case MpvEventId.Shutdown:
                    return;

                case MpvEventId.FileLoaded:
                    _logger.LogDebug("mpv: FileLoaded");
                    _fileLoaded = true;
                    SetState(PlaybackState.Paused);
                    // Do NOT resolve _loadTcs here — wait for PlaybackRestart which fires
                    // after --pause=yes is applied. Resolving here causes a race where our
                    // subsequent pause=no is overridden by mpv's own --pause option handling.
                    break;

                case MpvEventId.PlaybackRestart:
                    // Only resolve the load TCS if FileLoaded was already seen for this
                    // file — this filters out spurious PlaybackRestart events fired by
                    // seeks (e.g. from coordinator.StopAsync) that arrive before the
                    // new file's FileLoaded event.
                    _logger.LogDebug("mpv: PlaybackRestart (fileLoaded={FileLoaded})", _fileLoaded);
                    if (_fileLoaded)
                    {
                        _fileLoaded = false;
                        Volatile.Read(ref _loadTcs)?.TrySetResult();
                    }
                    break;

                case MpvEventId.EndFile when ev.Data != IntPtr.Zero:
                    var endFile = Marshal.PtrToStructure<MpvEventEndFile>(ev.Data);
                    _logger.LogDebug("mpv: EndFile reason={Reason}", endFile.Reason);
                    // EOF is handled by eof-reached property observer (keep-open=yes suppresses EndFile(Eof))
                    if (endFile.Reason is MpvEndFileReason.Eof or MpvEndFileReason.Stop or MpvEndFileReason.Quit)
                        SetState(PlaybackState.Stopped);
                    break;

                case MpvEventId.PropertyChange when ev.Data != IntPtr.Zero:
                    HandlePropertyChange(ev.ReplyUserdata, ev.Data);
                    break;
            }
        }
    }

    private void HandlePropertyChange(ulong obsId, IntPtr dataPtr)
    {
        var prop = Marshal.PtrToStructure<MpvEventProperty>(dataPtr);

        switch (obsId)
        {
            case ObsTimePos when prop.Format == MpvFormat.Double && prop.Data != IntPtr.Zero:
            {
                var secs = Marshal.PtrToStructure<double>(prop.Data);
                PositionChanged?.Invoke(this, new PositionChangedEventArgs((long)(secs * 1000)));
                break;
            }

            case ObsPause when prop.Format == MpvFormat.Flag && prop.Data != IntPtr.Zero:
            {
                var isPaused = Marshal.PtrToStructure<int>(prop.Data) != 0;
                var newState = isPaused ? PlaybackState.Paused : PlaybackState.Playing;
                if (_state != newState)
                {
                    _logger.LogDebug("mpv: pause property={IsPaused} → {State}", isPaused, newState);
                    SetState(newState);
                }
                break;
            }

            case ObsEofReached when prop.Format == MpvFormat.Flag && prop.Data != IntPtr.Zero:
            {
                var isEof = Marshal.PtrToStructure<int>(prop.Data) != 0;
                if (isEof)
                {
                    _logger.LogDebug("mpv: eof-reached → MediaEnded");
                    SetState(PlaybackState.Stopped);
                    MediaEnded?.Invoke(this, EventArgs.Empty);
                }
                break;
            }
        }
    }

    private void SetState(PlaybackState state)
    {
        _state = state;
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(state));
    }

    // ── Live property updates (no restart needed) ─────────────────────────

    /// <summary>Sets an mpv property at runtime (for options that don't require VO/AO reinit).</summary>
    public void SetLiveProperty(string name, string value) =>
        MpvInterop.SetPropertyString(_mpv, name, value);

    /// <summary>
    /// Updates the standard scale filter and remembers it as the base scale.
    /// This ensures that switching away from MetalFX restores the correct user-chosen filter.
    /// </summary>
    public void UpdateScale(string scale)
    {
        _baseScale = scale;
        MpvInterop.SetPropertyString(_mpv, "scale", scale);
    }

    // ── GLSL shaders ──────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the active GLSL shader list. Uses change-list append (one path at a time)
    /// to avoid the colon-separator conflict on Windows paths.
    /// </summary>
    public void SetGlslShaders(string[] shaderPaths)
    {
        // Clear existing shader list
        MpvInterop.Command(_mpv, "change-list", "glsl-shaders", "clr", "");
        foreach (var path in shaderPaths)
        {
            _logger.LogDebug("SetGlslShaders: append {Path}", path);
            MpvInterop.Command(_mpv, "change-list", "glsl-shaders", "append", path);
        }
    }

    /// <summary>
    /// Switches the active shader preset at runtime — no restart needed.
    /// MetalFX is handled by updating the "scale" property directly; all other presets
    /// work via the glsl-shaders change-list command.
    /// </summary>
    public void UpdateShadersLive(GlslShaderPreset newPreset)
    {
        if (newPreset == GlslShaderPreset.MetalFxSpatial)
        {
            // MetalFX uses the scale filter — clear any GLSL files first, then set the filter.
            SetGlslShaders([]);
            MpvInterop.SetPropertyString(_mpv, "scale", "metalfx-spatia");
        }
        else
        {
            if (_shaderPreset == GlslShaderPreset.MetalFxSpatial)
            {
                // Switching away from MetalFX — restore the user's configured scale filter.
                MpvInterop.SetPropertyString(_mpv, "scale", _baseScale);
            }
            SetGlslShaders(GetShaderPaths(newPreset));
        }
        _shaderPreset = newPreset;

        // Force a redraw so paused video reflects the new shader immediately.
        // Playing video picks up the new shader on the next frame automatically.
        if (_state == PlaybackState.Paused && _currentFilePath != null)
            MpvInterop.Command(_mpv, "seek", "0", "relative", "exact");

        _logger.LogInformation("Shaders updated live: {Preset}", newPreset);
    }

    /// <summary>
    /// Returns the absolute paths for the GLSL shader files in a given preset.
    /// Returns an empty array for presets that use no external GLSL files
    /// (None uses the default scale filter; MetalFxSpatial uses the "scale" property).
    /// </summary>
    public static string[] GetShaderPaths(GlslShaderPreset preset)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "Anime4K");
        string[] fileNames = preset switch
        {
            GlslShaderPreset.Sharpen =>
            [
                "CAS.glsl",
            ],
            GlslShaderPreset.FsrSpatial =>
            [
                "FSR.glsl",
            ],
            GlslShaderPreset.NIS =>
            [
                "NIS.glsl",
            ],
            GlslShaderPreset.RavuLite =>
            [
                "RAVU_Lite.glsl",
            ],
            GlslShaderPreset.FSRCNNX =>
            [
                "FSRCNNX.glsl",
            ],
            GlslShaderPreset.NLMeans =>
            [
                "nlmeans.glsl",
            ],
            GlslShaderPreset.Anime4KFast =>
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Upscale_Denoise_CNN_x2_M.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_S.glsl",
            ],
            GlslShaderPreset.Anime4KQuality =>
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Restore_CNN_M.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_S.glsl",
            ],
            // Auto Fast: denoise + spatial upscale + sharpen. Any GPU.
            GlslShaderPreset.AutoFast =>
            [
                "nlmeans.glsl",
                "FSR.glsl",
                "CAS.glsl",
            ],
            // Auto Quality: denoise + CNN upscale + sharpen. Mid-range GPU.
            GlslShaderPreset.AutoQuality =>
            [
                "nlmeans.glsl",
                "FSRCNNX.glsl",
                "CAS.glsl",
            ],
            _ => [],  // None, MetalFxSpatial use no external GLSL files
        };
        return fileNames.Select(f => Path.Combine(dir, f)).ToArray();
    }

    /// <summary>Updates the volume-max cap and re-scales the current volume proportionally.</summary>
    public void UpdateVolumeMax(int newMax)
    {
        if (newMax < 100) return;
        var current = Volume; // read normalised [0,1] using old _volumeMax
        _volumeMax = newMax;
        MpvInterop.SetPropertyString(_mpv, "volume-max", newMax.ToString());
        Volume = current;     // re-write using new _volumeMax so actual level is unchanged
    }

    // ── Track selection ───────────────────────────────────────────────────

    public List<MpvTrack> GetTracks()
    {
        var tracks = new List<MpvTrack>();
        var countStr = MpvInterop.GetPropertyString(_mpv, "track-list/count");
        if (!int.TryParse(countStr, out var n)) return tracks;
        for (int i = 0; i < n; i++)
        {
            var type  = MpvInterop.GetPropertyString(_mpv, $"track-list/{i}/type")     ?? "";
            var id    = MpvInterop.GetPropertyString(_mpv, $"track-list/{i}/id")       ?? "0";
            var title = MpvInterop.GetPropertyString(_mpv, $"track-list/{i}/title")    ?? "";
            var lang  = MpvInterop.GetPropertyString(_mpv, $"track-list/{i}/lang")     ?? "";
            var codec = MpvInterop.GetPropertyString(_mpv, $"track-list/{i}/codec")    ?? "";
            var sel   = MpvInterop.GetPropertyString(_mpv, $"track-list/{i}/selected") == "yes";
            if (int.TryParse(id, out var idInt))
                tracks.Add(new MpvTrack(type, idInt, title, lang, codec, sel));
        }
        return tracks;
    }

    public void SelectAudioTrack(int id)    => SetLiveProperty("aid", id.ToString());
    public void SelectSubtitleTrack(int id) => SetLiveProperty("sid", id.ToString());
    public void DisableSubtitles()          => SetLiveProperty("sid", "no");

    public void SetPitchCorrection(bool enabled)
    {
        try
        {
            if (enabled)
                SetLiveProperty("af-add", "scaletempo2");
            else
                SetLiveProperty("af-remove", "@scaletempo2");
        }
        catch { /* ignore if not supported */ }
    }

    /// <summary>
    /// Applies a VO-level option (e.g. gpu-api, icc-profile-auto) that cannot be changed
    /// via a simple property set. Saves the current position and play/pause state, reloads
    /// the current file so mpv reinitializes its video output with the new option, then
    /// restores position. If no file is loaded the option is queued for the next load.
    /// </summary>
    public async Task ReinitVoAsync(string optionName, string optionValue)
    {
        _logger.LogInformation("ReinitVo: {Option}={Value}", optionName, optionValue);
        MpvInterop.SetOptionString(_mpv, optionName, optionValue);

        if (_currentFilePath == null || _state == PlaybackState.Stopped)
            return;

        var posMs  = PositionMs;
        var resume = _state == PlaybackState.Playing;

        await LoadAsync(_currentFilePath);   // reloads file → mpv reinits VO with new option

        if (posMs > 1000)
        {
            var secs = (posMs / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            MpvInterop.Command(_mpv, "seek", secs, "absolute", "exact");
        }

        if (resume)
            MpvInterop.SetPropertyString(_mpv, "pause", "no");
    }

    // ── Software render API (macOS) ──────────────────────────────────────

    private void CreateSoftwareRenderContext()
    {
        var apiTypeStr = Marshal.StringToCoTaskMemUTF8("sw");
        try
        {
            var createParams = new MpvRenderParam[]
            {
                new() { Type = MpvRenderParamType.ApiType, Data = apiTypeStr },
                new() { Type = MpvRenderParamType.Invalid, Data = IntPtr.Zero }
            };

            int err = MpvInterop.mpv_render_context_create(out _renderCtx, _mpv, createParams);
            if (err < 0)
            {
                _logger.LogError("mpv_render_context_create failed: {Error}", MpvInterop.GetErrorString(err));
                return;
            }

            _renderUpdateCallback = OnRenderUpdate;
            MpvInterop.mpv_render_context_set_update_callback(_renderCtx, _renderUpdateCallback, IntPtr.Zero);
            _logger.LogInformation("Software render context created successfully");
        }
        finally
        {
            Marshal.FreeCoTaskMem(apiTypeStr);
        }
    }

    private void OnRenderUpdate(IntPtr ctx)
    {
        FrameAvailable?.Invoke();
    }

    /// <summary>
    /// Renders the current video frame into the provided RGBA buffer.
    /// Called from MpvSoftwareVideoView.Render() on the UI thread.
    /// </summary>
    public bool RenderSoftwareFrame(IntPtr buffer, int width, int height, int stride)
    {
        if (_renderCtx == IntPtr.Zero) return false;

        var sizeData = Marshal.AllocCoTaskMem(8);
        var fmtStr = Marshal.StringToCoTaskMemUTF8("rgba");
        var strideData = Marshal.AllocCoTaskMem(IntPtr.Size);

        try
        {
            Marshal.WriteInt32(sizeData, 0, width);
            Marshal.WriteInt32(sizeData, 4, height);
            Marshal.WriteIntPtr(strideData, (IntPtr)stride);

            var renderParams = new MpvRenderParam[]
            {
                new() { Type = MpvRenderParamType.SwSize,    Data = sizeData },
                new() { Type = MpvRenderParamType.SwFormat,  Data = fmtStr },
                new() { Type = MpvRenderParamType.SwStride,  Data = strideData },
                new() { Type = MpvRenderParamType.SwPointer, Data = buffer },
                new() { Type = MpvRenderParamType.Invalid,   Data = IntPtr.Zero }
            };

            int err = MpvInterop.mpv_render_context_render(_renderCtx, renderParams);
            if (err >= 0)
                MpvInterop.mpv_render_context_report_swap(_renderCtx);
            return err >= 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(sizeData);
            Marshal.FreeCoTaskMem(fmtStr);
            Marshal.FreeCoTaskMem(strideData);
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Free render context before destroying mpv
        if (_renderCtx != IntPtr.Zero)
        {
            MpvInterop.mpv_render_context_set_update_callback(_renderCtx, null, IntPtr.Zero);
            MpvInterop.mpv_render_context_free(_renderCtx);
            _renderCtx = IntPtr.Zero;
        }

        MpvInterop.mpv_wakeup(_mpv);   // wake up event loop so it sees _disposed
        _eventThread?.Join(TimeSpan.FromSeconds(2));
        MpvInterop.mpv_terminate_destroy(_mpv);
    }

    private static string DetectHardwareDecoder(string setting)
    {
        if (setting == "no") return "Software";
        if (OperatingSystem.IsWindows()) return "D3D11VA";
        if (OperatingSystem.IsMacOS())  return "VideoToolbox";
        return "VA-API";
    }
}
