using System.Text.Json;
using System.Text.Json.Serialization;

namespace HandyPlaylistPlayer.Core.Models;

public class KeybindingMap
{
    [JsonPropertyName("playPause")] public string PlayPause { get; set; } = "Space";
    [JsonPropertyName("escape")] public string Escape { get; set; } = "Escape";
    [JsonPropertyName("fullscreen")] public string Fullscreen { get; set; } = "F11";
    [JsonPropertyName("fullscreenAlt")] public string FullscreenAlt { get; set; } = "F";
    [JsonPropertyName("seekForward")] public string SeekForward { get; set; } = "Right";
    [JsonPropertyName("seekBackward")] public string SeekBackward { get; set; } = "Left";
    [JsonPropertyName("nextTrack")] public string NextTrack { get; set; } = "N";
    [JsonPropertyName("nextTrackAlt")] public string NextTrackAlt { get; set; } = "PageDown";
    [JsonPropertyName("prevTrack")] public string PrevTrack { get; set; } = "P";
    [JsonPropertyName("prevTrackAlt")] public string PrevTrackAlt { get; set; } = "PageUp";
    [JsonPropertyName("nudgePlus")] public string NudgePlus { get; set; } = "OemPlus";
    [JsonPropertyName("nudgeMinus")] public string NudgeMinus { get; set; } = "OemMinus";
    [JsonPropertyName("toggleOverrides")] public string ToggleOverrides { get; set; } = "O";
    [JsonPropertyName("volumeUp")] public string VolumeUp { get; set; } = "Up";
    [JsonPropertyName("volumeDown")] public string VolumeDown { get; set; } = "Down";
    [JsonPropertyName("toggleMute")] public string ToggleMute { get; set; } = "M";
    [JsonPropertyName("showHelp")] public string ShowHelp { get; set; } = "OemQuestion";
    [JsonPropertyName("setLoopA")] public string SetLoopA { get; set; } = "OemOpenBrackets";
    [JsonPropertyName("setLoopB")] public string SetLoopB { get; set; } = "OemCloseBrackets";
    [JsonPropertyName("clearLoop")] public string ClearLoop { get; set; } = "OemPipe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static KeybindingMap FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<KeybindingMap>(json, JsonOptions) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Returns the action name for a given key string, or null if not bound.</summary>
    public string? GetAction(string keyName)
    {
        if (keyName == PlayPause) return nameof(PlayPause);
        if (keyName == Escape) return nameof(Escape);
        if (keyName == Fullscreen || keyName == FullscreenAlt) return nameof(Fullscreen);
        if (keyName == SeekForward) return nameof(SeekForward);
        if (keyName == SeekBackward) return nameof(SeekBackward);
        if (keyName == NextTrack || keyName == NextTrackAlt) return nameof(NextTrack);
        if (keyName == PrevTrack || keyName == PrevTrackAlt) return nameof(PrevTrack);
        if (keyName == NudgePlus) return nameof(NudgePlus);
        // Also match Add key for nudge plus
        if (keyName == "Add" && NudgePlus == "OemPlus") return nameof(NudgePlus);
        if (keyName == NudgeMinus) return nameof(NudgeMinus);
        // Also match Subtract key for nudge minus
        if (keyName == "Subtract" && NudgeMinus == "OemMinus") return nameof(NudgeMinus);
        if (keyName == ToggleOverrides) return nameof(ToggleOverrides);
        if (keyName == VolumeUp) return nameof(VolumeUp);
        if (keyName == VolumeDown) return nameof(VolumeDown);
        if (keyName == ToggleMute) return nameof(ToggleMute);
        if (keyName == ShowHelp) return nameof(ShowHelp);
        if (keyName == SetLoopA) return nameof(SetLoopA);
        if (keyName == SetLoopB) return nameof(SetLoopB);
        if (keyName == ClearLoop) return nameof(ClearLoop);
        return null;
    }

    /// <summary>Friendly display names for UI.</summary>
    public static readonly Dictionary<string, string> ActionDisplayNames = new()
    {
        [nameof(PlayPause)] = "Play / Pause",
        [nameof(Escape)] = "Escape / Emergency Stop",
        [nameof(Fullscreen)] = "Toggle Fullscreen",
        [nameof(FullscreenAlt)] = "Fullscreen (Alt)",
        [nameof(SeekForward)] = "Seek Forward",
        [nameof(SeekBackward)] = "Seek Backward",
        [nameof(NextTrack)] = "Next Track",
        [nameof(NextTrackAlt)] = "Next Track (Alt)",
        [nameof(PrevTrack)] = "Previous Track",
        [nameof(PrevTrackAlt)] = "Previous Track (Alt)",
        [nameof(NudgePlus)] = "Nudge Offset +",
        [nameof(NudgeMinus)] = "Nudge Offset -",
        [nameof(ToggleOverrides)] = "Toggle Overrides",
        [nameof(VolumeUp)] = "Volume Up",
        [nameof(VolumeDown)] = "Volume Down",
        [nameof(ToggleMute)] = "Toggle Mute",
        [nameof(ShowHelp)] = "Show Help",
        [nameof(SetLoopA)] = "Set Loop A",
        [nameof(SetLoopB)] = "Set Loop B",
        [nameof(ClearLoop)] = "Clear Loop"
    };
}
