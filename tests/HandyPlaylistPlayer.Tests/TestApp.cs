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
/// xUnit class fixture that provides a headless Avalonia runtime for tests that need Application.Current.
/// Avalonia 12 binds the UI Dispatcher to the thread that creates it, and xUnit runs each test class on
/// its own thread, so the runtime lives in one process-wide <see cref="HeadlessUnitTestSession"/> with a
/// dedicated UI thread; all Avalonia work is marshalled onto it via <see cref="RunOnUiThread"/>.
/// Usage: add [Collection("Avalonia")] + IClassFixture&lt;AvaloniaTestFixture&gt; to your test class.
/// </summary>
public sealed class AvaloniaTestFixture : IDisposable
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp)));

    /// <summary>
    /// Runs <paramref name="action"/> synchronously on the Avalonia UI thread.
    /// Exceptions (including failed assertions) are rethrown to the caller.
    /// </summary>
    public void RunOnUiThread(Action action) =>
        Session.Value.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    public void Dispose() { }
}
