using HandyPlaylistPlayer.Core.Features.Library.DeleteMediaFiles;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class DeleteMediaFilesHandlerTests
{
    private readonly IMediaFileRepository _mediaRepo = Substitute.For<IMediaFileRepository>();
    private readonly IPairingRepository _pairingRepo = Substitute.For<IPairingRepository>();
    private readonly ILogger<DeleteMediaFilesHandler> _logger = Substitute.For<ILogger<DeleteMediaFilesHandler>>();
    private readonly DeleteMediaFilesHandler _handler;

    public DeleteMediaFilesHandlerTests()
    {
        _handler = new DeleteMediaFilesHandler(_mediaRepo, _pairingRepo, _logger);
    }

    [Fact]
    public async Task DeletesVideoAndRemovesPairing()
    {
        var video = new MediaItem { Id = 1, FullPath = "/nonexistent/video.mp4", IsScript = false };
        var command = new DeleteMediaFilesCommand([video]);

        await _handler.HandleAsync(command);

        await _pairingRepo.Received(1).DeleteForVideoAsync(1);
        await _mediaRepo.Received(1).DeleteByIdAsync(1);
    }

    [Fact]
    public async Task DeletesScript_DoesNotDeletePairing()
    {
        var script = new MediaItem { Id = 2, FullPath = "/nonexistent/script.funscript", IsScript = true };
        var command = new DeleteMediaFilesCommand([script]);

        await _handler.HandleAsync(command);

        await _pairingRepo.DidNotReceive().DeleteForVideoAsync(Arg.Any<int>());
        await _mediaRepo.Received(1).DeleteByIdAsync(2);
    }

    [Fact]
    public async Task DeletesMultipleItems()
    {
        var video = new MediaItem { Id = 1, FullPath = "/nonexistent/video.mp4", IsScript = false };
        var script = new MediaItem { Id = 2, FullPath = "/nonexistent/script.funscript", IsScript = true };
        var command = new DeleteMediaFilesCommand([video, script]);

        await _handler.HandleAsync(command);

        await _pairingRepo.Received(1).DeleteForVideoAsync(1);
        await _mediaRepo.Received(1).DeleteByIdAsync(1);
        await _mediaRepo.Received(1).DeleteByIdAsync(2);
    }

    [Fact]
    public async Task ContinuesOnPartialFailure()
    {
        var item1 = new MediaItem { Id = 1, FullPath = "/nonexistent/a.mp4", IsScript = false };
        var item2 = new MediaItem { Id = 2, FullPath = "/nonexistent/b.mp4", IsScript = false };
        _mediaRepo.DeleteByIdAsync(1).Returns(Task.FromException(new IOException("disk error")));

        var command = new DeleteMediaFilesCommand([item1, item2]);
        await _handler.HandleAsync(command);

        // Second item should still be processed despite first failing
        await _pairingRepo.Received(1).DeleteForVideoAsync(2);
        await _mediaRepo.Received(1).DeleteByIdAsync(2);
    }
}
