using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.PatternMode.GeneratePattern;

public class GeneratePatternValidator : IValidator<GeneratePatternCommand>
{
    public ValidationResult Validate(GeneratePatternCommand cmd)
    {
        var errors = new List<string>();
        if (cmd.FrequencyHz <= 0)
            errors.Add("Frequency must be positive.");
        if (cmd.DurationMs <= 0)
            errors.Add("Duration must be positive.");
        if (cmd.AmplitudeMin < 0 || cmd.AmplitudeMin > 100)
            errors.Add("AmplitudeMin must be 0-100.");
        if (cmd.AmplitudeMax < 0 || cmd.AmplitudeMax > 100)
            errors.Add("AmplitudeMax must be 0-100.");
        return errors.Count > 0 ? ValidationResult.Failure(errors) : ValidationResult.Success();
    }
}
