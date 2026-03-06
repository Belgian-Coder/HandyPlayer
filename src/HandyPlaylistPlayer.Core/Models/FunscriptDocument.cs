namespace HandyPlaylistPlayer.Core.Models;

public class FunscriptDocument
{
    public string Version { get; set; } = "1.0";
    public bool Inverted { get; set; }
    public int Range { get; set; } = 100;
    public List<FunscriptAction> Actions { get; set; } = [];
    public long DurationMs => Actions.Count > 0 ? Actions[^1].At : 0;
}
