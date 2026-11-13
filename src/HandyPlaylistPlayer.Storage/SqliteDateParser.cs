using System.Globalization;

namespace HandyPlaylistPlayer.Storage;

/// <summary>
/// Fast paths for the two date formats stored in the database; anything else falls back to <see cref="DateTime.Parse(string, IFormatProvider, DateTimeStyles)"/>
/// with the same arguments as before, so results are identical.
/// </summary>
internal static class SqliteDateParser
{
    public static DateTime Parse(string text)
    {
        // SQLite datetime('now') defaults: "yyyy-MM-dd HH:mm:ss"
        if (text.Length == 19 &&
            DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            return result;

        // Round-trip ("o") format written by DateTime.ToString("o")
        if (text.Length >= 27 &&
            DateTime.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result))
            return result;

        return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
