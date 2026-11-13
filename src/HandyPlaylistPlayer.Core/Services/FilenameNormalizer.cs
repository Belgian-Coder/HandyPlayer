using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Services;

/// <summary>
/// Normalizes media file names so videos and scripts can be matched. Single-pass, span-based (one allocation:
/// the result). Produces exactly what the previous regex pipeline produced:
/// <c>lowercase → drop [..] and (..) groups → drop whole-word release tags → [_-.] to space → collapse whitespace → trim</c>.
/// </summary>
public class FilenameNormalizer : IFilenameNormalizer
{
    // Whole-word release tags (the former \b(?:...)\b alternation). Because every tag consists of word characters only,
    // "\bTAG\b" matches exactly when a maximal run of word characters equals a tag, so a set lookup per word is equivalent.
    private static readonly FrozenSet<string> KnownTags = new[]
    {
        "1080p", "2160p", "4k", "720p", "480p", "uhd", "hd", "sd", "x264", "x265", "h264", "h265", "hevc", "avc", "av1",
        "60fps", "30fps", "120fps", "vr", "lr", "tb", "sbs", "180", "360", "trailer", "teaser", "preview", "sample",
        "bluray", "bdrip", "webrip", "webdl", "dvdrip", "hdtv", "aac", "ac3", "dts", "flac", "mp3", "remastered",
        "extended", "uncut", "directors"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> TagLookup =
        KnownTags.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly int MaxTagLength = KnownTags.Max(t => t.Length);

    private const int StackLimit = 256;

    public string Normalize(string filename)
    {
        // Remove extension
        var name = Path.GetFileNameWithoutExtension(filename.AsSpan());
        int length = name.Length;

        char[]? rented = null;
        Span<char> buffer = length <= StackLimit
            ? stackalloc char[StackLimit]
            : (rented = ArrayPool<char>.Shared.Rent(length));
        try
        {
            buffer = buffer[..length];

            // Lowercase
            name.ToLowerInvariant(buffer);

            // Remove bracket groups: [...] and (...) (each replaced by one space)
            int w = 0;
            for (int r = 0; r < length;)
            {
                char c = buffer[r];
                if (c == '[' || c == '(')
                {
                    int close = buffer[(r + 1)..length].IndexOf(c == '[' ? ']' : ')');
                    if (close >= 0)
                    {
                        buffer[w++] = ' ';
                        r += close + 2;
                        continue;
                    }
                }
                buffer[w++] = c;
                r++;
            }
            var text = buffer[..w];

            // Remove known tags: blank out every whole word that is a tag
            for (int i = 0; i < text.Length;)
            {
                if (!IsBoundaryWordChar(text[i])) { i++; continue; }

                int j = i + 1;
                while (j < text.Length && IsBoundaryWordChar(text[j])) j++;

                int wordLength = j - i;
                if (wordLength <= MaxTagLength && TagLookup.Contains(text[i..j]))
                    text[i..j].Fill(' ');
                i = j;
            }

            // Replace separators with spaces, collapse whitespace runs, trim
            int o = 0;
            bool pendingSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c is '_' or '-' or '.' || char.IsWhiteSpace(c))
                {
                    pendingSpace = o > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    text[o++] = ' ';
                    pendingSpace = false;
                }
                text[o++] = c;
            }

            return new string(text[..o]);
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Same character set the regex engine uses for \b: \w plus zero-width (non-)joiner.</summary>
    private static bool IsBoundaryWordChar(char c)
    {
        if (char.IsAscii(c)) return char.IsAsciiLetterOrDigit(c) || c == '_';
        if (c == '‍' || c == '‌') return true;

        return char.GetUnicodeCategory(c) is
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation;
    }
}
