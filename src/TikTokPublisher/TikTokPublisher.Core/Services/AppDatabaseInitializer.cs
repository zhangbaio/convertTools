using Microsoft.Data.Sqlite;

namespace TikTokPublisher.Core.Services;

/// <summary>确保应用主 SQLite 库存在（全局设置、执行历史等）。</summary>
public static class AppDatabaseInitializer
{
    public static void EnsureInitialized(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();

        using var tx = conn.BeginTransaction();
        conn.ExecuteNonQuery(
            """
            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """, tx);
        conn.ExecuteNonQuery(
            """
            CREATE TABLE IF NOT EXISTS upload_task_events (
                event_id TEXT PRIMARY KEY,
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """, tx);
        conn.ExecuteNonQuery(
            """
            CREATE INDEX IF NOT EXISTS idx_upload_task_events_created_at
                ON upload_task_events(created_at);
            """, tx);
        conn.ExecuteNonQuery(
            """
            CREATE TABLE IF NOT EXISTS ai_rewrite_history (
                rewrite_id TEXT PRIMARY KEY,
                account_profile_id TEXT NOT NULL DEFAULT '',
                original_title TEXT NOT NULL DEFAULT '',
                original_synopsis TEXT NOT NULL DEFAULT '',
                new_title TEXT NOT NULL DEFAULT '',
                new_synopsis TEXT NOT NULL DEFAULT '',
                variant_key TEXT NOT NULL DEFAULT '',
                model_name TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                payload_json TEXT NOT NULL DEFAULT '{}'
            );
            """, tx);
        conn.ExecuteNonQuery(
            """
            CREATE INDEX IF NOT EXISTS idx_ai_rewrite_history_title
                ON ai_rewrite_history(original_title, account_profile_id, created_at);
            """, tx);
        tx.Commit();
    }

    private static void ExecuteNonQuery(this SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null)
            cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }
}
