using System.Text.RegularExpressions;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Services;

public partial class FilenameNormalizer : IFilenameNormalizer
{
    public string Normalize(string filename)
    {
        // Remove extension
        var name = Path.GetFileNameWithoutExtension(filename);

        // Lowercase
        name = name.ToLowerInvariant();

        // Remove bracket groups: [...] and (...)
        name = BracketPattern().Replace(name, " ");

        // Remove known tags (single compiled regex alternation instead of 38 string.Replace calls)
        name = KnownTagsPattern().Replace(name, " ");

        // Replace separators with spaces
        name = SeparatorPattern().Replace(name, " ");

        // Collapse whitespace
        name = WhitespacePattern().Replace(name, " ");

        return name.Trim();
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^\)]*\)")]
    private static partial Regex BracketPattern();

    [GeneratedRegex(@"\b(?:1080p|2160p|4k|720p|480p|uhd|hd|sd|x264|x265|h264|h265|hevc|avc|av1|60fps|30fps|120fps|vr|lr|tb|sbs|180|360|trailer|teaser|preview|sample|bluray|bdrip|webrip|webdl|dvdrip|hdtv|aac|ac3|dts|flac|mp3|remastered|extended|uncut|directors)\b", RegexOptions.IgnoreCase)]
    private static partial Regex KnownTagsPattern();

    [GeneratedRegex(@"[_\-.]")]
    private static partial Regex SeparatorPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
