using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Services;

namespace HandyPlaylistPlayer.Core.Runtime;

public enum TickOutcome
{
    /// <summary>Send <see cref="TickDecision.Index"/> to the device.</summary>
    Send,
    /// <summary>Media time is before the first action.</summary>
    SkipBefore,
    /// <summary>Every action has been sent.</summary>
    SkipEnd,
    /// <summary>The next target was already sent; wait for media time to cross into the next segment.</summary>
    SkipWait,
}

public readonly record struct TickDecision(TickOutcome Outcome, int Index = -1, int DurationMs = 0, bool Resynced = false);

/// <summary>
/// Pure per-tick decision logic for <see cref="StreamingTickEngine"/>: given the media time, which keyframe
/// (if any) should be sent to a streaming device, and over what duration. No clocks, no I/O, so it can be
/// tested tick by tick.
/// <para>
/// Normal playback advances one keyframe at a time so fast transitions (vibrations) are never skipped when
/// several keyframes fall inside one tick. After a seek, a resume mid-script or a stall longer than
/// <see cref="CatchUpWindowMs"/>, it resynchronises to the segment the media is in instead of replaying
/// every missed keyframe.
/// </para>
/// </summary>
public sealed class TickPlanner
{
    /// <summary>Keyframes that fell behind the media clock by more than this are skipped rather than replayed.</summary>
    public const int CatchUpWindowMs = 100;
    public const int MinDurationMs = 20;
    public const int MaxDurationMs = 5000;

    private int _lastSentIndex = -1;

    /// <summary>Index of the last keyframe confirmed sent, or -1.</summary>
    public int LastSentIndex => _lastSentIndex;

    /// <summary>Record that the keyframe from the last <see cref="TickOutcome.Send"/> decision reached the device.</summary>
    public void MarkSent(int index) => _lastSentIndex = index;

    public void Reset() => _lastSentIndex = -1;

    public TickDecision Plan(long mediaTimeMs, ReadOnlySpan<FunscriptAction> actions)
    {
        // Last keyframe whose timestamp <= media time: the media is in segment [mediaIndex, mediaIndex + 1].
        var mediaIndex = FunscriptInterpolator.FindCurrentIndexCore(mediaTimeMs, actions);
        if (mediaIndex < 0)
            return new TickDecision(TickOutcome.SkipBefore);

        var resynced = false;
        var sendIdx = _lastSentIndex + 1;

        // Steady state: after sending keyframe k the media is still in segment [k-1, k], so
        // mediaIndex == _lastSentIndex - 1. Only a smaller mediaIndex is a real backward seek.
        // Forward: the next keyframe is already further behind the media clock than the catch-up window
        // (seek forward, resume mid-script, long stall) — jump to the current segment.
        if (mediaIndex < _lastSentIndex - 1
            || (sendIdx < mediaIndex && actions[sendIdx].At < mediaTimeMs - CatchUpWindowMs))
        {
            sendIdx = mediaIndex + 1;
            resynced = true;
        }

        if (sendIdx >= actions.Length)
            return new TickDecision(TickOutcome.SkipEnd);

        // Only move toward keyframe sendIdx once the media has reached the keyframe before it.
        if (sendIdx > 0 && mediaTimeMs < actions[sendIdx - 1].At)
            return new TickDecision(TickOutcome.SkipWait);

        var target = actions[sendIdx];
        // Steady state: the script's inter-keyframe gap (MultiFunPlayer semantics). After a resync the device
        // joins mid-segment, so use the time left until the target instead.
        var durationMs = resynced
            ? target.At - mediaTimeMs
            : target.At - (sendIdx > 0 ? actions[sendIdx - 1].At : target.At);

        return new TickDecision(TickOutcome.Send, sendIdx,
            (int)Math.Clamp(durationMs, MinDurationMs, MaxDurationMs), resynced);
    }
}
