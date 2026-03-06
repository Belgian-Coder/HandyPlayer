namespace HandyPlaylistPlayer.Core.Dispatching;

public class ValidationException(ValidationResult result)
    : Exception(string.Join("; ", result.Errors))
{
    public ValidationResult Result { get; } = result;
}
