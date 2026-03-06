namespace HandyPlaylistPlayer.Core.Dispatching;

public interface IValidator<in T>
{
    ValidationResult Validate(T instance);
}
