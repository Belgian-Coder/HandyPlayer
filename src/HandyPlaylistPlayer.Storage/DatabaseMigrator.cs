using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Storage;

public class DatabaseMigrator(DatabaseConfig config, ILogger<DatabaseMigrator> logger)
{
    public async Task MigrateAsync()
    {
        await using var connection = new SqliteConnection(config.ConnectionString);
        await connection.OpenAsync();

        // Enable foreign key constraints
        await using (var fkCmd = connection.CreateCommand())
        {
            fkCmd.CommandText = "PRAGMA foreign_keys = ON";
            await fkCmd.ExecuteNonQueryAsync();
        }

        // Performance PRAGMAs — WAL mode for better concurrent read/write
        await using (var walCmd = connection.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode = WAL";
            await walCmd.ExecuteNonQueryAsync();
        }
        await using (var syncCmd = connection.CreateCommand())
        {
            syncCmd.CommandText = "PRAGMA synchronous = NORMAL";
            await syncCmd.ExecuteNonQueryAsync();
        }
        await using (var cacheCmd = connection.CreateCommand())
        {
            cacheCmd.CommandText = "PRAGMA cache_size = -8000";
            await cacheCmd.ExecuteNonQueryAsync();
        }
        await using (var mmapCmd = connection.CreateCommand())
        {
            mmapCmd.CommandText = "PRAGMA mmap_size = 30000000";
            await mmapCmd.ExecuteNonQueryAsync();
        }
        await using (var tempCmd = connection.CreateCommand())
        {
            tempCmd.CommandText = "PRAGMA temp_store = MEMORY";
            await tempCmd.ExecuteNonQueryAsync();
        }

        // Create schema_version table if it doesn't exist
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_version (
                    version     INTEGER PRIMARY KEY,
                    applied_at  TEXT NOT NULL DEFAULT (datetime('now'))
                )
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Get current version
        int currentVersion = 0;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
            var result = await cmd.ExecuteScalarAsync();
            currentVersion = Convert.ToInt32(result);
        }

        logger.LogInformation("Current database version: {Version}", currentVersion);

        // Find and apply migrations
        var assembly = Assembly.GetExecutingAssembly();
        var migrations = assembly.GetManifestResourceNames()
            .Where(n => n.Contains("Migrations") && n.EndsWith(".sql"))
            .OrderBy(n => n)
            .ToList();

        foreach (var migrationName in migrations)
        {
            // Extract version number from name like "...Migrations.001_InitialSchema.sql"
            var fileName = migrationName.Split('.').Reverse().Skip(1).First(); // Get part before .sql
            var versionStr = fileName.Split('_')[0];
            if (!int.TryParse(versionStr, out int version)) continue;
            if (version <= currentVersion) continue;

            logger.LogInformation("Applying migration {Version}: {Name}", version, migrationName);

            var stream = assembly.GetManifestResourceStream(migrationName);
            if (stream == null)
            {
                logger.LogWarning("Migration resource not found: {Name}", migrationName);
                continue;
            }
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();

            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                // Split and execute each statement separately (SQLite batch support varies)
                var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0);
                foreach (var stmt in statements)
                {
                    await using var stmtCmd = connection.CreateCommand();
                    stmtCmd.Transaction = (SqliteTransaction)transaction;
                    stmtCmd.CommandText = stmt;
                    await stmtCmd.ExecuteNonQueryAsync();
                }

                await using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = (SqliteTransaction)transaction;
                    cmd.CommandText = "INSERT INTO schema_version (version) VALUES (@v)";
                    cmd.Parameters.AddWithValue("@v", version);
                    await cmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
                logger.LogInformation("Migration {Version} applied successfully", version);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                logger.LogError(ex, "Migration {Version} failed", version);
                throw;
            }
        }
    }
}
