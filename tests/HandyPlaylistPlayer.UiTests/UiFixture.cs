using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HandyPlaylistPlayer.App.ViewModels;
using HandyPlaylistPlayer.App.Views;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Runtime;
using HandyPlaylistPlayer.Media.Mpv;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PlayerApp = HandyPlaylistPlayer.App.App;

// All scenarios share one collection ([Collection(UiCollection.Name)]), so they run sequentially on one app.

namespace HandyPlaylistPlayer.UiTests;

/// <summary>Real App.axaml + Skia rendering on the headless platform (renders pixels for screenshots, no display).</summary>
public static class UiAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<PlayerApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}

[CollectionDefinition(Name)]
public sealed class UiCollection : ICollectionFixture<UiFixture>
{
    public const string Name = "UI";
}

/// <summary>
/// One headless application per test run, isolated from the user's profile: its own temp data directory (SQLite
/// database, logs) and fakes for the native edges. Everything else — DI, migrations, view models, views, command
/// handlers, the playback coordinator and tick engine — is the production code.
/// </summary>
public sealed class UiFixture : IDisposable
{
    private readonly HeadlessUnitTestSession _session;
    private MainWindow? _window;

    public string DataDir { get; } = Directory.CreateTempSubdirectory("handyplayer-ui-").FullName;
    public string ArtifactsDir { get; } = Path.Combine(AppContext.BaseDirectory, "ui-artifacts");
    public FakeVideoPlayer Player { get; } = new();
    public FakeDevice Device { get; } = new();

    public UiFixture()
    {
        // Must be set before the App type reads it (App.DataDir is resolved once).
        Environment.SetEnvironmentVariable("HANDYPLAYER_DATA_DIR", DataDir);
        PlayerApp.ServiceOverrides = services =>
        {
            services.AddSingleton<IVideoPlayer>(Player);
            services.AddSingleton<IThumbnailService>(new NullThumbnailService());
        };
        Directory.CreateDirectory(ArtifactsDir);
        _session = HeadlessUnitTestSession.StartNew(typeof(UiAppBuilder), AvaloniaTestIsolationLevel.PerAssembly);
    }

    /// <summary>
    /// Runs a scenario on the UI thread. On failure a screenshot of the main window is saved to
    /// <see cref="ArtifactsDir"/> (uploaded by CI) before the exception propagates.
    /// </summary>
    public Task RunAsync(Func<UiDriver, Task> scenario, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
        => HopToThreadPool(_session.Dispatch(async () =>
        {
            var driver = new UiDriver(this, MainWindow(), name);
            try
            {
                await scenario(driver);
            }
            catch
            {
                driver.Screenshot("FAILED");
                throw;
            }
            return 0;
        }, TestContext.Current.CancellationToken));

    /// <summary>
    /// The session completes dispatch tasks on its own UI thread, so the test framework's continuation (next test,
    /// then fixture Dispose) could run inline on that thread — and <see cref="HeadlessUnitTestSession.Dispose"/>
    /// waits for that very thread to finish: a self-deadlock. Always resume on the thread pool instead.
    /// </summary>
    private static Task HopToThreadPool(Task<int> dispatch) =>
        dispatch.ContinueWith(t => t, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

    private MainWindow MainWindow()
    {
        if (_window != null) return _window;
        _window = new MainWindow
        {
            DataContext = PlayerApp.Services.GetRequiredService<MainViewModel>(),
            Width = 1280,
            Height = 800,
        };
        _window.Show();
        // PlayerViewModel attaches the Handy client in its constructor; drive the fake device instead.
        PlayerApp.Services.GetRequiredService<IPlaybackCoordinator>().SetDeviceBackend(Device);
        UiDriver.Pump();
        return _window;
    }

    public void Dispose()
    {
        _session.Dispose();
        PlayerApp.ServiceOverrides = null;
        try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Playwright-style helpers: find by automation id, click with real pointer input, wait, screenshot.</summary>
public sealed class UiDriver(UiFixture fixture, MainWindow window, string scenario)
{
    private int _shot;

    public MainWindow Window => window;
    public MainViewModel Main => (MainViewModel)window.DataContext!;
    public T Service<T>() where T : notnull => PlayerApp.Services.GetRequiredService<T>();

    /// <summary>Runs queued UI work and a render pass.</summary>
    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public Control? TryFind(string automationId)
    {
        Pump();
        return window.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == automationId && c.IsEffectivelyVisible);
    }

    public Control Find(string automationId) =>
        TryFind(automationId) ?? throw new InvalidOperationException($"No visible control with AutomationId '{automationId}'");

    public T FindByName<T>(string name) where T : Control
    {
        Pump();
        return window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible)
               ?? throw new InvalidOperationException($"No visible {typeof(T).Name} named '{name}'");
    }

    /// <summary>Left-click in the middle of the control via the headless input pipeline (hit testing included).</summary>
    public void Click(string automationId)
    {
        var control = Find(automationId);
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                     ?? throw new InvalidOperationException($"'{automationId}' is not in the window's visual tree");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Pump();
    }

    public void Press(Key key, PhysicalKey physicalKey)
    {
        window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
        window.KeyRelease(key, RawInputModifiers.None, physicalKey, null);
        Pump();
    }

    /// <summary>Polls (pumping the UI) until <paramref name="condition"/> holds; fails with <paramref name="what"/>.</summary>
    public async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            Pump();
            if (condition()) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for: {what}");
            await Task.Delay(20);
        }
    }

    /// <summary>Saves a PNG of the rendered window to the artifacts folder.</summary>
    public string? Screenshot(string step)
    {
        try
        {
            Pump();
            using var frame = window.CaptureRenderedFrame();
            if (frame == null) return null;
            var path = Path.Combine(fixture.ArtifactsDir, $"{scenario}-{++_shot:00}-{step}.png");
            frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            return path;
        }
        catch (Exception)
        {
            return null; // screenshots are diagnostics; never fail a scenario because of one
        }
    }

    /// <summary>Creates a library folder with one video and a matching funscript.</summary>
    public static string SeedFolder(string name, string scriptJson)
    {
        var dir = Directory.CreateTempSubdirectory($"handyplayer-lib-{name}-").FullName;
        File.WriteAllBytes(Path.Combine(dir, $"{name}.mp4"), new byte[1024]);
        File.WriteAllText(Path.Combine(dir, $"{name}.funscript"), scriptJson);
        return dir;
    }
}
