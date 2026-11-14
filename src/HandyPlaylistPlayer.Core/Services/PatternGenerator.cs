using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Services;

public enum PatternType
{
    Sine,
    Sawtooth,
    Square,
    Triangle,
    Random
}

public class PatternGenerator
{
    public static FunscriptDocument Generate(PatternType type, double frequencyHz = 1.0,
        int amplitudeMin = 0, int amplitudeMax = 100, int durationMs = 60000, int? seed = null)
    {
        if (frequencyHz <= 0) frequencyHz = 1.0;
        if (durationMs <= 0) durationMs = 1000;
        if (amplitudeMin > amplitudeMax) (amplitudeMin, amplitudeMax) = (amplitudeMax, amplitudeMin);
        amplitudeMin = Math.Clamp(amplitudeMin, 0, 100);
        amplitudeMax = Math.Clamp(amplitudeMax, 0, 100);

        var actions = type switch
        {
            PatternType.Sine => GenerateSine(frequencyHz, amplitudeMin, amplitudeMax, durationMs),
            PatternType.Sawtooth => GenerateSawtooth(frequencyHz, amplitudeMin, amplitudeMax, durationMs),
            PatternType.Square => GenerateSquare(frequencyHz, amplitudeMin, amplitudeMax, durationMs),
            PatternType.Triangle => GenerateTriangle(frequencyHz, amplitudeMin, amplitudeMax, durationMs),
            PatternType.Random => GenerateRandom(frequencyHz, amplitudeMin, amplitudeMax, durationMs, seed),
            _ => []
        };

        return new FunscriptDocument
        {
            Version = "1.0",
            Actions = actions
        };
    }

    private static List<FunscriptAction> GenerateSine(double freq, int min, int max, int durationMs)
    {
        var periodMs = 1000.0 / freq;
        int step = Math.Max(20, (int)(periodMs / 20)); // ~20 points per cycle
        var actions = new List<FunscriptAction>(durationMs / step + 1);

        for (long t = 0; t <= durationMs; t += step)
        {
            var phase = 2 * Math.PI * freq * t / 1000.0;
            var value = (Math.Sin(phase) + 1) / 2; // 0..1
            var pos = (int)Math.Round(min + value * (max - min));
            actions.Add(new FunscriptAction(t, Math.Clamp(pos, 0, 100)));
        }
        return actions;
    }

    /// <summary>
    /// Duration of the fast edge in sawtooth/square waves: ~10% of the span, at least 20 ms (the shortest
    /// move devices accept), never more than half the span.
    /// </summary>
    private static double EdgeMs(double spanMs) => Math.Min(Math.Max(20, spanMs / 10), spanMs / 2);

    /// <summary>Ramp from min to max over one period, then drop back to min over a short edge.</summary>
    private static List<FunscriptAction> GenerateSawtooth(double freq, int min, int max, int durationMs)
    {
        var periodMs = Math.Max(40, 1000.0 / freq);
        var edgeMs = EdgeMs(periodMs);
        var actions = new List<FunscriptAction>((int)(2 * durationMs / periodMs) + 2);
        for (var start = 0.0; start <= durationMs; start += periodMs)
        {
            actions.Add(new FunscriptAction((long)Math.Round(start), min));
            var peak = start + periodMs - edgeMs;
            if (peak <= durationMs)
                actions.Add(new FunscriptAction((long)Math.Round(peak), max));
        }
        return actions;
    }

    /// <summary>Hold max for half a period, then min for half a period, with short transitions.</summary>
    private static List<FunscriptAction> GenerateSquare(double freq, int min, int max, int durationMs)
    {
        var halfPeriodMs = Math.Max(40, 500.0 / freq);
        var edgeMs = EdgeMs(halfPeriodMs);
        var actions = new List<FunscriptAction>((int)(2 * durationMs / halfPeriodMs) + 2);
        var high = true;
        for (var start = 0.0; start <= durationMs; start += halfPeriodMs, high = !high)
        {
            var level = high ? max : min;
            actions.Add(new FunscriptAction((long)Math.Round(start), level));
            var holdEnd = start + halfPeriodMs - edgeMs;
            if (holdEnd <= durationMs)
                actions.Add(new FunscriptAction((long)Math.Round(holdEnd), level));
        }
        return actions;
    }

    /// <summary>Linear min→max→min, one full cycle per period (interpolation draws the ramps).</summary>
    private static List<FunscriptAction> GenerateTriangle(double freq, int min, int max, int durationMs)
    {
        var halfPeriodMs = Math.Max(20, 500.0 / freq);
        var actions = new List<FunscriptAction>((int)(durationMs / halfPeriodMs) + 1);
        var rising = false;
        for (var t = 0.0; t <= durationMs; t += halfPeriodMs, rising = !rising)
            actions.Add(new FunscriptAction((long)Math.Round(t), rising ? max : min));
        return actions;
    }

    private static List<FunscriptAction> GenerateRandom(double freq, int min, int max, int durationMs, int? seed)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;
        var intervalMs = Math.Max(20, (int)(500.0 / freq));
        var actions = new List<FunscriptAction>(durationMs / intervalMs + 1);

        for (long t = 0; t <= durationMs; t += intervalMs)
        {
            var pos = rng.Next(min, max + 1);
            actions.Add(new FunscriptAction(t, pos));
        }
        return actions;
    }
}
