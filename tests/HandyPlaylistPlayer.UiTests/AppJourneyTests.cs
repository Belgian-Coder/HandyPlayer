using Avalonia.Controls;
using Avalonia.Input;
using HandyPlaylistPlayer.App.ViewModels;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Runtime;
using HandyPlaylistPlayer.Core.Models;
using Xunit;

namespace HandyPlaylistPlayer.UiTests;

/// <summary>
/// End-to-end user journeys through the real UI. Scenarios share one app instance (and database), so each one seeds
/// its own uniquely named media and asserts only on that.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AppJourneyTests(UiFixture ui)
{
    private const string StrokeScript = """{"actions":[{"at":0,"pos":0},{"at":1000,"pos":100},{"at":2000,"pos":0},{"at":3000,"pos":100}]}""";

    [Fact]
    public Task Startup_ShowsMainWindow_WithIsolatedDatabase() => ui.RunAsync(d =>
    {
        Assert.True(d.Window.IsVisible);
        Assert.Equal("HandyPlayer", d.Window.Title);
        Assert.True(File.Exists(Path.Combine(ui.DataDir, "handy.db")), "database should be created in the test data dir");
        d.Screenshot("startup");
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("Nav.Library", 1, typeof(LibraryViewModel))]
    [InlineData("Nav.Playlists", 2, typeof(PlaylistListViewModel))]
    [InlineData("Nav.Patterns", 3, typeof(PatternModeViewModel))]
    [InlineData("Nav.Settings", 4, typeof(SettingsViewModel))]
    [InlineData("Nav.Statistics", 5, typeof(StatsViewModel))]
    public Task Navigation_EveryPageOpensFromTheSidebar(string navItem, int index, Type page) => ui.RunAsync(async d =>
    {
        d.Click(navItem);

        await d.WaitUntilAsync(() => d.Main.SelectedNavIndex == index && d.Main.IsOverlayVisible, $"{navItem} selected");
        Assert.IsType(page, d.Main.OverlayPage);
        d.Screenshot(navItem);

        d.Click("Nav.NowPlaying");
        await d.WaitUntilAsync(() => !d.Main.IsOverlayVisible, "back on the player");
    }, $"Navigation_{navItem}");

    [Fact]
    public Task AddFolder_IndexesVideo_AndPairsItWithItsScript() => ui.RunAsync(async d =>
    {
        var folder = UiDriver.SeedFolder("addfolder", StrokeScript);
        var library = d.Service<LibraryViewModel>();
        library.PickFolderAsync = _ => Task.FromResult<string?>(folder);

        d.Click("Nav.Library");
        d.Click("Library.AddFolder");

        await d.WaitUntilAsync(
            () => library.Items.Any(i => i.MediaItem.Filename == "addfolder" && i.IsPaired),
            "addfolder.mp4 listed and paired");
        var list = d.FindByName<ListBox>("LibraryList");
        Assert.Contains(list.Items.OfType<LibraryItemViewModel>(), i => i.MediaItem.Filename == "addfolder");
        Assert.DoesNotContain(library.Items, i => i.MediaItem.Extension == ".funscript");
        d.Screenshot("library");

        d.Click("Nav.NowPlaying");
    });

    [Fact]
    public Task PlayPairedVideo_PlayerClockDrivesDevice_EmergencyStopHaltsBoth() => ui.RunAsync(async d =>
    {
        ui.Device.Positions.Clear();
        ui.Device.Calls.Clear();
        var folder = UiDriver.SeedFolder("playback", StrokeScript);
        var library = d.Service<LibraryViewModel>();
        library.PickFolderAsync = _ => Task.FromResult<string?>(folder);
        d.Click("Nav.Library");
        d.Click("Library.AddFolder");
        await d.WaitUntilAsync(() => library.Items.Any(i => i.MediaItem.Filename == "playback" && i.IsPaired),
            "playback.mp4 paired");
        var item = library.Items.First(i => i.MediaItem.Filename == "playback");

        // Same command the library list's "Play" context-menu item and double-click invoke.
        await library.PlayItemCommand.ExecuteAsync(item);

        var video = Path.Combine(folder, "playback.mp4");
        await d.WaitUntilAsync(() => ui.Player.LoadedPath == video && ui.Player.State == PlaybackState.Playing,
            "fake player loaded and playing the video");
        Assert.Equal(PlaybackState.Playing, d.Service<IPlaybackCoordinator>().State);

        // The streaming tick engine reads the player clock and sends script keyframes to the device: at 0 ms it
        // heads for the 1000 ms keyframe (pos 100); once the clock passes 1000 ms, for the 2000 ms one (pos 0).
        await d.WaitUntilAsync(() => ui.Device.Positions.Contains((100, 1000)), "device sent the 1000 ms keyframe");
        Assert.DoesNotContain(ui.Device.Positions, p => p == (0, 1000));
        ui.Player.AdvanceTo(1100);
        await d.WaitUntilAsync(() => ui.Device.Positions.Contains((0, 1000)), "device sent the 2000 ms keyframe");

        d.Click("Nav.NowPlaying");
        d.Screenshot("playing");
        d.Click("Player.EmergencyStop");

        await d.WaitUntilAsync(() => ui.Device.Calls.Contains("EmergencyStop"), "device emergency stop");
        await d.WaitUntilAsync(() => ui.Player.State == PlaybackState.Stopped, "player stopped");
        Assert.Equal(PlaybackState.Stopped, d.Service<IPlaybackCoordinator>().State);
    });

    [Fact]
    public Task SpaceKey_TogglesPlayPause() => ui.RunAsync(async d =>
    {
        var folder = UiDriver.SeedFolder("spacekey", StrokeScript);
        var library = d.Service<LibraryViewModel>();
        library.PickFolderAsync = _ => Task.FromResult<string?>(folder);
        d.Click("Nav.Library");
        d.Click("Library.AddFolder");
        await d.WaitUntilAsync(() => library.Items.Any(i => i.MediaItem.Filename == "spacekey"), "spacekey.mp4 listed");
        await library.PlayItemCommand.ExecuteAsync(library.Items.First(i => i.MediaItem.Filename == "spacekey"));
        await d.WaitUntilAsync(() => ui.Player.State == PlaybackState.Playing, "playing");
        d.Click("Nav.NowPlaying");
        // Clicking the sidebar leaves focus on its ListBoxItem, where the app deliberately ignores Space (it selects
        // items there). Put focus on the transport bar, as a user would after clicking a player control. The window's
        // tunnelling key handler runs first and marks Space handled, so the button itself does not also fire.
        Assert.True(d.Find("Player.PlayPause").Focus());

        d.Press(Key.Space, PhysicalKey.Space);
        await d.WaitUntilAsync(() => ui.Player.State == PlaybackState.Paused, "paused by Space");

        d.Press(Key.Space, PhysicalKey.Space);
        await d.WaitUntilAsync(() => ui.Player.State == PlaybackState.Playing, "resumed by Space");

        await d.Service<IPlaybackCoordinator>().StopAsync();
    });
}
