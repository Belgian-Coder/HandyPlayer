namespace HandyPlaylistPlayer.Core;

/// <summary>
/// Constants for app_settings table keys.
/// </summary>
public static class SettingKeys
{
    // Device connection
    public const string ConnectionKey = "connection_key";
    public const string IntifaceUrl = "intiface_url";
    public const string Backend = "backend";
    public const string HandyProtocol = "handy_protocol";

    // Playback
    public const string DefaultOffsetMs = "default_offset_ms";
    public const string SeekStepSeconds = "seek_step_seconds";
    public const string WatchedThresholdPercent   = "watched_threshold_percent";
    public const string GaplessPrepareSeconds     = "gapless_prepare_seconds";
    public const string FullscreenAutoHideSeconds  = "fullscreen_autohide_seconds";
    public const string ThumbnailWidth             = "thumbnail_width";
    public const string ThumbnailHeight            = "thumbnail_height";

    // Queue persistence
    public const string QueueState = "queue_state";

    // Keybindings
    public const string Keybindings = "keybindings";

    // Appearance
    public const string AccentColor              = "accent_color";
    public const string AppTheme                 = "app_theme";
    public const string CustomBgColor            = "custom_bg_color";
    public const string CustomTextColor          = "custom_text_color";
    public const string CustomHighlightTextColor = "custom_highlight_text_color";
    public const string CustomButtonColor        = "custom_button_color";

    // MPV engine (require restart)
    public const string MpvHwDecode       = "mpv_hw_decode";
    public const string MpvGpuApi         = "mpv_gpu_api";
    public const string MpvDeband         = "mpv_deband";
    public const string MpvDemuxerCacheMb = "mpv_demuxer_cache_mb";
    public const string MpvScale          = "mpv_scale";
    public const string MpvLoudnorm       = "mpv_loudnorm";
    public const string MpvIccProfile     = "mpv_icc_profile";
    public const string MpvToneMapping    = "mpv_tone_mapping";
    public const string MpvVolumeMax          = "mpv_volume_max";
    public const string MpvVoBackend          = "mpv_vo_backend";
    public const string MpvGlslShaderPreset   = "mpv_glsl_shader_preset";
    public const string MpvVideoSync       = "mpv_video_sync";
    public const string MpvDither          = "mpv_dither";
    public const string MpvSubAuto         = "mpv_sub_auto";
    public const string MpvPitchCorrection = "mpv_pitch_correction";

    // Resume position + queue restore
    public const string SaveLastPosition         = "save_last_position";
    public const string RestoreQueueOnStartup    = "restore_queue_on_startup";

    // Player control visibility (all default true)
    public const string ShowNavButtons     = "show_nav_buttons";
    public const string ShowFullscreenBtn  = "show_fullscreen_btn";
    public const string ShowAbLoopButtons  = "show_ab_loop_buttons";
    public const string ShowSpeedControls  = "show_speed_controls";
    public const string ShowQueueButton    = "show_queue_button";
    public const string ShowOverridesBtn   = "show_overrides_btn";
    public const string ShowEqButton       = "show_eq_button";
    public const string ShowTracksButton   = "show_tracks_button";
    public const string ShowVolumeControl  = "show_volume_control";
}
