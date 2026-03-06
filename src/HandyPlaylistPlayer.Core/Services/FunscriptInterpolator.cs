using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Services;

public static class FunscriptInterpolator
{
    public static int GetPositionAtTime(long timeMs, IReadOnlyList<FunscriptAction> actions)
    {
        if (actions.Count == 0) return 50;
        if (timeMs <= actions[0].At) return actions[0].Pos;
        if (timeMs >= actions[^1].At) return actions[^1].Pos;

        // Binary search for the surrounding actions
        int lo = 0, hi = actions.Count - 1;
        while (lo < hi - 1)
        {
            int mid = (lo + hi) / 2;
            if (actions[mid].At <= timeMs)
                lo = mid;
            else
                hi = mid;
        }

        var a = actions[lo];
        var b = actions[hi];
        if (b.At == a.At) return a.Pos;

        // Linear interpolation
        double t = (double)(timeMs - a.At) / (b.At - a.At);
        return (int)Math.Round(a.Pos + t * (b.Pos - a.Pos));
    }

    /// <summary>
    /// Returns the interpolated funscript position at <paramref name="timeMs"/>
    /// normalized to [0, 1] (where 1 = top of stroke, 0 = bottom). Returns 0 for empty scripts.
    /// </summary>
    public static double GetNormalizedPosition(long timeMs, IReadOnlyList<FunscriptAction> actions)
    {
        if (actions.Count == 0) return 0.0;
        return Math.Clamp(GetPositionAtTime(timeMs, actions) / 100.0, 0.0, 1.0);
    }

    public static (FunscriptAction Action, int Index)? GetNextAction(long timeMs, IReadOnlyList<FunscriptAction> actions)
    {
        if (actions.Count == 0) return null;

        int lo = 0, hi = actions.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (actions[mid].At <= timeMs)
                lo = mid + 1;
            else
                hi = mid;
        }

        if (lo >= actions.Count) return null;
        return (actions[lo], lo);
    }
}
