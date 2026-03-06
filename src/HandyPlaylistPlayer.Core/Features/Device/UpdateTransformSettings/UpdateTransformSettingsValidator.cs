using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Device.UpdateTransformSettings;

public class UpdateTransformSettingsValidator : IValidator<UpdateTransformSettingsCommand>
{
    public ValidationResult Validate(UpdateTransformSettingsCommand command)
    {
        var s = command.Settings;
        var errors = new List<string>();

        if (s.RangeMin < 0 || s.RangeMin > 100)
            errors.Add("RangeMin must be 0-100.");
        if (s.RangeMax < 0 || s.RangeMax > 100)
            errors.Add("RangeMax must be 0-100.");
        if (s.RangeMin > s.RangeMax)
            errors.Add("RangeMin cannot exceed RangeMax.");
        if (s.SpeedLimit.HasValue && s.SpeedLimit.Value <= 0)
            errors.Add("SpeedLimit must be positive.");

        return errors.Count > 0 ? ValidationResult.Failure(errors) : ValidationResult.Success();
    }
}
