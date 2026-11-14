using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Storage;
using HandyPlaylistPlayer.Storage.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

/// <summary>
/// Integration tests against a real, migrated SQLite database in a unique temp file per test (mocks cannot catch
/// broken SQL, foreign keys or migrations).
/// </summary>
public sealed class StorageTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"handyplayer-test-{Guid.NewGuid():N}.db");
    private DatabaseConfig _config = null!;

    public async ValueTask InitializeAsync()
    {
        _config = new DatabaseConfig(_dbPath);
        await Migrator().MigrateAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
        }
        return ValueTask.CompletedTask;
    }

    private DatabaseMigrator Migrator() => new(_config, NullLogger<DatabaseMigrator>.Instance);

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new SqliteConnection(_config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<(int RootId, MediaFileRepository Media)> SeedRootAsync(string rootPath = "/lib")
    {
        var rootId = await new LibraryRootRepository(_config).AddAsync(rootPath);
        return (rootId, new MediaFileRepository(_config));
    }

    // Not Path.GetFileName: these tests use Windows-style paths on every OS (backslash is not a separator on Linux).
    private static string FileName(string path) => path.Split('/', '\\')[^1];

    private static MediaItem Video(int rootId, string path) => new()
    {
        LibraryRootId = rootId,
        FullPath = path,
        Filename = FileName(path),
        Extension = Path.GetExtension(path),
    };

    private static MediaItem Script(int rootId, string path) => new()
    {
        LibraryRootId = rootId,
        FullPath = path,
        Filename = FileName(path),
        Extension = ".funscript",
        IsScript = true,
    };

    [Fact]
    public async Task Migrate_FreshDatabase_AppliesEveryMigrationOnce()
    {
        var migrationCount = typeof(DatabaseMigrator).Assembly.GetManifestResourceNames()
            .Count(n => n.Contains("Migrations") && n.EndsWith(".sql", StringComparison.Ordinal));

        Assert.Equal(migrationCount, await ScalarAsync("SELECT MAX(version) FROM schema_version"));
        Assert.Equal(migrationCount, await ScalarAsync("SELECT COUNT(*) FROM schema_version"));
    }

    [Fact]
    public async Task Migrate_IsIdempotent()
    {
        var before = await ScalarAsync("SELECT COUNT(*) FROM schema_version");

        await Migrator().MigrateAsync();

        Assert.Equal(before, await ScalarAsync("SELECT COUNT(*) FROM schema_version"));
    }

    [Fact]
    public async Task Settings_SetTwice_Upserts()
    {
        var settings = new AppSettingsRepository(_config);

        await settings.SetAsync("theme", "dark");
        await settings.SetAsync("theme", "light");

        Assert.Equal("light", await settings.GetAsync("theme"));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM app_settings WHERE key = 'theme'"));
        Assert.Null(await settings.GetAsync("missing"));
    }

    [Fact]
    public async Task MediaUpsert_SamePath_KeepsId_UpdatesMetadata()
    {
        var (rootId, media) = await SeedRootAsync();
        var first = await media.UpsertAsync(Video(rootId, "/lib/a.mp4"));

        var item = Video(rootId, "/lib/a.mp4");
        item.FileSize = 42;
        var second = await media.UpsertAsync(item);

        Assert.Equal(first, second);
        Assert.Equal(42, (await media.GetByIdAsync(first))!.FileSize);
    }

    [Fact]
    public async Task Pairing_DefaultsToZeroOffset_AndPersistsOffsetUpdates()
    {
        var (rootId, media) = await SeedRootAsync();
        var video = await media.UpsertAsync(Video(rootId, "/lib/a.mp4"));
        var script = await media.UpsertAsync(Script(rootId, "/lib/a.funscript"));
        var pairings = new PairingRepository(_config);

        await pairings.UpsertAsync(video, script, isManual: true);
        Assert.Equal(0, (await pairings.GetForVideoAsync(video))!.OffsetMs);

        await pairings.UpdateOffsetAsync(video, script, -150);
        var pairing = (await pairings.GetForVideoAsync(video))!;
        Assert.Equal(-150, pairing.OffsetMs);
        Assert.True(pairing.IsManual);
    }

    [Fact]
    public async Task DeletingMediaFile_CascadesToPairingsAndPlaylistItems()
    {
        var (rootId, media) = await SeedRootAsync();
        var video = await media.UpsertAsync(Video(rootId, "/lib/a.mp4"));
        var script = await media.UpsertAsync(Script(rootId, "/lib/a.funscript"));
        await new PairingRepository(_config).UpsertAsync(video, script, isManual: false);
        var playlists = new PlaylistRepository(_config);
        var playlist = await playlists.CreateAsync("p");
        await playlists.AddItemAsync(playlist, video, 0);

        await media.DeleteByIdAsync(video);

        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM pairings"));
        Assert.Equal(0, await playlists.GetItemCountAsync(playlist));
    }

    [Fact]
    public async Task Playlist_ReorderItems_PersistsNewOrder()
    {
        var (rootId, media) = await SeedRootAsync();
        var a = await media.UpsertAsync(Video(rootId, "/lib/a.mp4"));
        var b = await media.UpsertAsync(Video(rootId, "/lib/b.mp4"));
        var c = await media.UpsertAsync(Video(rootId, "/lib/c.mp4"));
        var playlists = new PlaylistRepository(_config);
        var playlist = await playlists.CreateAsync("p");
        await playlists.AddItemsBatchAsync(playlist, [a, b, c]);

        await playlists.ReorderItemsAsync(playlist, [c, a, b]);

        Assert.Equal([c, a, b], (await playlists.GetItemsAsync(playlist)).Select(i => i.Id));
    }

    [Fact]
    public async Task FolderItems_MatchFolderAndSubfolders_VideosOnly_AnySeparator()
    {
        var (rootId, media) = await SeedRootAsync(@"C:\lib");
        await media.UpsertAsync(Video(rootId, @"C:\lib\show\ep1.mp4"));
        await media.UpsertAsync(Video(rootId, @"C:\lib\show\season2\ep2.mp4"));
        await media.UpsertAsync(Script(rootId, @"C:\lib\show\ep1.funscript"));
        await media.UpsertAsync(Video(rootId, @"C:\lib\showcase\other.mp4"));

        var items = await new PlaylistRepository(_config).GetFolderItemsAsync("C:/lib/show");

        Assert.Equal(["ep1.mp4", "ep2.mp4"], items.Select(i => i.Filename).Order());
    }

    [Theory]
    [InlineData("/lib/a_b", "/lib/axb/clip.mp4")]
    [InlineData("/lib/100%", "/lib/100x/clip.mp4")]
    public async Task FolderItems_WildcardCharactersInFolderName_AreLiteral(string folder, string lookalikePath)
    {
        var (rootId, media) = await SeedRootAsync();
        await media.UpsertAsync(Video(rootId, folder + "/real.mp4"));
        await media.UpsertAsync(Video(rootId, lookalikePath));

        var items = await new PlaylistRepository(_config).GetFolderItemsAsync(folder);

        Assert.Equal("real.mp4", Assert.Single(items).Filename);
    }
}
