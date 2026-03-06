using System.Runtime.InteropServices;
using System.Threading.Channels;
using HandyPlaylistPlayer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Media.Mpv;

/// <summary>
/// Generates video thumbnails using a pool of headless mpv instances.
/// Handles are pre-initialized once and reused across thumbnails, eliminating
/// the ~100-150 ms create/init/destroy overhead on every generation.
/// Up to <see cref="Concurrency"/> thumbnails are produced in parallel.
/// Thumbnails are stored as JPEG (quality 80) for compact file size.
/// </summary>
public class MpvThumbnailGenerator : IThumbnailService
{
    private const int Concurrency  = 4;
    private const int JpegQuality  = 80;

    private readonly int _thumbWidth;
    private readonly int _thumbHeight;
    private readonly string _thumbnailDir;
    private readonly Channel<IntPtr> _pool;
    private readonly ILogger<MpvThumbnailGenerator> _logger;
    private volatile bool _disposed;

    public MpvThumbnailGenerator(string thumbnailDir, ILogger<MpvThumbnailGenerator> logger,
        int thumbWidth = 320, int thumbHeight = 180)
    {
        _thumbnailDir = thumbnailDir;
        _logger = logger;
        _thumbWidth = thumbWidth > 0 ? thumbWidth : 320;
        _thumbHeight = thumbHeight > 0 ? thumbHeight : 180;
        Directory.CreateDirectory(thumbnailDir);

        _pool = Channel.CreateBounded<IntPtr>(Concurrency);
        for (int i = 0; i < Concurrency; i++)
        {
            var handle = CreateHandle();
            if (handle != IntPtr.Zero)
                _pool.Writer.TryWrite(handle);
            else
                _logger.LogWarning("Failed to create mpv thumbnail handle {Index}", i);
        }
    }

    // ── Handle lifecycle ──────────────────────────────────────────────────────

    private IntPtr CreateHandle()
    {
        var mpv = MpvInterop.mpv_create();
        if (mpv == IntPtr.Zero) return IntPtr.Zero;

        MpvInterop.SetOptionString(mpv, "vo",    "null");
        MpvInterop.SetOptionString(mpv, "ao",    "null");
        MpvInterop.SetOptionString(mpv, "pause", "yes");
        MpvInterop.SetOptionString(mpv, "hwdec", "no");
        MpvInterop.SetOptionString(mpv, "input-default-bindings", "no");
        MpvInterop.SetOptionString(mpv, "terminal", "no");
        MpvInterop.SetOptionString(mpv, "cache", "no");
        MpvInterop.SetOptionString(mpv, "screenshot-sw",           "yes");
        MpvInterop.SetOptionString(mpv, "screenshot-format",       "jpeg");
        MpvInterop.SetOptionString(mpv, "screenshot-jpeg-quality", JpegQuality.ToString());
        MpvInterop.SetOptionString(mpv, "vf",
            $"scale={_thumbWidth}:{_thumbHeight}:force_original_aspect_ratio=decrease," +
            $"pad={_thumbWidth}:{_thumbHeight}:-1:-1");
        MpvInterop.SetOptionString(mpv, "vd-lavc-threads", "1");

        var err = MpvInterop.mpv_initialize(mpv);
        if (err < 0)
        {
            _logger.LogDebug("mpv_initialize failed: {Err}", MpvInterop.GetErrorString(err));
            MpvInterop.mpv_terminate_destroy(mpv);
            return IntPtr.Zero;
        }

        return mpv;
    }

    /// <summary>
    /// Resets a handle after use so it is ready for the next file.
    /// Stops playback and drains the event queue to prevent stale events
    /// from confusing the next thumbnail's WaitForEvent calls.
    /// </summary>
    private static void ResetHandle(IntPtr mpv)
    {
        try { MpvInterop.Command(mpv, "stop"); } catch { }

        // Drain all pending events
        while (true)
        {
            var ptr = MpvInterop.mpv_wait_event(mpv, 0);
            if (ptr == IntPtr.Zero) break;
            var ev = Marshal.PtrToStructure<MpvEvent>(ptr);
            if (ev.EventId == MpvEventId.None) break;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<string?> GetOrGenerateThumbnailAsync(int mediaFileId, string videoPath,
        CancellationToken ct = default)
    {
        var outputPath = Path.Combine(_thumbnailDir, $"{mediaFileId}.jpg");
        if (File.Exists(outputPath)) return outputPath;

        // Borrow a pre-initialized handle; blocks until one is available
        IntPtr handle;
        try { handle = await _pool.Reader.ReadAsync(ct); }
        catch (OperationCanceledException) { return null; }
        catch (System.Threading.Channels.ChannelClosedException) { return null; }
        try
        {
            if (File.Exists(outputPath)) return outputPath;

            return await Task.Run(() => GenerateAsync(handle, videoPath, outputPath, ct), ct)
                ? outputPath : null;
        }
        finally
        {
            if (!_disposed)
            {
                try
                {
                    ResetHandle(handle);
                    _pool.Writer.TryWrite(handle);
                }
                catch
                {
                    // Handle is in an unknown state — destroy and replace with a fresh one
                    try { MpvInterop.mpv_terminate_destroy(handle); } catch { }
                    if (!_disposed)
                    {
                        var fresh = CreateHandle();
                        if (fresh != IntPtr.Zero)
                            _pool.Writer.TryWrite(fresh);
                    }
                }
            }
            else
            {
                try { MpvInterop.mpv_terminate_destroy(handle); } catch { }
            }
        }
    }

    // ── Thumbnail generation ──────────────────────────────────────────────────

    private async Task<bool> GenerateAsync(IntPtr mpv, string videoPath, string outputPath,
        CancellationToken ct)
    {
        try
        {
            // Ensure paused for this load (property survives stop/loadfile cycles)
            MpvInterop.SetPropertyString(mpv, "pause", "yes");
            MpvInterop.Command(mpv, "loadfile", videoPath, "replace");

            if (!await WaitForEventAsync(mpv, MpvEventId.FileLoaded, 8000, ct)) return false;

            // Fast keyframe seek to ~10%
            var duration = MpvInterop.GetPropertyDouble(mpv, "duration");
            if (duration <= 0) return false;

            var seekSecs = (duration * 0.10).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            MpvInterop.Command(mpv, "seek", seekSecs, "absolute");

            // PlaybackRestart signals the seek is done and a frame is decoded/ready
            await WaitForEventAsync(mpv, MpvEventId.PlaybackRestart, 5000, ct);

            var tempPath = Path.Combine(Path.GetTempPath(), $"hpthumb_{Guid.NewGuid():N}.jpg");
            try
            {
                MpvInterop.Command(mpv, "screenshot-to-file", tempPath, "video");

                for (int i = 0; i < 30 && !File.Exists(tempPath); i++)
                {
                    if (ct.IsCancellationRequested) return false;
                    await Task.Delay(50, CancellationToken.None);
                }

                if (!File.Exists(tempPath)) return false;

                // Retry move — antivirus or mpv may briefly hold a lock
                for (int retry = 0; retry < 3; retry++)
                {
                    try
                    {
                        File.Move(tempPath, outputPath, overwrite: true);
                        return true;
                    }
                    catch (UnauthorizedAccessException) when (retry < 2)
                    {
                        await Task.Delay(100, CancellationToken.None);
                    }
                    catch (IOException) when (retry < 2)
                    {
                        await Task.Delay(100, CancellationToken.None);
                    }
                }
                return false;
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to generate thumbnail for {Path}", videoPath);
            return false;
        }
    }

    private static async Task<bool> WaitForEventAsync(IntPtr mpv, MpvEventId targetEvent,
        int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;
            var eventPtr = MpvInterop.mpv_wait_event(mpv, 0.05);
            if (eventPtr == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<MpvEvent>(eventPtr);
            if (ev.EventId == targetEvent) return true;
            if (ev.EventId == MpvEventId.Shutdown) return false;
        }
        return false;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public void DeleteThumbnail(int mediaFileId)
    {
        try
        {
            var path = Path.Combine(_thumbnailDir, $"{mediaFileId}.jpg");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete thumbnail for {Id}", mediaFileId);
        }
    }

    public void DeleteOrphanedThumbnails(IReadOnlyCollection<int> activeIds)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_thumbnailDir, "*.*"))
            {
                var ext  = Path.GetExtension(file).ToLowerInvariant();
                var name = Path.GetFileNameWithoutExtension(file);

                // Delete legacy .bmp files unconditionally (format migration)
                if (ext == ".bmp")
                {
                    try { File.Delete(file); } catch { }
                    continue;
                }

                // Delete .jpg files whose ID is no longer in the active library
                if (ext == ".jpg" && int.TryParse(name, out var id) && !activeIds.Contains(id))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete orphaned thumbnail {File}", file); }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enumerate thumbnail directory");
        }
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _disposed = true;
        _pool.Writer.TryComplete();

        // Destroy all idle handles still in the pool.
        // In-use handles self-destruct via the finally block in GetOrGenerateThumbnailAsync.
        while (_pool.Reader.TryRead(out var handle))
        {
            try { MpvInterop.mpv_terminate_destroy(handle); } catch { }
        }
    }
}
