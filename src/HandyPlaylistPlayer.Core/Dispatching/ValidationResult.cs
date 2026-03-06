namespace HandyPlaylistPlayer.Core.Dispatching;

public class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; init; } = [];

    public static ValidationResult Success() => new();

    public static ValidationResult Failure(string error) => new() { Errors = [error] };

    public static ValidationResult Failure(IEnumerable<string> errors) => new() { Errors = [.. errors] };
}
