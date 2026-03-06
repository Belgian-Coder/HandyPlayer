using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Interfaces;

public class PlaybackStateChangedEventArgs(PlaybackState state) : EventArgs
{
    public PlaybackState State { get; } = state;
}

public class PositionChangedEventArgs(long positionMs) : EventArgs
{
    public long PositionMs { get; } = positionMs;
}

public interface IMediaPlayer : IDisposable
{
    Task LoadAsync(string filePath);
    Task PlayAsync();
    Task PauseAsync();
    Task StopAsync();
    Task SeekAsync(long positionMs);
    long PositionMs { get; }
    long DurationMs { get; }
    double Volume { get; set; }
    PlaybackState State { get; }
    event EventHandler<PlaybackStateChangedEventArgs> StateChanged;
    event EventHandler<PositionChangedEventArgs> PositionChanged;
    event EventHandler MediaEnded;
}
