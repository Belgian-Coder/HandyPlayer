using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Playback.LoadMedia;

public class LoadMediaValidator : IValidator<LoadMediaCommand>
{
    public ValidationResult Validate(LoadMediaCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.VideoPath))
            return ValidationResult.Failure("Video path is required.");
        if (!File.Exists(command.VideoPath))
            return ValidationResult.Failure("Video file not found.");
        return ValidationResult.Success();
    }
}
