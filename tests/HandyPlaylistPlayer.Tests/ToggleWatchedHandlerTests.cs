using HandyPlaylistPlayer.Core.Features.Library.ToggleWatched;
using HandyPlaylistPlayer.Core.Interfaces;
using NSubstitute;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class ToggleWatchedHandlerTests
{
    private readonly IMediaFileRepository _mediaRepo = Substitute.For<IMediaFileRepository>();
    private readonly ToggleWatchedHandler _handler;

    public ToggleWatchedHandlerTests()
    {
        _handler = new ToggleWatchedHandler(_mediaRepo);
    }

    [Fact]
    public async Task MarkAsWatched_SetsTimestamp()
    {
        var command = new ToggleWatchedCommand(42, true);

        await _handler.HandleAsync(command);

        await _mediaRepo.Received(1).MarkWatchedAsync(42, Arg.Is<DateTime?>(d => d != null));
    }

    [Fact]
    public async Task MarkAsUnwatched_ClearsTimestamp()
    {
        var command = new ToggleWatchedCommand(42, false);

        await _handler.HandleAsync(command);

        await _mediaRepo.Received(1).MarkWatchedAsync(42, null);
    }
}
