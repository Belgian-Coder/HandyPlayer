namespace HandyPlaylistPlayer.Core.Models;

public record ScanProgress(
    int TotalFiles,
    int ProcessedFiles,
    int Errors,
    string? CurrentFile,
    IReadOnlyList<string>? ErrorFiles = null);
