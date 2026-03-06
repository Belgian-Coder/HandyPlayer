using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Presets.CreatePreset;

public class CreatePresetValidator : IValidator<CreatePresetCommand>
{
    public ValidationResult Validate(CreatePresetCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Preset.Name))
            return ValidationResult.Failure("Preset name is required.");
        return ValidationResult.Success();
    }
}
