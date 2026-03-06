using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Library.RelocateMediaFile;

public record RelocateMediaFileCommand(int Id, string NewFullPath) : ICommand;
