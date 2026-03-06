using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Runtime;

public class ScriptTransformPipeline
{
    public TransformSettings Settings { get; set; } = TransformSettings.Default;

    private long _previousOutput = 50;
    private long _previousTimeMs;

    public int Transform(int rawPosition, long currentTimeMs)
    {
        double pos = rawPosition;

        // 1. Invert
        if (Settings.Invert)
            pos = 100.0 - pos;

        // 2. Range scaling
        pos = Settings.RangeMin + (pos / 100.0) * (Settings.RangeMax - Settings.RangeMin);

        // 3. Intensity (scale deltas around midpoint)
        if (Settings.Intensity is { } intensity && intensity != 1.0)
        {
            var mid = (Settings.RangeMin + Settings.RangeMax) / 2.0;
            pos = mid + (pos - mid) * intensity;
        }

        // 4. Clamp
        pos = Math.Clamp(pos, 0, 100);

        // 5. Edging throttle — reduce movement when speed exceeds threshold
        var prevTime = Interlocked.Read(ref _previousTimeMs);
        var prevOutput = (int)Interlocked.Read(ref _previousOutput);
        if (Settings.EdgeThreshold.HasValue && prevTime > 0)
        {
            var dt = currentTimeMs - prevTime;
            if (dt > 0)
            {
                var speed = Math.Abs(pos - prevOutput) / dt * 1000.0; // positions/sec
                if (speed > Settings.EdgeThreshold.Value)
                {
                    // Blend toward previous output by reduction factor
                    pos = prevOutput + (pos - prevOutput) * Settings.EdgeReduction;
                }
            }
        }

        // 6. Speed limit
        if (Settings.SpeedLimit.HasValue && prevTime > 0)
        {
            var deltaTime = currentTimeMs - prevTime;
            if (deltaTime > 0)
            {
                var maxDelta = Settings.SpeedLimit.Value * deltaTime / 1000.0;
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
