using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Playback.Seek;

public class SeekValidator : IValidator<SeekCommand>
{
    public ValidationResult Validate(SeekCommand command)
    {
        if (command.PositionMs < 0)
            return ValidationResult.Failure("Seek position must be non-negative.");
        return ValidationResult.Success();
    }
}
