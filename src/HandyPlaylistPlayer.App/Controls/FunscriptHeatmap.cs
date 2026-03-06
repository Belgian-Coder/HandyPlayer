using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.App.Controls;

public class FunscriptHeatmap : Control
{
    public static readonly StyledProperty<FunscriptDocument?> ScriptProperty =
        AvaloniaProperty.Register<FunscriptHeatmap, FunscriptDocument?>(nameof(Script));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<FunscriptHeatmap, double>(nameof(Position));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<FunscriptHeatmap, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<long?> LoopStartProperty =
        AvaloniaProperty.Register<FunscriptHeatmap, long?>(nameof(LoopStart));

    public static readonly StyledProperty<long?> LoopEndProperty =
        AvaloniaProperty.Register<FunscriptHeatmap, long?>(nameof(LoopEnd));

    public FunscriptDocument? Script
    {
        get => GetValue(ScriptProperty);
        set => SetValue(ScriptProperty, value);
    }

    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public long? LoopStart
    {
        get => GetValue(LoopStartProperty);
        set => SetValue(LoopStartProperty, value);
    }

    public long? LoopEnd
    {
        get => GetValue(LoopEndProperty);
        set => SetValue(LoopEndProperty, value);
    }

    private ImmutableSolidColorBrush[]? _cachedBrushes;
    private int _cachedWidth;
    private FunscriptDocument? _cachedScript;

    private static readonly IPen PositionPen = new Pen(Brushes.White, 2).ToImmutable();
    private static readonly IPen LoopMarkerPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 200, 0)), 3).ToImmutable();
    private static readonly ImmutableSolidColorBrush LoopRegionBrush = new(Color.FromArgb(80, 255, 215, 0));
    private static readonly ImmutableSolidColorBrush EmptyBrush = new(Color.FromRgb(30, 30, 50));

    static FunscriptHeatmap()
    {
        AffectsRender<FunscriptHeatmap>(ScriptProperty, PositionProperty, MaximumProperty, LoopStartProperty, LoopEndProperty);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width < 1 || bounds.Height < 1) return;

        int width = Math.Max(1, (int)bounds.Width);
        int height = Math.Max(1, (int)bounds.Height);

        var script = Script;
        if (script == null || script.Actions.Count < 2)
        {
            context.FillRectangle(EmptyBrush, new Rect(bounds.Size));
        }
        else
        {
            // Recalculate heatmap brushes if script or width changed
            if (_cachedBrushes == null || _cachedWidth != width || _cachedScript != script)
            {
                var intensity = ComputeIntensity(script, width);
                _cachedBrushes = new ImmutableSolidColorBrush[width];
                for (int i = 0; i < width && i < intensity.Length; i++)
                    _cachedBrushes[i] = new ImmutableSolidColorBrush(IntensityToColor(intensity[i]));
                _cachedWidth = width;
                _cachedScript = script;
            }

            // Draw heatmap bars
            for (int x = 0; x < width && x < _cachedBrushes.Length; x++)
            {
                context.FillRectangle(_cachedBrushes[x], new Rect(x, 0, 1, height));
            }
        }

        // Draw A-B loop region and markers (always, even without a script)
        if (Maximum > 0)
        {
            var loopA = LoopStart;
            var loopB = LoopEnd;
            if (loopA.HasValue && loopB.HasValue)
            {
                double ax = (loopA.Value / Maximum) * width;
                double bx = (loopB.Value / Maximum) * width;
                context.FillRectangle(LoopRegionBrush, new Rect(ax, 0, bx - ax, height));
                context.DrawLine(LoopMarkerPen, new Point(ax, 0), new Point(ax, height));
                context.DrawLine(LoopMarkerPen, new Point(bx, 0), new Point(bx, height));
                DrawLoopTriangle(context, ax, height, true);
                DrawLoopTriangle(context, bx, height, false);
            }
            else if (loopA.HasValue)
            {
                double ax = (loopA.Value / Maximum) * width;
                context.DrawLine(LoopMarkerPen, new Point(ax, 0), new Point(ax, height));
                DrawLoopTriangle(context, ax, height, true);
            }
        }

        // Draw playback position indicator (always, even without a script)
        if (Maximum > 0 && Position >= 0)
        {
            double posX = (Position / Maximum) * width;
            context.DrawLine(PositionPen, new Point(posX, 0), new Point(posX, height));
        }
    }

    private static readonly ImmutableSolidColorBrush LoopMarkerFill = new(Color.FromRgb(255, 200, 0));

    private static void DrawLoopTriangle(DrawingContext context, double x, double height, bool isStart)
    {
        // Small triangle at top pointing inward to mark loop boundary
        const double sz = 6;
        var dir = isStart ? 1 : -1;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(x, 0), true);
            ctx.LineTo(new Point(x + sz * dir, 0));
            ctx.LineTo(new Point(x, sz));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(LoopMarkerFill, null, geo);

        // Bottom triangle
        var geo2 = new StreamGeometry();
        using (var ctx = geo2.Open())
        {
            ctx.BeginFigure(new Point(x, height), true);
            ctx.LineTo(new Point(x + sz * dir, height));
            ctx.LineTo(new Point(x, height - sz));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(LoopMarkerFill, null, geo2);
    }

    private static byte[] ComputeIntensity(FunscriptDocument script, int width)
    {
        var result = new byte[width];
        if (script.Actions.Count < 2 || width < 1) return result;

        long duration = script.DurationMs;
        if (duration <= 0) return result;

        double msPerPixel = (double)duration / width;

        var actions = script.Actions;
        int actionIdx = 0;

        for (int x = 0; x < width; x++)
        {
            long startMs = (long)(x * msPerPixel);
            long endMs = (long)((x + 1) * msPerPixel);

            // Find max speed (position change per time) in this bucket
            double maxSpeed = 0;

            // Advance action index to start of this bucket
            while (actionIdx > 0 && actions[actionIdx].At > startMs) actionIdx--;
            while (actionIdx < actions.Count - 1 && actions[actionIdx + 1].At < startMs) actionIdx++;

            for (int i = Math.Max(0, actionIdx); i < actions.Count - 1; i++)
            {
                var a = actions[i];
                var b = actions[i + 1];

                if (a.At > endMs) break;
                if (b.At < startMs) continue;

                double dt = b.At - a.At;
                if (dt <= 0) continue;
                double dp = Math.Abs(b.Pos - a.Pos);
                double speed = dp / dt * 1000; // positions per second
                if (speed > maxSpeed) maxSpeed = speed;
            }

            // Clamp speed to 0-255 range (500 pos/s is very fast)
            result[x] = (byte)Math.Min(255, maxSpeed * 255.0 / 500.0);
        }

        return result;
    }

    private static Color IntensityToColor(byte intensity)
    {
        // 5-stop gradient: Dark Blue → Blue → Cyan → Green → Yellow → Orange → Red
        double t = intensity / 255.0;
        if (t < 0.05)
        {
            // Near-zero: dark muted blue
            return Color.FromRgb(20, 20, 50);
        }
        if (t < 0.2)
        {
            // Dark blue → Bright blue
            double s = (t - 0.05) / 0.15;
            return Color.FromRgb((byte)(20 + 10 * s), (byte)(20 + 80 * s), (byte)(50 + 205 * s));
        }
        if (t < 0.4)
        {
            // Blue → Cyan
            double s = (t - 0.2) / 0.2;
            return Color.FromRgb((byte)(30 * s), (byte)(100 + 155 * s), 255);
        }
        if (t < 0.6)
        {
            // Cyan → Green
            double s = (t - 0.4) / 0.2;
            return Color.FromRgb((byte)(30 + 50 * s), 255, (byte)(255 - 255 * s));
        }
        if (t < 0.8)
        {
            // Green → Yellow/Orange
            double s = (t - 0.6) / 0.2;
            return Color.FromRgb((byte)(80 + 175 * s), 255, 0);
        }
        {
            // Orange → Red
            double s = (t - 0.8) / 0.2;
            return Color.FromRgb(255, (byte)(255 - 200 * s), 0);
        }
    }
}
