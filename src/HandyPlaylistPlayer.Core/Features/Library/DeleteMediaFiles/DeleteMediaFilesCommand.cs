using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Library.DeleteMediaFiles;

public record DeleteMediaFilesCommand(IReadOnlyList<MediaItem> Items) : ICommand;
