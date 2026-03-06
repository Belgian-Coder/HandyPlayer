using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HandyPlaylistPlayer.App.ViewModels;
using HandyPlaylistPlayer.Media.Mpv;

namespace HandyPlaylistPlayer.App.Views;

public partial class PlayerView : UserControl
{
    private int _autoHideTimeoutSeconds = 3;
    private const int InputTransparencyRetryMs = 500;
    private const int InputTransparencyMaxRetries = 10;
    private const double PointerMoveThreshold = 10.0; // pixels before showing controls

    private bool _isDragging;
    private bool _isClickSeeking;
    private MpvVideoView? _mpvView;
    private MpvSoftwareVideoView? _swView;
    private readonly DispatcherTimer _autoHideTimer;
    private DispatcherTimer? _inputTransparencyTimer;
    private PlayerViewModel? _subscribedVm;
    private CancellationTokenSource? _singleTapCts;
    private Point _lastPointerPosition;

    public PlayerView()
    {
        InitializeComponent();
        SeekSlider.AddHandler(Thumb.DragStartedEvent, OnSeekStarted, handledEventsToo: true);
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, OnSeekCompleted, handledEventsToo: true);
        SeekSlider.AddHandler(Thumb.DragDeltaEvent, OnSeekDragDelta, handledEventsToo: true);
        SeekSlider.AddHandler(PointerPressedEvent, OnSliderPointerPressed, RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerReleasedEvent, OnSliderPointerReleased, RoutingStrategies.Tunnel);

        // PlayerView stays in the visual tree permanently (overlay navigation),
        // so we only need to create the VideoView once when the DataContext arrives.
        DataContextChanged += OnDataContextChanged;

        // Fullscreen auto-hide timer (one-shot, configurable)
        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_autoHideTimeoutSeconds) };
        _autoHideTimer.Tick += OnAutoHideTimerTick;

        PointerMoved += OnPointerMovedForAutoHide;
        AddHandler(KeyDownEvent, OnKeyDownShowControls, RoutingStrategies.Tunnel);
        VideoContainer.Tapped += OnVideoTapped;
        VideoContainer.DoubleTapped += OnVideoDoubleTapped;
        LoopMarkerCanvas.SizeChanged += OnLoopMarkerCanvasSizeChanged;
        VisualizerCanvas.SizeChanged += OnVisualizerCanvasSizeChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from previous VM
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedVm = null;
        }

        if (DataContext is PlayerViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            _subscribedVm = vm;

            // Apply initial queue column visibility
            UpdateQueueColumnVisibility(vm.IsQueueVisible);

            // Sync auto-hide timeout from VM
            UpdateAutoHideTimeout(vm.AutoHideSeconds);

            if (_mpvView == null && vm.MpvAdapter != null)
                Dispatcher.UIThread.Post(CreateVideoView, DispatcherPriority.Loaded);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PlayerViewModel vm) return;

        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.IsFullscreen):
                if (vm.IsFullscreen)
                {
                    VideoBorder.CornerRadius = new CornerRadius(0);
                    ShowTransportControls();
                    _lastPointerPosition = new Point(double.NegativeInfinity, double.NegativeInfinity);
                    _autoHideTimer.Start();
                }
                else
                {
                    _autoHideTimer.Stop();
                    VideoBorder.CornerRadius = new CornerRadius(4);
                    ShowTransportControls();
                    Cursor = Cursor.Default;
                }
                break;

            case nameof(PlayerViewModel.LoopStartMs):
            case nameof(PlayerViewModel.LoopEndMs):
            case nameof(PlayerViewModel.SeekMaximum):
                UpdateLoopMarkers(vm);
                break;

            case nameof(PlayerViewModel.VisualizerPosition):
                UpdateVisualizerDot(vm);
                break;

            case nameof(PlayerViewModel.IsQueueVisible):
                UpdateQueueColumnVisibility(vm.IsQueueVisible);
                break;
        }
    }

    private void UpdateLoopMarkers(PlayerViewModel vm)
    {
        var canvasWidth = LoopMarkerCanvas.Bounds.Width;
        if (canvasWidth < 1 || vm.SeekMaximum <= 0)
        {
            LoopRegionRect.IsVisible = false;
            LoopMarkerA.IsVisible = false;
            LoopMarkerB.IsVisible = false;
            return;
        }

        var loopA = vm.LoopStartMs;
        var loopB = vm.LoopEndMs;

        if (loopA.HasValue)
        {
            double ax = (loopA.Value / vm.SeekMaximum) * canvasWidth;
            Canvas.SetLeft(LoopMarkerA, ax - 1.5); // center the 3px marker
            LoopMarkerA.IsVisible = true;
        }
        else
        {
            LoopMarkerA.IsVisible = false;
        }

        if (loopB.HasValue)
        {
            double bx = (loopB.Value / vm.SeekMaximum) * canvasWidth;
            Canvas.SetLeft(LoopMarkerB, bx - 1.5);
            LoopMarkerB.IsVisible = true;
        }
        else
        {
            LoopMarkerB.IsVisible = false;
        }

        if (loopA.HasValue && loopB.HasValue)
        {
            double ax = (loopA.Value / vm.SeekMaximum) * canvasWidth;
            double bx = (loopB.Value / vm.SeekMaximum) * canvasWidth;
            Canvas.SetLeft(LoopRegionRect, ax);
            LoopRegionRect.Width = Math.Max(0, bx - ax);
            LoopRegionRect.IsVisible = true;
        }
        else
        {
            LoopRegionRect.IsVisible = false;
        }
    }

    private void OnLoopMarkerCanvasSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is PlayerViewModel vm)
            UpdateLoopMarkers(vm);
    }

    private void OnVisualizerCanvasSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is PlayerViewModel vm)
            UpdateVisualizerDot(vm);
    }

    private void UpdateVisualizerDot(PlayerViewModel vm)
    {
        const double dotSize = 18;
        var h = VisualizerCanvas.Bounds.Height;
        var top = (1.0 - vm.VisualizerPosition) * Math.Max(0, h - dotSize);
        Canvas.SetTop(VisualizerDot, top);
    }

    private void UpdateQueueColumnVisibility(bool visible)
    {
        var splitterCol = ContentGrid.ColumnDefinitions[1];
        var queueCol = ContentGrid.ColumnDefinitions[2];
        if (visible)
        {
            splitterCol.Width = new GridLength(4);
            queueCol.Width = new GridLength(260);
            queueCol.MinWidth = 160;
            queueCol.MaxWidth = 600;
        }
        else
        {
            splitterCol.Width = new GridLength(0);
            queueCol.Width = new GridLength(0);
            queueCol.MinWidth = 0;
            queueCol.MaxWidth = 0;
        }
    }

    private void CreateVideoView()
    {
        if (_mpvView != null || _swView != null) return;
        if (DataContext is not PlayerViewModel vm || vm.MpvAdapter == null) return;

        if (vm.MpvAdapter.UseSoftwareRendering)
        {
            // macOS: use software render API (vo=libmpv → WriteableBitmap)
            _swView = new MpvSoftwareVideoView();
            VideoContainer.Children.Insert(0, _swView);

            // Initialize mpv without a window handle — render API doesn't need one
            vm.MpvAdapter.SetWindowHandle(IntPtr.Zero);
            _swView.SetAdapter(vm.MpvAdapter);
        }
        else
        {
            // Windows/Linux: use NativeControlHost with --wid
            _mpvView = new MpvVideoView();
            VideoContainer.Children.Insert(0, _mpvView);

            Dispatcher.UIThread.Post(() =>
            {
                if (_mpvView == null || DataContext is not PlayerViewModel vm2) return;
                if (_mpvView.WindowHandle != IntPtr.Zero)
                {
                    vm2.MpvAdapter!.SetWindowHandle(_mpvView.WindowHandle);
                    if (OperatingSystem.IsWindows())
                        ScheduleInputTransparency();
                }
            }, DispatcherPriority.Render);
        }
    }

    // --- Win32 interop: make mpv's native HWND input-transparent ---

    private void ScheduleInputTransparency()
    {
        _inputTransparencyTimer?.Stop();

        // mpv creates its rendering window after SetWindowHandle().
        // Retry every 500ms until child HWNDs appear (up to 5s).
        int retries = 0;
        _inputTransparencyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(InputTransparencyRetryMs) };
        _inputTransparencyTimer.Tick += (_, _) =>
        {
            retries++;
            if (ApplyInputTransparency() || retries >= InputTransparencyMaxRetries)
                _inputTransparencyTimer?.Stop();
        };
        _inputTransparencyTimer.Start();
    }

    private bool ApplyInputTransparency()
    {
        var windowHandle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (windowHandle == IntPtr.Zero) return false;

        bool found = false;
        NativeMethods.EnumChildWindows(windowHandle, (hwnd, _) =>
        {
            var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                exStyle | NativeMethods.WS_EX_TRANSPARENT);
            found = true;
            return true;
        }, IntPtr.Zero);

        return found;
    }

    private async void OnCopyErrorClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlayerViewModel vm && !string.IsNullOrEmpty(vm.ErrorText))
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(vm.ErrorText);
        }
    }

    private void OnSeekStarted(object? sender, VectorEventArgs e)
    {
        _isDragging = true;
        if (DataContext is PlayerViewModel vm)
        {
            vm.OnSeekStarted();
            vm.OnSeekPreview(SeekSlider.Value);
            SeekPreviewBorder.IsVisible = true;
            UpdateSeekPreviewPosition();
        }
    }

    private void OnSeekDragDelta(object? sender, VectorEventArgs e)
    {
        if (_isDragging && DataContext is PlayerViewModel vm)
        {
            vm.OnSeekPreview(SeekSlider.Value);
            UpdateSeekPreviewPosition();
        }
    }

    private async void OnSeekCompleted(object? sender, VectorEventArgs e)
    {
        _isDragging = false;
        SeekPreviewBorder.IsVisible = false;
        if (DataContext is PlayerViewModel vm)
        {
            try
            {
                await vm.OnSeekCompleted(SeekSlider.Value);
            }
            catch { /* seek failure handled in coordinator */ }
        }
    }

    private void UpdateSeekPreviewPosition()
    {
        if (SeekSlider.Maximum <= 0) return;
        var fraction = SeekSlider.Value / SeekSlider.Maximum;
        var sliderWidth = SeekSlider.Bounds.Width;
        var previewWidth = SeekPreviewBorder.Bounds.Width;
        var x = fraction * sliderWidth - previewWidth / 2;
        x = Math.Clamp(x, 0, sliderWidth - previewWidth);
        SeekPreviewBorder.Margin = new Avalonia.Thickness(x, -24, 0, 0);
    }

    private double? GetSliderValueFromPointer(Point pointerPosition)
    {
        var sliderWidth = SeekSlider.Bounds.Width;
        if (sliderWidth <= 0 || SeekSlider.Maximum <= SeekSlider.Minimum) return null;

        // Slider track has ~10px padding on each side for the thumb
        const double trackPadding = 10;
        var effectiveWidth = sliderWidth - 2 * trackPadding;
        if (effectiveWidth <= 0) return null;

        var fraction = Math.Clamp((pointerPosition.X - trackPadding) / effectiveWidth, 0, 1);
        return fraction * (SeekSlider.Maximum - SeekSlider.Minimum) + SeekSlider.Minimum;
    }

    private void OnSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isClickSeeking = true;
        if (DataContext is PlayerViewModel vm)
        {
            vm.OnSeekStarted();

            // Snap slider to click position — Avalonia Slider default moves by LargeChange,
            // but for a seekbar we want snap-to-position on track click.
            var clickValue = GetSliderValueFromPointer(e.GetPosition(SeekSlider));
            if (clickValue.HasValue)
            {
                SeekSlider.Value = clickValue.Value;
                vm.OnSeekPreview(clickValue.Value);
                SeekPreviewBorder.IsVisible = true;
                UpdateSeekPreviewPosition();
            }
        }
    }

    private async void OnSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isClickSeeking) return;
        _isClickSeeking = false;
        SeekPreviewBorder.IsVisible = false;

        if (_isDragging) return;

        if (DataContext is PlayerViewModel vm)
        {
            try
            {
                // Calculate seek position from pointer — don't use SeekSlider.Value which
                // may have been overridden by the Slider's LargeChange step behavior.
                var seekValue = GetSliderValueFromPointer(e.GetPosition(SeekSlider)) ?? SeekSlider.Value;
                SeekSlider.Value = seekValue;
                await vm.OnSeekCompleted(seekValue);
            }
            catch { /* seek failure handled in coordinator */ }
        }
    }

    // --- Fullscreen auto-hide ---

    private void ShowTransportControls()
    {
        TransportControls.Opacity = 1;
        TransportControls.IsHitTestVisible = true;
    }

    private void HideTransportControls()
    {
        TransportControls.Opacity = 0;
        TransportControls.IsHitTestVisible = false;
    }

    private void OnPointerMovedForAutoHide(object? sender, PointerEventArgs e)
    {
        if (DataContext is not PlayerViewModel vm || !vm.IsFullscreen) return;

        // Ignore sub-pixel jitter — require meaningful movement before showing controls
        var pos = e.GetPosition(this);
        var dx = pos.X - _lastPointerPosition.X;
        var dy = pos.Y - _lastPointerPosition.Y;
        if (dx * dx + dy * dy < PointerMoveThreshold * PointerMoveThreshold)
            return;

        _lastPointerPosition = pos;
        ShowTransportControls();
        Cursor = Cursor.Default;
        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }

    private void OnKeyDownShowControls(object? sender, KeyEventArgs e)
    {
        if (DataContext is PlayerViewModel vm && vm.IsFullscreen)
        {
            ShowTransportControls();
            Cursor = Cursor.Default;
            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }
    }

    public void UpdateAutoHideTimeout(int seconds)
    {
        _autoHideTimeoutSeconds = Math.Max(1, seconds);
        _autoHideTimer.Interval = TimeSpan.FromSeconds(_autoHideTimeoutSeconds);
    }

    private void OnAutoHideTimerTick(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();
        if (DataContext is PlayerViewModel vm && vm.IsFullscreen)
        {
            HideTransportControls();
            Cursor = new Cursor(StandardCursorType.None);
        }
    }

    private async void OnVideoTapped(object? sender, TappedEventArgs e)
    {
        // Cancel and dispose any pending single-tap CTS from a previous click
        var oldCts = _singleTapCts;
        _singleTapCts = new CancellationTokenSource();
        var ct = _singleTapCts.Token;
        try { oldCts?.Cancel(); } finally { oldCts?.Dispose(); }

        try
        {
            // Wait to see if a double-tap follows (system double-click time ~300ms)
            await Task.Delay(300, ct);
            // No double-tap arrived — treat as single tap: play/pause
            if (DataContext is PlayerViewModel vm)
                vm.PlayPauseCommand.Execute(null);
        }
        catch (TaskCanceledException)
        {
            // Double-tap arrived, ignore the single-tap
        }
    }

    private void OnVideoDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Cancel the pending single-tap so it doesn't fire play/pause
        _singleTapCts?.Cancel();

        if (this.FindAncestorOfType<MainWindow>() is MainWindow mw
            && mw.DataContext is MainViewModel mainVm)
        {
            mw.SetFullscreen(mainVm, !mainVm.IsFullscreen);
        }
    }

    private void OnFullscreenButtonClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<MainWindow>() is MainWindow mw
            && mw.DataContext is MainViewModel mainVm)
        {
            mw.SetFullscreen(mainVm, !mainVm.IsFullscreen);
        }
    }

    // --- Win32 P/Invoke ---

    private static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;

        public delegate bool EnumChildWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildWindowsProc lpEnumFunc, IntPtr lParam);
    }
}
