namespace HandyPlaylistPlayer.Core.Models;

public record FunscriptAction(long At, int Pos)
{
    public static FunscriptAction Clamped(long at, int pos) =>
        new(Math.Max(0, at), Math.Clamp(pos, 0, 100));
}
