using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

/// <summary>
/// Integration tests for the Handy HSSP playback flow.
/// Tests the full coordinator → device backend interaction using mocked backends,
/// covering connect, setup, play, pause, seek, and reconnect scenarios.
/// </summary>
public class HandyIntegrationTests
{
    private readonly IFunscriptParser _parser = Substitute.For<IFunscriptParser>();
    private readonly ILogger<PlaybackCoordinator> _logger = Substitute.For<ILogger<PlaybackCoordinator>>();
    private readonly IMediaPlayer _player = Substitute.For<IMediaPlayer>();
    private readonly IDeviceBackend _backend = Substitute.For<IDeviceBackend>();
    private readonly PlaybackCoordinator _coordinator;

    private static readonly FunscriptDocument TestScript = new()
    {
        Actions = [new(0, 0), new(500, 100), new(1000, 0), new(1500, 100)]
    };

    public HandyIntegrationTests()
    {
        _coordinator = new PlaybackCoordinator(_parser, _logger);
        _coordinator.SetMediaPlayer(_player);

        _backend.Name.Returns("Handy Wi-Fi API");
        _backend.IsStreamingMode.Returns(false);
        _backend.ConnectionState.Returns(DeviceConnectionState.Connected);
        _coordinator.SetDeviceBackend(_backend);

        _parser.ParseFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TestScript);
    }

    // ────────────────────────────────────────────────────
    // Full HSSP flow: load → play → pause → resume → stop
    // ────────────────────────────────────────────────────

    [Fact]
    public async Task FullFlow_LoadPlayPauseResumeStop_AllDeviceCallsMade()
    {
        // Load — also calls StopAsync internally which may hit StopPlaybackAsync
        await _coordinator.LoadAsync("video.mp4", "script.funscript");
        await _backend.Received(1).SetupScriptAsync(TestScript, Arg.Any<CancellationToken>());

        // Play
        _player.PositionMs.Returns(0L);
        await _coordinator.PlayAsync();
        await _backend.Received(1).StartPlaybackAsync(0L);
        Assert.Equal(PlaybackState.Playing, _coordinator.State);

        // Pause — calls StopPlaybackAsync on the device
        await _coordinator.PauseAsync();
        Assert.Equal(PlaybackState.Paused, _coordinator.State);

        // Resume
        _player.PositionMs.Returns(3000L);
        await _coordinator.PlayAsync();
        await _backend.Received(1).StartPlaybackAsync(3000L);

        // Stop
        await _coordinator.StopAsync();
        Assert.Equal(PlaybackState.Stopped, _coordinator.State);

        // Device should have received stop calls (from Load's internal stop, Pause, and Stop)
        await _backend.Received().StopPlaybackAsync(Arg.Any<CancellationToken>());
    }

    // ────────────────────────────────────────────────────
    // Connect AFTER playback started
    // ────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectDuringPlayback_AutoSetupsAndStartsDevice()
    {
        // Disconnect the backend initially
        _backend.ConnectionState.Returns(DeviceConnectionState.Disconnected);
        _coordinator.SetDeviceBackend(_backend);

        // Load and play WITHOUT device connected
        await _coordinator.LoadAsync("video.mp4", "script.funscript");
        await _backend.DidNotReceive().SetupScriptAsync(Arg.Any<FunscriptDocument>(), Arg.Any<CancellationToken>());

        _player.PositionMs.Returns(0L);
        await _coordinator.PlayAsync();
        await _backend.DidNotReceive().StartPlaybackAsync(Arg.Any<long>());

        // Now connect device — should auto-setup AND auto-start playback
        _backend.ConnectionState.Returns(DeviceConnectionState.Connected);
        _player.PositionMs.Returns(5000L);

        await _coordinator.ConnectDeviceAsync();

        await _backend.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await _backend.Received(1).SetupScriptAsync(TestScript, Arg.Any<CancellationToken>());
        await _backend.Received(1).StartPlaybackAsync(5000L);
    }

    [Fact]
    public async Task ConnectWhileStopped_SetupsButDoesNotStartPlayback()
    {
        // Disconnect initially
        _backend.ConnectionState.Returns(DeviceConnectionState.Disconnected);
        _coordinator.SetDeviceBackend(_backend);

        // Load script but don't play
        await _coordinator.LoadAsync("video.mp4", "script.funscript");

        // Connect — should setup but not start
        _backend.ConnectionState.Returns(DeviceConnectionState.Connected);

        await _coordinator.ConnectDeviceAsync();

        await _backend.Received(1).SetupScriptAsync(TestScript, Arg.Any<CancellationToken>());
        await _backend.DidNotReceive().StartPlaybackAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task ConnectWithNoScript_NoSetupAttempted()
    {
        // Load video without script
        await _coordinator.LoadAsync("video.mp4", null);

        // Disconnect and reconnect
        _backend.ConnectionState.Returns(DeviceConnectionState.Disconnected);
        _coordinator.SetDeviceBackend(_backend);
        _backend.ConnectionState.Returns(DeviceConnectionState.Connected);

        await _coordinator.ConnectDeviceAsync();

        await _backend.DidNotReceive().SetupScriptAsync(Arg.Any<FunscriptDocument>(), Arg.Any<CancellationToken>());
    }

    // ────────────────────────────────────────────────────
    // Seeking
    // ────────────────────────────────────────────────────

    [Fact]
    public async Task Seek_WithConnectedDevice_SeeksDevice()
    {
        await _coordinator.LoadAsync("video.mp4", "script.funscript");
        await _coordinator.PlayAsync();

        await _coordinator.SeekAsync(10000);

        await _player.Received(1).SeekAsync(10000);
        await _backend.Received(1).SeekPlaybackAsync(10000);
    }

    [Fact]
    public async Task Seek_DeviceDisconnected_OnlySeeksMediaPlayer()
    {
        _backend.ConnectionState.Returns(DeviceConnectionState.Disconnected);
        _coordinator.SetDeviceBackend(_backend);

        await _coordinator.LoadAsync("video.mp4", "script.funscript");

        await _coordinator.SeekAsync(5000);

        await _player.Received(1).SeekAsync(5000);
        await _backend.DidNotReceive().SeekPlaybackAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task Seek_NoScript_DoesNotSeekDevice()
    {
        await _coordinator.LoadAsync("video.mp4", null);

        await _coordinator.SeekAsync(5000);

        await _player.Received(1).SeekAsync(5000);
        await _backend.DidNotReceive().SeekPlaybackAsync(Arg.Any<long>());
    }

    // ────────────────────────────────────────────────────
    // Pattern playback
    // ────────────────────────────────────────────────────

    [Fact]
    public async Task LoadPattern_WithConnectedDevice_SetupsScript()
    {
        var pattern = new FunscriptDocument
        {
            Actions = [new(0, 0), new(250, 100), new(500, 0), new(750, 100)]
        };

        await _coordinator.LoadPatternAsync(pattern);

        await _backend.Received(1).SetupScriptAsync(pattern, Arg.Any<CancellationToken>());
        Assert.Equal(pattern, _coordinator.CurrentScript);
    }

    [Fact]
    public async Task LoadPattern_SetupFails_DoesNotThrow()
    {
        _backend.SetupScriptAsync(Arg.Any<FunscriptDocument>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("Server error")));

        var pattern = new FunscriptDocument
        {
            Actions = [new(0, 0), new(500, 100)]
        };

        // Should not throw — error is caught and logged
        await _coordinator.LoadPatternAsync(pattern);
        Assert.Equal(pattern, _coordinator.CurrentScript);
    }

    // ────────────────────────────────────────────────────
    // Emergency stop
    // ────────────────────────────────────────────────────

    [Fact]
    public async Task EmergencyStop_StopsEverything()
    {
        await _coordinator.LoadAsync("video.mp4", "script.funscript");
        await _coordinator.PlayAsync();

        await _coordinator.EmergencyStopAsync();

        await _player.Received().StopAsync();
        await _backend.Received(1).EmergencyStopAsync();
        Assert.Equal(PlaybackState.Stopped, _coordinator.State);
    }

    // ────────────────────────────────────────────────────
    // Error resilience
    // ────────────────────────────────────────────────────

    [Fact]
    public async Task Play_DeviceStartFails_StillSetsPlayingState()
    {
        await _coordinator.LoadAsync("video.mp4", "script.funscript");
        _backend.StartPlaybackAsync(Arg.Any<long>())
            .Returns(Task.FromException(new HttpRequestException("Timeout")));

        await _coordinator.PlayAsync();

        // Coordinator should still be Playing even if device fails
        Assert.Equal(PlaybackState.Playing, _coordinator.State);
    }

    [Fact]
    public async Task Stop_DeviceStopFails_StillSetsStopped()
    {
        await _coordinator.LoadAsync("video.mp4", "script.funscript");
        await _coordinator.PlayAsync();

        _backend.StopPlaybackAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("Timeout")));

        await _coordinator.StopAsync();

        Assert.Equal(PlaybackState.Stopped, _coordinator.State);
    }

    [Fact]
    public async Task Seek_DeviceSeekFails_MediaPlayerStillSeeks()
    {
        await _coordinator.LoadAsync("video.mp4", "script.funscript");
        _backend.SeekPlaybackAsync(Arg.Any<long>())
            .Returns(Task.FromException(new HttpRequestException("Timeout")));

        await _coordinator.SeekAsync(5000);

        await _player.Received(1).SeekAsync(5000);
        // Device seek failed but coordinator doesn't crash
    }

    // ────────────────────────────────────────────────────
    // Script re-load on new media
    // ────────────────────────────────────────────────────

    [Fact]
    public async Task LoadNewMedia_ReplacesScript_DeviceGetsNewScript()
    {
        var script1 = new FunscriptDocument { Actions = [new(0, 0), new(1000, 100)] };
        var script2 = new FunscriptDocument { Actions = [new(0, 50), new(2000, 50)] };

        _parser.ParseFileAsync("script1.funscript", Arg.Any<CancellationToken>()).Returns(script1);
        _parser.ParseFileAsync("script2.funscript", Arg.Any<CancellationToken>()).Returns(script2);

        await _coordinator.LoadAsync("video1.mp4", "script1.funscript");
        await _backend.Received(1).SetupScriptAsync(script1, Arg.Any<CancellationToken>());

        await _coordinator.LoadAsync("video2.mp4", "script2.funscript");
        await _backend.Received(1).SetupScriptAsync(script2, Arg.Any<CancellationToken>());

        Assert.Equal(script2, _coordinator.CurrentScript);
    }

    [Fact]
    public async Task LoadMedia_NoScriptAfterScript_ClearsCurrentScript()
    {
        await _coordinator.LoadAsync("video.mp4", "script.funscript");
        Assert.NotNull(_coordinator.CurrentScript);

        await _coordinator.LoadAsync("video2.mp4", null);
        Assert.Null(_coordinator.CurrentScript);
    }

    // ────────────────────────────────────────────────────
    // Streaming mode (Intiface) vs HSSP mode (Handy)
    // ────────────────────────────────────────────────────

    [Fact]
    public async Task HsspMode_Play_CallsStartPlayback()
    {
        await _coordinator.LoadAsync("video.mp4", "script.funscript");
        await _coordinator.PlayAsync();

        await _backend.Received(1).StartPlaybackAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task StreamingMode_Play_DoesNotCallStartPlayback()
    {
        var streamingBackend = Substitute.For<IDeviceBackend>();
        streamingBackend.IsStreamingMode.Returns(true);
        streamingBackend.ConnectionState.Returns(DeviceConnectionState.Connected);
        _coordinator.SetDeviceBackend(streamingBackend);

        await _coordinator.LoadAsync("video.mp4", "script.funscript");
        await _coordinator.PlayAsync();

        await streamingBackend.DidNotReceive().StartPlaybackAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task HsspMode_Pause_StopsDevicePlayback()
    {
        await _coordinator.LoadAsync("video.mp4", "script.funscript");
        await _coordinator.PlayAsync();
        await _coordinator.PauseAsync();

        // StopPlayback called during both StopAsync (in LoadAsync) and PauseAsync
        await _backend.Received().StopPlaybackAsync(Arg.Any<CancellationToken>());
    }
}
