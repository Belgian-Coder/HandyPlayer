using System.Net.Http.Json;
using HandyPlaylistPlayer.Devices.HandyApi.Models;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Devices.HandyApi;

public class ServerTimeSyncService(ILogger<ServerTimeSyncService> logger)
{
    private long _offsetMs;
    private long _avgRtdMs;
    private volatile bool _synced;

    public bool IsSynced => _synced;
    public long OffsetMs => Interlocked.Read(ref _offsetMs);

    /// <summary>Average round-trip delay to the Handy server in ms (measured during sync).</summary>
    public long AvgRoundTripMs => Interlocked.Read(ref _avgRtdMs);

    public long GetEstimatedServerTime() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Interlocked.Read(ref _offsetMs);

    /// <summary>Quick RTT probe (5 rounds) — updates AvgRoundTripMs without full time sync.</summary>
    public async Task<long> ProbeRttAsync(HttpClient client, CancellationToken ct = default)
    {
        var rtds = new List<long>(5);
        for (int i = 0; i < 5; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using var resp = await client.GetAsync("hstp/time", ct);
                var end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                rtds.Add(end - start);
            }
            catch { /* skip failed round */ }
        }

        if (rtds.Count == 0) return Interlocked.Read(ref _avgRtdMs);

        // Use median to avoid outlier spikes
        rtds.Sort();
        var median = rtds[rtds.Count / 2];
        Interlocked.Exchange(ref _avgRtdMs, median);
        return median;
    }

    public async Task SyncAsync(HttpClient client, int rounds = 30, CancellationToken ct = default)
    {
        var offsets = new List<long>(rounds);
        var rtds = new List<long>(rounds);

        for (int i = 0; i < rounds; i++)
        {
            ct.ThrowIfCancellationRequested();

            var sendTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            try
            {
                // Read raw response first for diagnostics
                using var rawResponse = await client.GetAsync("hstp/time", ct);
                var rawBody = await rawResponse.Content.ReadAsStringAsync(ct);
                var response = System.Text.Json.JsonSerializer.Deserialize<HandyServerTimeResponse>(rawBody);
                if (response == null) continue;

                var receiveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var rtd = receiveTime - sendTime;
                var offset = response.ServerTime - (sendTime + rtd / 2);

                // Log first 3 rounds for diagnostics
                if (i < 3)
                    logger.LogInformation("Time sync round {Round}: raw={Raw}, serverTime={ServerTime}, sendTime={SendTime}, rtd={Rtd}ms, offset={Offset}ms",
                        i, rawBody.Trim(), response.ServerTime, sendTime, rtd, offset);

                rtds.Add(rtd);
                offsets.Add(offset);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Time sync round {Round} failed", i);
            }
        }

        if (offsets.Count < 5)
        {
            logger.LogError("Time sync failed: only {Count} successful rounds", offsets.Count);
            _synced = false;
            return;
        }

        // Remove outliers (beyond 1.5x IQR)
        var sorted = offsets.OrderBy(x => x).ToList();
        int n = sorted.Count;
        var q1 = sorted[(n - 1) / 4];
        var q3 = sorted[(n - 1) * 3 / 4];
        var iqr = Math.Max(q3 - q1, 1); // Avoid zero IQR
        var lower = q1 - (long)(1.5 * iqr);
        var upper = q3 + (long)(1.5 * iqr);

        var filtered = offsets.Where(o => o >= lower && o <= upper).ToList();
        if (filtered.Count == 0)
            filtered = offsets; // Fallback: use all if filtering removed everything

        Interlocked.Exchange(ref _offsetMs, (long)filtered.Average());
        _synced = true;

        var avgRtd = rtds.Count > 0 ? (long)rtds.Average() : 0;
        Interlocked.Exchange(ref _avgRtdMs, avgRtd);
        logger.LogInformation(
            "Time sync complete. Offset: {Offset}ms, Avg RTD: {Rtd}ms, Samples: {Count}/{Total}",
            Interlocked.Read(ref _offsetMs), avgRtd, filtered.Count, rounds);
    }
}
