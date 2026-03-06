using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Playlists.CreatePlaylist;

public class CreatePlaylistValidator : IValidator<CreatePlaylistCommand>
{
    public ValidationResult Validate(CreatePlaylistCommand command)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(command.Name))
            errors.Add("Playlist name is required.");
        if (command.Name?.Length > 100)
            errors.Add("Playlist name must be 100 characters or fewer.");
        if (command.Type is not (PlaylistTypes.Static or PlaylistTypes.Folder or PlaylistTypes.Smart))
            errors.Add("Invalid playlist type.");
        return errors.Count > 0 ? ValidationResult.Failure(errors) : ValidationResult.Success();
    }
}
