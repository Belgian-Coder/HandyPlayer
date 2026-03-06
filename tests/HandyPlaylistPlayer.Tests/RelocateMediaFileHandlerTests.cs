using HandyPlaylistPlayer.Core.Features.Library.RelocateMediaFile;
using HandyPlaylistPlayer.Core.Interfaces;
using NSubstitute;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class RelocateMediaFileHandlerTests
{
    private readonly IMediaFileRepository _mediaRepo = Substitute.For<IMediaFileRepository>();
    private readonly RelocateMediaFileHandler _handler;

    public RelocateMediaFileHandlerTests()
    {
        _handler = new RelocateMediaFileHandler(_mediaRepo);
    }

    [Fact]
    public async Task HandleAsync_CallsRelocateAsync_WithCorrectArguments()
    {
        var command = new RelocateMediaFileCommand(Id: 42, NewFullPath: @"D:\Videos\new_location.mp4");

        await _handler.HandleAsync(command);

        await _mediaRepo.Received(1).RelocateAsync(42, @"D:\Videos\new_location.mp4");
    }

    [Fact]
    public async Task HandleAsync_ReturnsUnitValue()
    {
        var command = new RelocateMediaFileCommand(Id: 1, NewFullPath: "/some/path.mp4");

        var result = await _handler.HandleAsync(command);

        Assert.Equal(HandyPlaylistPlayer.Core.Dispatching.Unit.Value, result);
    }
}
