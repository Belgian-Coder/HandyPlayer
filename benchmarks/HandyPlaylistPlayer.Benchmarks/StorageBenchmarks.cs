using BenchmarkDotNet.Attributes;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Storage;
using HandyPlaylistPlayer.Storage.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HandyPlaylistPlayer.Benchmarks;

/// <summary>SQLite repository round trips against a real temp database (WAL), pooled connections.</summary>
[MemoryDiagnoser]
public class StorageBenchmarks
{
    private const int Rows = 5_000;
    private string _dir = null!;
    private DatabaseConfig _config = null!;
    private MediaFileRepository _media = null!;
    private AppSettingsRepository _settings = null!;
    private PairingRepository _pairings = null!;
    private int _rootId;
    private List<MediaItem> _items = null!;
    private List<MediaItem> _batch = null!;
    private int[] _ids = null!;
    private string[] _paths = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "handy_bench_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new DatabaseConfig(Path.Combine(_dir, "bench.db"));
        await new DatabaseMigrator(_config, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();

        _rootId = await new LibraryRootRepository(_config).AddAsync("/library", "bench");
        _media = new MediaFileRepository(_config);
        _settings = new AppSettingsRepository(_config);
        _pairings = new PairingRepository(_config);

        _items = new List<MediaItem>(Rows);
        for (int i = 0; i < Rows; i++)
        {
            bool script = i % 2 == 1;
            var name = $"clip_{i:D5}";
            _items.Add(new MediaItem
            {
                LibraryRootId = _rootId,
                FullPath = $"/library/f{i % 40}/{name}{(script ? ".funscript" : ".mp4")}",
                Filename = name,
                Extension = script ? "funscript" : "mp4",
                FileSize = 1_000_000 + i,
                ModifiedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                IsScript = script
            });
        }
        await _media.UpsertBatchAsync(_items);

        var all = await _media.GetAllAsync();
        _ids = all.Select(m => m.Id).ToArray();
        _paths = all.Select(m => m.FullPath).ToArray();
        _batch = _items.Take(1_000).ToList();

        var videos = all.Where(m => !m.IsScript).Take(500).ToList();
        var scripts = all.Where(m => m.IsScript).Take(500).ToList();
        for (int i = 0; i < 500; i++)
            await _pairings.UpsertAsync(videos[i].Id, scripts[i].Id, false, 0.9);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Transactional upsert of 1,000 rows (prepared statement, one transaction).</summary>
    [Benchmark]
    public Task UpsertBatch_1000() => _media.UpsertBatchAsync(_batch);

    /// <summary>Read the whole 5,000-row table.</summary>
    [Benchmark]
    public async Task<int> GetAll_5000() => (await _media.GetAllAsync()).Count;

    [Benchmark]
    public async Task<int> GetAllVideos_2500() => (await _media.GetAllVideosAsync()).Count;

    /// <summary>200 point lookups by id (a connection open/close per call).</summary>
    [Benchmark(OperationsPerInvoke = 200)]
    public async Task<int> GetById_x200()
    {
        int n = 0;
        for (int i = 0; i < 200; i++)
            if (await _media.GetByIdAsync(_ids[(i * 25) % _ids.Length]) != null) n++;
        return n;
    }

    [Benchmark(OperationsPerInvoke = 200)]
    public async Task<int> GetByPath_x200()
    {
        int n = 0;
        for (int i = 0; i < 200; i++)
            if (await _media.GetByPathAsync(_paths[(i * 25) % _paths.Length]) != null) n++;
        return n;
    }

    [Benchmark]
    public async Task<int> GetByIds_100() => (await _media.GetByIdsAsync(_ids.Take(100))).Count;

    /// <summary>100 app-setting writes + reads.</summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public async Task<int> Settings_SetGet_x100()
    {
        int n = 0;
        for (int i = 0; i < 100; i++)
        {
            await _settings.SetAsync("bench.key." + (i & 15), "value" + i);
            if (await _settings.GetAsync("bench.key." + (i & 15)) != null) n++;
        }
        return n;
    }

    [Benchmark]
    public async Task<int> GetAllPairings_500() => (await _pairings.GetAllPairingsAsync()).Count;
}
