using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Devices.HandyApi;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

/// <summary>
/// Offline wire-protocol tests for <see cref="HandyApiClient"/>: requests go to a recording handler, never the network.
/// </summary>
public class HandyApiClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(HttpMethod Method, string Path, string Body, string? Key)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("X-Connection-Key", out var keys);
            Requests.Enqueue((request.Method, request.RequestUri!.AbsolutePath, body, keys?.FirstOrDefault()));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"result":0}""") };
        }

        public IEnumerable<JsonElement> PlayBodies() => Requests
            .Where(r => r.Path.EndsWith("/hssp/play", StringComparison.Ordinal))
            .Select(r => JsonDocument.Parse(r.Body).RootElement);
    }

    private static (HandyApiClient Client, RecordingHandler Http) CreateHssp()
    {
        var http = new RecordingHandler();
        var client = new HandyApiClient(
            Substitute.For<IScriptHostingService>(),
            new ServerTimeSyncService(NullLogger<ServerTimeSyncService>.Instance),
            NullLogger<HandyApiClient>.Instance,
            () => http);
        client.SetConnectionKey("test-key");
        client.SetProtocol(HandyProtocol.HSSP);
        return (client, http);
    }

    [Fact]
    public async Task StartPlayback_SendsHsspPlay_WithKeyHeaderAndStartTime()
    {
        var (client, http) = CreateHssp();

        await client.StartPlaybackAsync(1234, TestContext.Current.CancellationToken);

        var play = Assert.Single(http.Requests, r => r.Path.EndsWith("/hssp/play", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Put, play.Method);
        Assert.Equal("test-key", play.Key);
        var body = JsonDocument.Parse(play.Body).RootElement;
        Assert.Equal(1234, body.GetProperty("startTime").GetInt64());
        Assert.False(body.GetProperty("loop").GetBoolean());
    }

    [Fact]
    public async Task HardResync_KeepsTheSelectedPlaybackRate()
    {
        var (client, http) = CreateHssp();
        await client.SetPlaybackRateAsync(1.5);
        await client.StartPlaybackAsync(0, TestContext.Current.CancellationToken);

        // Media is far from where the device should be: > 500 ms drift forces stop + play.
        await client.ResyncAsync(100_000, TestContext.Current.CancellationToken);

        Assert.Contains(http.Requests, r => r.Path.EndsWith("/hssp/stop", StringComparison.Ordinal));
        var plays = http.PlayBodies().ToList();
        Assert.Equal(2, plays.Count);
        Assert.Equal(100_000, plays[1].GetProperty("startTime").GetInt64());
        Assert.Equal(1.5, plays[1].GetProperty("playbackRate").GetDouble());
    }

    [Fact]
    public async Task Resync_SmallDriftWithinInterval_SendsNothing()
    {
        var (client, http) = CreateHssp();
        await client.StartPlaybackAsync(0, TestContext.Current.CancellationToken);
        var before = http.Requests.Count;

        await client.ResyncAsync(50, TestContext.Current.CancellationToken);

        Assert.Equal(before, http.Requests.Count);
    }

    [Fact]
    public async Task Hdsp_StartPlayback_IsNoOp()
    {
        var (client, http) = CreateHssp();
        client.SetProtocol(HandyProtocol.HDSP);

        await client.StartPlaybackAsync(0, TestContext.Current.CancellationToken);

        Assert.Empty(http.Requests);
    }
}
