using System.Runtime.InteropServices;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Services;

public static class FunscriptInterpolator
{
    public static int GetPositionAtTime(long timeMs, IReadOnlyList<FunscriptAction> actions)
    {
        // Fast path: List<T> / arrays are searched through a span (no interface dispatch per probe).
        if (actions is List<FunscriptAction> list) return GetPositionAtTimeCore(timeMs, CollectionsMarshal.AsSpan(list));
        if (actions is FunscriptAction[] array) return GetPositionAtTimeCore(timeMs, array);

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

        return Interpolate(timeMs, actions[lo], actions[hi]);
    }

    internal static int GetPositionAtTimeCore(long timeMs, ReadOnlySpan<FunscriptAction> actions)
    {
        if (actions.Length == 0) return 50;
        if (timeMs <= actions[0].At) return actions[0].Pos;
        if (timeMs >= actions[^1].At) return actions[^1].Pos;

        // Binary search for the surrounding actions
        int lo = 0, hi = actions.Length - 1;
        while (lo < hi - 1)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (actions[mid].At <= timeMs)
                lo = mid;
            else
                hi = mid;
        }

        return Interpolate(timeMs, actions[lo], actions[hi]);
    }

    private static int Interpolate(long timeMs, FunscriptAction a, FunscriptAction b)
    {
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
        if (actions is List<FunscriptAction> list) return GetNextActionCore(timeMs, CollectionsMarshal.AsSpan(list));
        if (actions is FunscriptAction[] array) return GetNextActionCore(timeMs, array);

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

    internal static (FunscriptAction Action, int Index)? GetNextActionCore(long timeMs, ReadOnlySpan<FunscriptAction> actions)
    {
        if (actions.Length == 0) return null;

        int lo = 0, hi = actions.Length;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (actions[mid].At <= timeMs)
                lo = mid + 1;
            else
                hi = mid;
        }

        if (lo >= actions.Length) return null;
        return (actions[lo], lo);
    }

    /// <summary>
    /// Index of the last action whose timestamp is &lt;= <paramref name="timeMs"/>, or -1 when
    /// <paramref name="timeMs"/> is before all actions.
    /// </summary>
    internal static int FindCurrentIndexCore(long timeMs, ReadOnlySpan<FunscriptAction> actions)
    {
        int lo = 0, hi = actions.Length - 1;
        int result = -1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (actions[mid].At <= timeMs)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }
}
