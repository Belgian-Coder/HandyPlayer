using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class PlaybackCoordinatorTests
{
    private readonly IFunscriptParser _parser = Substitute.For<IFunscriptParser>();
    private readonly ILogger<PlaybackCoordinator> _logger = Substitute.For<ILogger<PlaybackCoordinator>>();
    private readonly IMediaPlayer _player = Substitute.For<IMediaPlayer>();
    private readonly IDeviceBackend _backend = Substitute.For<IDeviceBackend>();
    private readonly PlaybackCoordinator _coordinator;

    public PlaybackCoordinatorTests()
    {
        _coordinator = new PlaybackCoordinator(_parser, _logger);
        _backend.Name.Returns("Handy API");
        _backend.ConnectionState.Returns(DeviceConnectionState.Connected);
    }

    [Fact]
    public void InitialState_IsStopped()
    {
        Assert.Equal(PlaybackState.Stopped, _coordinator.State);
    }

    [Fact]
    public void SetMediaPlayer_SubscribesToEvents()
    {
        _coordinator.SetMediaPlayer(_player);
        Assert.Equal(0, _coordinator.CurrentPositionMs);
    }

    [Fact]
    public void SetDeviceBackend_SetsHsspModeForHandyApi()
    {
        _coordinator.SetDeviceBackend(_backend);
        Assert.False(_coordinator.IsStreamingMode);
    }

    [Fact]
    public void SetDeviceBackend_SetsStreamingModeForIntiface()
    {
        var intifaceBackend = Substitute.For<IDeviceBackend>();
        intifaceBackend.Name.Returns("Intiface Central");
        intifaceBackend.IsStreamingMode.Returns(true);

        _coordinator.SetDeviceBackend(intifaceBackend);
        Assert.True(_coordinator.IsStreamingMode);
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenNoMediaPlayer()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _coordinator.LoadAsync("video.mp4", null));
    }

    [Fact]
    public async Task LoadAsync_LoadsVideoAndScript()
    {
        _coordinator.SetMediaPlayer(_player);
        _coordinator.SetDeviceBackend(_backend);

        var script = new FunscriptDocument
        {
            Actions = [new FunscriptAction(0, 0), new FunscriptAction(1000, 100)]
        };
        _parser.ParseFileAsync("script.funscript", Arg.Any<CancellationToken>()).Returns(script);

        await _coordinator.LoadAsync("video.mp4", "script.funscript");

        await _player.Received(1).LoadAsync("video.mp4");
        await _backend.Received(1).SetupScriptAsync(script, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAsync_NoScript_SkipsSetup()
    {
        _coordinator.SetMediaPlayer(_player);
        _coordinator.SetDeviceBackend(_backend);

        await _coordinator.LoadAsync("video.mp4", null);

        await _player.Received(1).LoadAsync("video.mp4");
        await _backend.DidNotReceive().SetupScriptAsync(Arg.Any<FunscriptDocument>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlayAsync_SetsStatePlaying()
    {
        _coordinator.SetMediaPlayer(_player);

        PlaybackState? fired = null;
        _coordinator.PlaybackStateChanged += (_, s) => fired = s;

        await _coordinator.PlayAsync();

        Assert.Equal(PlaybackState.Playing, _coordinator.State);
        Assert.Equal(PlaybackState.Playing, fired);
    }

    [Fact]
    public async Task PauseAsync_SetsStatePaused()
    {
        _coordinator.SetMediaPlayer(_player);
        await _coordinator.PlayAsync();

        await _coordinator.PauseAsync();

        Assert.Equal(PlaybackState.Paused, _coordinator.State);
    }

    [Fact]
    public async Task StopAsync_SetsStateStopped()
    {
        _coordinator.SetMediaPlayer(_player);
        await _coordinator.PlayAsync();

        await _coordinator.StopAsync();

        Assert.Equal(PlaybackState.Stopped, _coordinator.State);
    }

    [Fact]
    public async Task SeekAsync_CallsMediaPlayerSeek()
    {
        _coordinator.SetMediaPlayer(_player);

        await _coordinator.SeekAsync(5000);

        await _player.Received(1).SeekAsync(5000);
    }

    [Fact]
    public async Task ConnectDeviceAsync_ThrowsWhenNoBackend()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _coordinator.ConnectDeviceAsync());
    }

    [Fact]
    public async Task ConnectDeviceAsync_CallsBackend()
    {
        _coordinator.SetDeviceBackend(_backend);

        await _coordinator.ConnectDeviceAsync();

        await _backend.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TransformSettings_CanBeSetAndRead()
    {
        var settings = new TransformSettings { RangeMin = 10, RangeMax = 90 };
        _coordinator.TransformSettings = settings;

        Assert.Equal(10, _coordinator.TransformSettings.RangeMin);
        Assert.Equal(90, _coordinator.TransformSettings.RangeMax);
    }

    [Fact]
    public void DeviceState_ReturnsDisconnectedWhenNoBackend()
    {
        Assert.Equal(DeviceConnectionState.Disconnected, _coordinator.DeviceState);
    }

    [Fact]
    public async Task PlayAsync_WithHsspBackendAndScript_StartsPlayback()
    {
        _coordinator.SetMediaPlayer(_player);
        _coordinator.SetDeviceBackend(_backend);
        _player.PositionMs.Returns(500L);

        var script = new FunscriptDocument
        {
            Actions = [new FunscriptAction(0, 0)]
        };
        _parser.ParseFileAsync("s.funscript", Arg.Any<CancellationToken>()).Returns(script);
        await _coordinator.LoadAsync("v.mp4", "s.funscript");

        await _coordinator.PlayAsync();

        await _backend.Received(1).StartPlaybackAsync(500L);
    }

    [Fact]
    public async Task StopAsync_StopsDevicePlayback()
    {
        _coordinator.SetMediaPlayer(_player);
        _coordinator.SetDeviceBackend(_backend);

        var script = new FunscriptDocument
        {
            Actions = [new FunscriptAction(0, 0)]
        };
        _parser.ParseFileAsync("s.funscript", Arg.Any<CancellationToken>()).Returns(script);
        await _coordinator.LoadAsync("v.mp4", "s.funscript");
        await _coordinator.PlayAsync();
        await _coordinator.StopAsync();

        await _backend.Received().StopPlaybackAsync(Arg.Any<CancellationToken>());
    }
}
