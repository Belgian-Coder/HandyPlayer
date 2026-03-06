namespace HandyPlaylistPlayer.Core;

/// <summary>
/// Device backend type identifiers stored in the database.
/// </summary>
public static class BackendTypes
{
    public const string HandyApi = "handy_api";
    public const string Intiface = "intiface";
}

/// <summary>
/// Handy protocol string identifiers stored in app_settings.
/// </summary>
public static class ProtocolNames
{
    public const string HSSP = "hssp";
    public const string HDSP = "hdsp";
}

/// <summary>
/// Handy device mode numbers used in the v2 API.
/// </summary>
public static class HandyDeviceModes
{
    public const int HAMP = 0;
    public const int HSSP = 1;
    public const int HDSP = 2;

    public static string GetDisplayName(int mode) => mode switch
    {
        HAMP => nameof(HAMP),
        HSSP => nameof(HSSP),
        HDSP => nameof(HDSP),
        _ => $"Unknown ({mode})"
    };
}

/// <summary>
/// Playlist type identifiers stored in the database.
/// </summary>
public static class PlaylistTypes
{
    public const string Static = "static";
    public const string Smart = "smart";
    public const string Folder = "folder";
}

/// <summary>
/// Sort order identifiers for playlists and library views.
/// </summary>
public static class SortOrders
{
    public const string Name = "name";
}
