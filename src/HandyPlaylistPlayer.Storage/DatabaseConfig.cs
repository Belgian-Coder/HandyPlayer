namespace HandyPlaylistPlayer.Storage;

public record DatabaseConfig(string DatabasePath)
{
    public string ConnectionString => $"Data Source={DatabasePath};Foreign Keys=True;Pooling=True";
}
