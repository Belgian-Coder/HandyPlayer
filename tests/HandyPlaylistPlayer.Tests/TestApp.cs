using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace HandyPlaylistPlayer.Tests;

/// <summary>
/// Minimal Avalonia application for headless tests.
/// Does NOT load App.axaml — keeps tests fast and avoids heavy DI setup.
/// </summary>
internal class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}

/// <summary>
/// xUnit class fixture that starts a headless Avalonia runtime for one test class.
/// Usage: add [Collection("Avalonia")] + IClassFixture&lt;AvaloniaTestFixture&gt; to your test class.
/// </summary>
public sealed class AvaloniaTestFixture : IDisposable
{
    public AvaloniaTestFixture()
    {
        // UseHeadlessDrawing = true is required: false skips rendering subsystem
        // registration and causes "No rendering system configured" in SetupWithoutStarting().
        AppBuilder.Configure<HeadlessTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
            .SetupWithoutStarting();
    }

    /// <summary>
    /// Runs <paramref name="action"/> synchronously on the Avalonia UI dispatcher.
    /// </summary>
    public void RunOnUiThread(Action action)
    {
        var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        var tcs = new TaskCompletionSource();
        dispatcher.Post(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        tcs.Task.GetAwaiter().GetResult();
    }

    public void Dispose() { }
}
