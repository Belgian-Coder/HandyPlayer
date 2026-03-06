using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Library.RelocateMediaFile;

public class RelocateMediaFileHandler(IMediaFileRepository mediaRepo) : ICommandHandler<RelocateMediaFileCommand, Unit>
{
    public async Task<Unit> HandleAsync(RelocateMediaFileCommand command, CancellationToken ct = default)
    {
        await mediaRepo.RelocateAsync(command.Id, command.NewFullPath);
        return Unit.Value;
    }
}
