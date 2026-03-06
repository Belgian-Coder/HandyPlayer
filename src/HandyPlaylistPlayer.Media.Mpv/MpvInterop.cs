using System.Runtime.InteropServices;

namespace HandyPlaylistPlayer.Media.Mpv;

/// <summary>
/// P/Invoke bindings for libmpv. Uses NativeLibrary.SetDllImportResolver for
/// cross-platform library name resolution (libmpv-2.dll on Windows,
/// libmpv.2.dylib on macOS, libmpv.so.2 on Linux).
/// </summary>
internal static class MpvInterop
{
    // Virtual library name used in DllImport attributes — resolved at runtime.
    internal const string LibName = "libmpv";

    static MpvInterop()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvInterop).Assembly, (name, assembly, searchPath) =>
        {
            if (name != LibName) return IntPtr.Zero;

            if (OperatingSystem.IsWindows())
            {
                NativeLibrary.TryLoad("libmpv-2.dll", assembly, searchPath, out var h);
                return h;
            }

            if (OperatingSystem.IsMacOS())
            {
                // Try standard search paths first (includes next to assembly)
                foreach (var n in new[] { "libmpv.2.dylib", "libmpv.dylib", "mpv" })
                    if (NativeLibrary.TryLoad(n, assembly, searchPath, out var h))
                        return h;

                // .app bundles launched from Finder don't inherit shell paths.
                // Try Homebrew locations explicitly (ARM64 and Intel).
                foreach (var prefix in new[] { "/opt/homebrew/lib", "/usr/local/lib" })
                    foreach (var n in new[] { "libmpv.2.dylib", "libmpv.dylib" })
                    {
                        var fullPath = Path.Combine(prefix, n);
                        if (NativeLibrary.TryLoad(fullPath, out var h))
                            return h;
                    }
            }

            // Linux
            foreach (var n in new[] { "libmpv.so.2", "libmpv.so", "mpv" })
                if (NativeLibrary.TryLoad(n, assembly, searchPath, out var h))
                    return h;

            return IntPtr.Zero;
        });
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────

    [DllImport(LibName)] internal static extern IntPtr mpv_create();
    [DllImport(LibName)] internal static extern int    mpv_initialize(IntPtr ctx);
    [DllImport(LibName)] internal static extern void   mpv_terminate_destroy(IntPtr ctx);
    [DllImport(LibName)] internal static extern void   mpv_free(IntPtr data);

    // ── Options (before initialize) ────────────────────────────────────────

    [DllImport(LibName)] internal static extern int mpv_set_option_string(IntPtr ctx, IntPtr name, IntPtr data);
    [DllImport(LibName)] internal static extern int mpv_set_option(IntPtr ctx, IntPtr name, MpvFormat format, ref long data);

    // ── Commands ───────────────────────────────────────────────────────────

    [DllImport(LibName)] internal static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    // ── Properties ────────────────────────────────────────────────────────

    [DllImport(LibName)] internal static extern int    mpv_set_property_string(IntPtr ctx, IntPtr name, IntPtr data);
    [DllImport(LibName)] internal static extern IntPtr mpv_get_property_string(IntPtr ctx, IntPtr name);
    [DllImport(LibName)] internal static extern int    mpv_set_property(IntPtr ctx, IntPtr name, MpvFormat format, ref double data);
    [DllImport(LibName)] internal static extern int    mpv_get_property(IntPtr ctx, IntPtr name, MpvFormat format, ref double data);
    [DllImport(LibName)] internal static extern int    mpv_set_property(IntPtr ctx, IntPtr name, MpvFormat format, ref long data);
    [DllImport(LibName)] internal static extern int    mpv_get_property(IntPtr ctx, IntPtr name, MpvFormat format, ref long data);

    // ── Events ─────────────────────────────────────────────────────────────

    [DllImport(LibName)] internal static extern int    mpv_observe_property(IntPtr ctx, ulong id, IntPtr name, MpvFormat format);
    [DllImport(LibName)] internal static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    [DllImport(LibName)] internal static extern void   mpv_wakeup(IntPtr ctx);

    // ── Render API (vo=libmpv software rendering) ─────────────────────────

    [DllImport(LibName)] internal static extern int  mpv_render_context_create(out IntPtr ctx, IntPtr mpv, MpvRenderParam[] @params);
    [DllImport(LibName)] internal static extern int    mpv_render_context_render(IntPtr ctx, MpvRenderParam[] @params);
    [DllImport(LibName)] internal static extern ulong mpv_render_context_update(IntPtr ctx);
    [DllImport(LibName)] internal static extern void  mpv_render_context_free(IntPtr ctx);
    [DllImport(LibName)] internal static extern void mpv_render_context_set_update_callback(IntPtr ctx, MpvRenderUpdateFn? callback, IntPtr callbackCtx);
    [DllImport(LibName)] internal static extern void mpv_render_context_report_swap(IntPtr ctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void MpvRenderUpdateFn(IntPtr callbackCtx);

    // ── Utility ────────────────────────────────────────────────────────────

    [DllImport(LibName)] internal static extern IntPtr mpv_error_string(int error);

    // ── Managed helpers ────────────────────────────────────────────────────

    internal static int SetOptionString(IntPtr ctx, string name, string value)
    {
        var n = Marshal.StringToCoTaskMemUTF8(name);
        var v = Marshal.StringToCoTaskMemUTF8(value);
        try   { return mpv_set_option_string(ctx, n, v); }
        finally { Marshal.FreeCoTaskMem(n); Marshal.FreeCoTaskMem(v); }
    }

    internal static int SetOptionInt64(IntPtr ctx, string name, long value)
    {
        var n = Marshal.StringToCoTaskMemUTF8(name);
        try   { return mpv_set_option(ctx, n, MpvFormat.Int64, ref value); }
        finally { Marshal.FreeCoTaskMem(n); }
    }

    internal static int Command(IntPtr ctx, params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1]; // null terminator
        for (int i = 0; i < args.Length; i++)
            ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        ptrs[args.Length] = IntPtr.Zero;
        try   { return mpv_command(ctx, ptrs); }
        finally { for (int i = 0; i < args.Length; i++) Marshal.FreeCoTaskMem(ptrs[i]); }
    }

    internal static int SetPropertyString(IntPtr ctx, string name, string value)
    {
        var n = Marshal.StringToCoTaskMemUTF8(name);
        var v = Marshal.StringToCoTaskMemUTF8(value);
        try   { return mpv_set_property_string(ctx, n, v); }
        finally { Marshal.FreeCoTaskMem(n); Marshal.FreeCoTaskMem(v); }
    }

    internal static string? GetPropertyString(IntPtr ctx, string name)
    {
        var n = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var ptr = mpv_get_property_string(ctx, n);
            if (ptr == IntPtr.Zero) return null;
            try   { return Marshal.PtrToStringUTF8(ptr); }
            finally { mpv_free(ptr); }
        }
        finally { Marshal.FreeCoTaskMem(n); }
    }

    internal static double GetPropertyDouble(IntPtr ctx, string name, double fallback = 0)
    {
        var n = Marshal.StringToCoTaskMemUTF8(name);
        double result = 0;
        try
        {
            return mpv_get_property(ctx, n, MpvFormat.Double, ref result) == 0 ? result : fallback;
        }
        finally { Marshal.FreeCoTaskMem(n); }
    }

    internal static int ObserveProperty(IntPtr ctx, ulong id, string name, MpvFormat format)
    {
        var n = Marshal.StringToCoTaskMemUTF8(name);
        try   { return mpv_observe_property(ctx, id, n, format); }
        finally { Marshal.FreeCoTaskMem(n); }
    }

    internal static string GetErrorString(int error)
    {
        var ptr = mpv_error_string(error);
        return ptr == IntPtr.Zero ? $"error {error}" : (Marshal.PtrToStringUTF8(ptr) ?? $"error {error}");
    }
}

// ── Enums ──────────────────────────────────────────────────────────────────

internal enum MpvFormat : int
{
    None      = 0,
    String    = 1,
    OsdString = 2,
    Flag      = 3,
    Int64     = 4,
    Double    = 5,
}

internal enum MpvEventId : int
{
    None              = 0,
    Shutdown          = 1,
    LogMessage        = 2,
    GetPropertyReply  = 3,
    SetPropertyReply  = 4,
    CommandReply      = 5,
    StartFile         = 6,
    EndFile           = 7,
    FileLoaded        = 8,
    Seek              = 20,
    PlaybackRestart   = 21,
    PropertyChange    = 22,
    QueueOverflow     = 24,
    Hook              = 25,
}

internal enum MpvEndFileReason : int
{
    Eof        = 0,
    Stop       = 2,
    Quit       = 3,
    Error      = 4,
    Redirect   = 5,
}

// ── Event structs ──────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int        Error;
    public ulong      ReplyUserdata;
    public IntPtr     Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr    Name;   // const char*
    public MpvFormat Format;
    public IntPtr    Data;   // points to value (double*, int*, etc.)
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int              Error;
}

// ── Render API structs ────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderParam
{
    public int    Type;   // MpvRenderParamType
    public IntPtr Data;
}

internal static class MpvRenderUpdateFlag
{
    public const ulong Frame = 1;  // MPV_RENDER_UPDATE_FRAME
}

internal static class MpvRenderParamType
{
    public const int Invalid   = 0;
    public const int ApiType   = 1;   // char* ("sw" or "opengl")
    public const int SwSize    = 17;  // int[2] {width, height}
    public const int SwFormat  = 18;  // char* ("rgba", "bgr0", etc.)
    public const int SwStride  = 19;  // size_t*
    public const int SwPointer = 20;  // void* (output buffer)
}
