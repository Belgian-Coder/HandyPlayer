using HandyPlaylistPlayer.Core.Features.Library.UpdatePairingOffset;
using HandyPlaylistPlayer.Core.Interfaces;
using NSubstitute;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class UpdatePairingOffsetHandlerTests
{
    private readonly IPairingRepository _pairingRepo = Substitute.For<IPairingRepository>();
    private readonly UpdatePairingOffsetHandler _handler;

    public UpdatePairingOffsetHandlerTests()
    {
        _handler = new UpdatePairingOffsetHandler(_pairingRepo);
    }

    [Fact]
    public async Task HandleAsync_CallsUpdateOffsetAsync_WithCorrectArguments()
    {
        var command = new UpdatePairingOffsetCommand(VideoId: 10, ScriptId: 20, OffsetMs: 150);

        await _handler.HandleAsync(command);

        await _pairingRepo.Received(1).UpdateOffsetAsync(10, 20, 150);
    }

    [Fact]
    public async Task HandleAsync_NegativeOffset_PassesThrough()
    {
        var command = new UpdatePairingOffsetCommand(VideoId: 5, ScriptId: 7, OffsetMs: -300);

        await _handler.HandleAsync(command);

        await _pairingRepo.Received(1).UpdateOffsetAsync(5, 7, -300);
    }

    [Fact]
    public async Task HandleAsync_ZeroOffset_IsValid()
    {
        var command = new UpdatePairingOffsetCommand(VideoId: 1, ScriptId: 2, OffsetMs: 0);

        await _handler.HandleAsync(command);

        await _pairingRepo.Received(1).UpdateOffsetAsync(1, 2, 0);
    }
}
