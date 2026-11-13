using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Runtime;

public class ScriptTransformPipeline
{
    public TransformSettings Settings { get; set; } = TransformSettings.Default;

    private long _previousOutput = 50;
    private long _previousTimeMs;

    public int Transform(int rawPosition, long currentTimeMs)
    {
        // Read the settings object once: one consistent snapshot per call (and one property read instead of ~15).
        var settings = Settings;
        double pos = rawPosition;

        // 1. Invert
        if (settings.Invert)
            pos = 100.0 - pos;

        // 2. Range scaling
        pos = settings.RangeMin + (pos / 100.0) * (settings.RangeMax - settings.RangeMin);

        // 3. Intensity (scale deltas around midpoint)
        if (settings.Intensity is { } intensity && intensity != 1.0)
        {
            var mid = (settings.RangeMin + settings.RangeMax) / 2.0;
            pos = mid + (pos - mid) * intensity;
        }

        // 4. Clamp
        pos = Math.Clamp(pos, 0, 100);

        // 5. Edging throttle — reduce movement when speed exceeds threshold
        var prevTime = Interlocked.Read(ref _previousTimeMs);
        var prevOutput = (int)Interlocked.Read(ref _previousOutput);
        if (settings.EdgeThreshold.HasValue && prevTime > 0)
        {
            var dt = currentTimeMs - prevTime;
            if (dt > 0)
            {
                var speed = Math.Abs(pos - prevOutput) / dt * 1000.0; // positions/sec
                if (speed > settings.EdgeThreshold.Value)
                {
                    // Blend toward previous output by reduction factor
                    pos = prevOutput + (pos - prevOutput) * settings.EdgeReduction;
                }
            }
        }

        // 6. Speed limit
        if (settings.SpeedLimit.HasValue && prevTime > 0)
        {
            var deltaTime = currentTimeMs - prevTime;
            if (deltaTime > 0)
            {
                var maxDelta = settings.SpeedLimit.Value * deltaTime / 1000.0;
                var actualDelta = pos - prevOutput;
                if (Math.Abs(actualDelta) > maxDelta)
                    pos = prevOutput + Math.Sign(actualDelta) * maxDelta;
            }
        }

        var result = (int)Math.Clamp(pos, 0, 100);
        Interlocked.Exchange(ref _previousOutput, result);
        Interlocked.Exchange(ref _previousTimeMs, currentTimeMs);
        return result;
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _previousOutput, 50);
        Interlocked.Exchange(ref _previousTimeMs, 0);
    }
}
