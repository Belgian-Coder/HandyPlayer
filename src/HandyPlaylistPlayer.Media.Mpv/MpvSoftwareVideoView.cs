using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace HandyPlaylistPlayer.Media.Mpv;

/// <summary>
/// Software-rendered video view for macOS where the Metal VO backend does not
/// support --wid embedding. Uses mpv's render API (vo=libmpv) to render frames
/// into a WriteableBitmap displayed via an Avalonia Control.Render() override.
///
/// Based on the proven pattern from https://github.com/homov/LibMpv
/// </summary>
public class MpvSoftwareVideoView : Control
{
    private WriteableBitmap? _bitmap;
    private MpvMediaPlayerAdapter? _adapter;

    public MpvSoftwareVideoView()
    {
        ClipToBounds = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    public void SetAdapter(MpvMediaPlayerAdapter adapter)
    {
        _adapter = adapter;
        adapter.FrameAvailable += OnFrameAvailable;
    }

    private void OnFrameAvailable()
    {
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
    }

    public override void Render(DrawingContext context)
    {
        if (VisualRoot == null || _adapter == null)
            return;

        var width = Math.Max(1, (int)Bounds.Width);
        var height = Math.Max(1, (int)Bounds.Height);

        if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Rgba8888, AlphaFormat.Premul);

        using (var fb = _bitmap.Lock())
        {
            _adapter.RenderSoftwareFrame(fb.Address, width, height, fb.RowBytes);
        }

        context.DrawImage(_bitmap, new Rect(0, 0, width, height));
    }

    public void Detach()
    {
        if (_adapter != null)
        {
            _adapter.FrameAvailable -= OnFrameAvailable;
            _adapter = null;
        }
    }
}
