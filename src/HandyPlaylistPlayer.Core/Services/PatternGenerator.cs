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
        var actions = new List<FunscriptAction>();
        var periodMs = 1000.0 / freq;
        int step = Math.Max(20, (int)(periodMs / 20)); // ~20 points per cycle

        for (long t = 0; t <= durationMs; t += step)
        {
            var phase = 2 * Math.PI * freq * t / 1000.0;
            var value = (Math.Sin(phase) + 1) / 2; // 0..1
            var pos = (int)Math.Round(min + value * (max - min));
            actions.Add(new FunscriptAction(t, Math.Clamp(pos, 0, 100)));
        }
        return actions;
    }

    private static List<FunscriptAction> GenerateSawtooth(double freq, int min, int max, int durationMs)
    {
        var actions = new List<FunscriptAction>();
        var periodMs = 1000.0 / freq;

        var sawStep = Math.Max(20, (int)(periodMs / 2));
        for (long t = 0; t <= durationMs; t += sawStep)
        {
            var phase = (t % (long)Math.Max(1, periodMs)) / periodMs;
            var pos = (int)Math.Round(min + phase * (max - min));
            actions.Add(new FunscriptAction(t, Math.Clamp(pos, 0, 100)));
        }
        return actions;
    }

    private static List<FunscriptAction> GenerateSquare(double freq, int min, int max, int durationMs)
    {
        var actions = new List<FunscriptAction>();
        var halfPeriodMs = Math.Max(20, (long)(500.0 / freq));

        for (long t = 0; t <= durationMs; t += halfPeriodMs)
        {
            var cycleIndex = t / halfPeriodMs;
            var pos = cycleIndex % 2 == 0 ? max : min;
            actions.Add(new FunscriptAction(t, pos));
        }
        return actions;
    }

    private static List<FunscriptAction> GenerateTriangle(double freq, int min, int max, int durationMs)
    {
        var actions = new List<FunscriptAction>();
        var quarterPeriodMs = Math.Max(10, (long)(250.0 / freq));

        for (long t = 0; t <= durationMs; t += quarterPeriodMs)
        {
            var cyclePhase = (t / quarterPeriodMs) % 4;
            var pos = cyclePhase switch
            {
                0 => min,
                1 => max,
                2 => min,
                3 => max,
                _ => min
            };
            // For triangle: interpolation happens automatically between points
            actions.Add(new FunscriptAction(t, pos));
        }
        return actions;
    }

    private static List<FunscriptAction> GenerateRandom(double freq, int min, int max, int durationMs, int? seed)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;
        var actions = new List<FunscriptAction>();
        var intervalMs = Math.Max(20, (int)(500.0 / freq));

        for (long t = 0; t <= durationMs; t += intervalMs)
        {
            var pos = rng.Next(min, max + 1);
            actions.Add(new FunscriptAction(t, pos));
        }
        return actions;
    }
}
