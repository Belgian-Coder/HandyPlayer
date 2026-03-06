namespace HandyPlaylistPlayer.Core.Dispatching;

public interface ICommand<TResult>;

public interface ICommand : ICommand<Unit>;
