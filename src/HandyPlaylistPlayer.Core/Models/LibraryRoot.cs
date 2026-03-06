namespace HandyPlaylistPlayer.Core.Models;

public class LibraryRoot
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Label { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastScan { get; set; }
    public string Status { get; set; } = "unknown";
}
