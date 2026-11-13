namespace HandyPlaylistPlayer.Core.Models;

/// <summary>
/// A single funscript keyframe. A readonly struct (value equality, same shape as the former record class)
/// so that large scripts are stored contiguously in <see cref="List{T}"/> instead of one heap object per action.
/// </summary>
public readonly record struct FunscriptAction(long At, int Pos)
{
    public static FunscriptAction Clamped(long at, int pos) =>
        new(Math.Max(0, at), Math.Clamp(pos, 0, 100));
}
