using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace HandyPlaylistPlayer.Media.Mpv;

/// <summary>
/// Avalonia NativeControlHost that creates a native child window for mpv to
/// render into. On Windows we create an HWND child window with a black
/// background so no white flash appears before mpv starts rendering.
/// On macOS/Linux we let Avalonia create the native view and forward its
/// handle to mpv.
/// </summary>
public class MpvVideoView : NativeControlHost
{
    private IntPtr _windowHandle;

    /// <summary>
    /// The native window handle (HWND on Windows, NSView* on macOS).
    /// Valid after the control is attached to the visual tree.
    /// </summary>
    public IntPtr WindowHandle => _windowHandle;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (OperatingSystem.IsWindows())
        {
            var hwnd = Win32.CreateChildWindow(parent.Handle);
            _windowHandle = hwnd;
            return new PlatformHandle(hwnd, "HWND");
        }

        // macOS / Linux: let Avalonia create the native view
        var baseHandle = base.CreateNativeControlCore(parent);
        _windowHandle = baseHandle.Handle;

        // macOS: ensure the NSView is layer-backed — required for mpv GPU rendering.
        if (OperatingSystem.IsMacOS() && _windowHandle != IntPtr.Zero)
            ObjC.SetWantsLayer(_windowHandle, true);

        return baseHandle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (OperatingSystem.IsWindows() && _windowHandle != IntPtr.Zero)
        {
            Win32.DestroyWindow(_windowHandle);
            _windowHandle = IntPtr.Zero;
        }
        else
        {
            base.DestroyNativeControlCore(control);
        }
    }

    // ── macOS ObjC helper ──────────────────────────────────────────────────

    private static class ObjC
    {
        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern void void_objc_msgSend_bool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
        private static extern IntPtr sel_registerName(string name);

        public static void SetWantsLayer(IntPtr nsView, bool value)
        {
            var sel = sel_registerName("setWantsLayer:");
            void_objc_msgSend_bool(nsView, sel, value);
        }
    }

    // ── Win32 helper ──────────────────────────────────────────────────────

    private static class Win32
    {
        private const int WS_CHILD        = 0x40000000;
        private const int WS_VISIBLE      = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;

        private const string ClassName = "HandyMpvHost";
        private static readonly object _regLock = new();
        private static bool _classRegistered;

        // The WndProc delegate must be kept alive for the lifetime of the
        // process so GC doesn't collect it while the window class is registered.
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static readonly WndProcDelegate _wndProc = DefWindowProc;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int    cbSize;
            public uint   style;
            public IntPtr lpfnWndProc;
            public int    cbClsExtra;
            public int    cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string  lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern short RegisterClassEx(ref WNDCLASSEX lpWc);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint crColor);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName,
            string? lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hwnd);

        private static void EnsureClassRegistered()
        {
            if (_classRegistered) return;
            lock (_regLock)
            {
                if (_classRegistered) return;

                var wc = new WNDCLASSEX
                {
                    cbSize        = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance     = GetModuleHandle(null),
                    hbrBackground = CreateSolidBrush(0x00000000), // pure black
                    lpszClassName = ClassName,
                };
                RegisterClassEx(ref wc);
                _classRegistered = true;
            }
        }

        public static IntPtr CreateChildWindow(IntPtr parent)
        {
            EnsureClassRegistered();
            var hwnd = CreateWindowEx(0, ClassName, null,
                WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                0, 0, 1, 1, parent, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

            return hwnd;
        }
    }
}
