using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Library.AddLibraryRoot;

public class AddLibraryRootValidator : IValidator<AddLibraryRootCommand>
{
    public ValidationResult Validate(AddLibraryRootCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Path))
            return ValidationResult.Failure("Library root path is required.");
        return ValidationResult.Success();
    }
}
