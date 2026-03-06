namespace HandyPlaylistPlayer.Core.Models;

/// <summary>
/// Upscaling preset. Each option targets a different use-case and GPU tier.
/// </summary>
public enum GlslShaderPreset
{
    None,             // 0 - Default mpv scale filter (spline36) — no extra GPU cost
    Sharpen,          // 1 - CAS — contrast-adaptive sharpening, all content, any GPU
    FsrSpatial,       // 2 - FSR 1.0 — AMD spatial upscaling, all content, any GPU
    NIS,              // 3 - NVIDIA Image Scaling — 6-tap Lanczos + sharpen, any GPU
    RavuLite,         // 4 - RAVU Lite — CNN prescaler, real-world/live-action, any GPU
    FSRCNNX,          // 5 - FSRCNNX 8-0-4-1 — CNN upscaler, real-world, mid-range GPU
    NLMeans,          // 6 - nlmeans — adaptive denoising + sharpening, any content
    Anime4KFast,      // 7 - Anime4K Mode C — upscale-only pipeline, anime, mid-range GPU
    Anime4KQuality,   // 8 - Anime4K Mode A — restore+upscale pipeline, anime, high-end GPU
    MetalFxSpatial,   // 9 - Apple MetalFX Spatial — hardware-accelerated, macOS only
    AutoFast,         // 10 - nlmeans + FSR + CAS — denoise, upscale, sharpen, any GPU
    AutoQuality,      // 11 - nlmeans + FSRCNNX + CAS — denoise, CNN upscale, sharpen, mid-range GPU
}

/// <summary>
/// MPV engine settings loaded at startup. Changes require app restart.
/// </summary>
/// <param name="HardwareDecode">
///   "auto" (default) — let mpv pick; "no" — software decode.
///   Named values: "d3d11va" (Windows), "videotoolbox" (macOS), "vaapi" (Linux).
/// </param>
/// <param name="GpuApi">
///   GPU rendering API. "auto" — let mpv pick the best for the platform.
///   Windows options: "d3d11" (stable default), "vulkan" (potentially faster).
///   Cross-platform fallback: "opengl".
/// </param>
/// <param name="Deband">Remove color banding artefacts. Slightly heavier on GPU.</param>
/// <param name="DemuxerCacheMb">Demuxer read-ahead buffer in MB (default 150 MB).</param>
/// <param name="Scale">
///   Luma upscaling filter. "spline36" balanced default; "bilinear" fastest;
///   "lanczos" sharp; "ewa_lanczossharp" highest quality, heaviest; "mitchell" smooth bicubic.
/// </param>
/// <param name="LoudnormAf">
///   Apply EBU R128 loudness normalization (af=loudnorm). Evens out volume
///   across different videos. Slight CPU overhead.
/// </param>
/// <param name="IccProfileAuto">
///   Use the monitor's ICC color profile for accurate colors (Windows/macOS).
/// </param>
/// <param name="ToneMapping">
///   HDR→SDR tone-mapping algorithm. "auto" lets mpv pick; "hable" filmic;
///   "reinhard" simple and fast; "bt.2446a" standards-compliant; "clip" fastest.
/// </param>
/// <param name="VolumeMax">
///   Maximum mpv volume level (100–200). 100 = no boost; 150 = 50% beyond normal.
///   The volume slider maps linearly to this cap.
/// </param>
/// <param name="VoBackend">
///   Video output backend. "gpu" is the stable default; "gpu-next" is the modern
///   renderer with better GLSL shader support. Requires restart.
/// </param>
/// <param name="ShaderPreset">
///   GLSL shader preset. None disables extra shaders; RAVU Lite is a CNN prescaler for
///   real-world content; Anime4K Fast/Quality target anime. GPU-Next recommended for Anime4K.
/// </param>
/// <param name="VideoSync">
///   Use "display-resample" for display-synced playback to reduce judder.
/// </param>
/// <param name="Dither">
///   Enable dithering (dither-depth=auto, dither=fruit) to reduce banding on 10-bit displays.
/// </param>
/// <param name="SubAuto">
///   Automatically load external subtitle files (sub-auto=fuzzy).
/// </param>
/// <param name="PitchCorrection">
///   Apply scaletempo2 audio filter to correct pitch when speed is changed.
/// </param>
public record MpvSettings(
    string           HardwareDecode  = "auto",
    string           GpuApi          = "auto",
    bool             Deband          = false,
    int              DemuxerCacheMb  = 150,
    string           Scale           = "spline36",
    bool             LoudnormAf      = false,
    bool             IccProfileAuto  = false,
    string           ToneMapping     = "auto",
    int              VolumeMax       = 100,
    string           VoBackend       = "gpu",
    GlslShaderPreset ShaderPreset    = GlslShaderPreset.None,
    bool             VideoSync       = false,
    bool             Dither          = true,
    bool             SubAuto         = true,
    bool             PitchCorrection = true);
