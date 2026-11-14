using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Media.Mpv;

/// <summary>
/// The video player surface the view models use: playback (<see cref="IMediaPlayer"/>) plus the mpv-specific
/// track, property and renderer controls. Implemented by <see cref="MpvMediaPlayerAdapter"/>; UI tests substitute a
/// fake so no native mpv instance (and no libmpv) is needed.
/// </summary>
public interface IVideoPlayer : IMediaPlayer
{
    /// <summary>Hardware decoder mpv picked, for display in settings.</summary>
    string DetectedHwDecoder { get; }

    void SetLiveProperty(string name, string value);
    void UpdateScale(string scale);
    void UpdateShadersLive(GlslShaderPreset newPreset);
    void UpdateVolumeMax(int newMax);
    void SetPitchCorrection(bool enabled);
    Task ReinitVoAsync(string optionName, string optionValue);

    List<MpvTrack> GetTracks();
    void SelectAudioTrack(int id);
    void SelectSubtitleTrack(int id);
    void DisableSubtitles();
}
