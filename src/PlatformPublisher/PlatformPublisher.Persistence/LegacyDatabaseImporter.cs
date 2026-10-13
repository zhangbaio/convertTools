using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace PlatformPublisher.Persistence;

public sealed record LegacyDatabaseImportResult(int Settings, int AnalyticsRows, IReadOnlyList<string> ImportedSources);

public sealed class LegacyDatabaseImporter
{
    private readonly PlatformDatabase _target;
    public LegacyDatabaseImporter(PlatformDatabase target) => _target = target;

    public LegacyDatabaseImportResult Import(string legacySettingsDatabase, string legacyAnalyticsDatabase)
    {
        PlatformDatabaseInitializer.EnsureMainDatabase(_target);
        var settings = 0; var analytics = 0; var sources = new List<string>();
        if (CanImport(legacySettingsDatabase, "legacy-platform-settings", out var settingsHash))
        {
            settings = ImportSettings(legacySettingsDatabase);
            Record("legacy-platform-settings", legacySettingsDatabase, settingsHash, settings);
            sources.Add(legacySettingsDatabase);
        }
        if (CanImport(legacyAnalyticsDatabase, "legacy-analytics-database", out var analyticsHash))
        {
            analytics = ImportAnalytics(legacyAnalyticsDatabase);
            Record("legacy-analytics-database", legacyAnalyticsDatabase, analyticsHash, analytics);
            sources.Add(legacyAnalyticsDatabase);
        }
        return new(settings, analytics, sources);
    }

    private bool CanImport(string path, string key, out string hash)
    {
        hash = string.Empty;
        if (!File.Exists(path) || Path.GetFullPath(path).Equals(_target.Path, StringComparison.OrdinalIgnoreCase)) return false;
        hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        using var connection = _target.Open(readOnly: true); using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_hash FROM app_migrations WHERE migration_key=$key LIMIT 1";
        command.Parameters.AddWithValue("$key", key);
        return !string.Equals(command.ExecuteScalar()?.ToString(), hash, StringComparison.OrdinalIgnoreCase);
    }

    private int ImportSettings(string path)
    {
        return WithAttached(path, (connection, transaction) =>
        {
            if (!TableExists(connection, "legacy", "app_settings")) return 0;
            using var command=connection.CreateCommand();command.Transaction=transaction;
            command.CommandText="""
                INSERT OR IGNORE INTO app_settings(key,value_json,schema_version,updated_at)
                SELECT key,value_json,1,updated_at FROM legacy.app_settings
                """;
            return command.ExecuteNonQuery();
        });
    }

    private int ImportAnalytics(string path)
    {
        return WithAttached(path, (connection, transaction) =>
        {
            var total=0;
            foreach(var table in new[]{"analytics_account_snapshots","analytics_daily_metrics","analytics_subject_daily_metrics","analytics_publish_activities","analytics_collection_runs","analytics_account_mappings","analytics_runtime_state"})
            {
                if(!TableExists(connection,"legacy",table)||!TableExists(connection,"main",table))continue;
                using var command=connection.CreateCommand();command.Transaction=transaction;
                command.CommandText=$"INSERT OR IGNORE INTO main.[{table}] SELECT * FROM legacy.[{table}]";
                total+=command.ExecuteNonQuery();
            }
            return total;
        });
    }

    private int WithAttached(string path, Func<SqliteConnection,SqliteTransaction,int> action)
    {
        _target.WriteGate.Wait();
        try
        {
            using var connection=_target.Open();
            using(var attach=connection.CreateCommand()){attach.CommandText="ATTACH DATABASE $path AS legacy";attach.Parameters.AddWithValue("$path",Path.GetFullPath(path));attach.ExecuteNonQuery();}
            try{using var transaction=connection.BeginTransaction();var count=action(connection,transaction);transaction.Commit();return count;}
            finally{using var detach=connection.CreateCommand();detach.CommandText="DETACH DATABASE legacy";detach.ExecuteNonQuery();}
        }
        finally{_target.WriteGate.Release();}
    }

    private void Record(string key,string path,string hash,int count)
    {
        _target.WriteGate.Wait();try
        {
            using var connection=_target.Open();using var command=connection.CreateCommand();command.CommandText="""
                INSERT INTO app_migrations(migration_key,source_path,source_hash,imported_count,skipped_count,completed_at)
                VALUES($key,$path,$hash,$count,0,$at)
                ON CONFLICT(migration_key) DO UPDATE SET source_path=excluded.source_path,source_hash=excluded.source_hash,
                imported_count=excluded.imported_count,completed_at=excluded.completed_at
                """;
            command.Parameters.AddWithValue("$key",key);command.Parameters.AddWithValue("$path",Path.GetFullPath(path));command.Parameters.AddWithValue("$hash",hash);command.Parameters.AddWithValue("$count",count);command.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));command.ExecuteNonQuery();
        }
        finally{_target.WriteGate.Release();}
    }

    private static bool TableExists(SqliteConnection connection,string database,string table)
    {
        using var command=connection.CreateCommand();command.CommandText=$"SELECT 1 FROM [{database}].sqlite_master WHERE type='table' AND name=$name LIMIT 1";command.Parameters.AddWithValue("$name",table);return command.ExecuteScalar()!=null;
    }
}
