using Microsoft.Data.Sqlite;

namespace TikTokPublisher.Core.Services;

/// <summary>确保 Python 客户端 SQLite 库存在且含账号表（与 <c>database.py</c> schema v1 子集兼容）。</summary>
public static class PythonDatabaseInitializer
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
            CREATE TABLE IF NOT EXISTS app_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );
            """, tx);
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
            CREATE TABLE IF NOT EXISTS tiktok_account_profiles (
                profile_id TEXT PRIMARY KEY,
                name TEXT NOT NULL DEFAULT '',
                display_order INTEGER NOT NULL DEFAULT 0,
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """, tx);
        conn.ExecuteNonQuery(
            """
            CREATE TABLE IF NOT EXISTS account_auth_states (
                profile_id TEXT PRIMARY KEY,
                auth_state_path TEXT NOT NULL DEFAULT '',
                last_login_email TEXT NOT NULL DEFAULT '',
                last_login_at TEXT NOT NULL DEFAULT '',
                auth_fingerprint TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL,
                FOREIGN KEY(profile_id) REFERENCES tiktok_account_profiles(profile_id) ON DELETE CASCADE
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
            CREATE INDEX IF NOT EXISTS idx_tiktok_account_profiles_display_order
                ON tiktok_account_profiles(display_order, profile_id);
            """, tx);
        tx.Commit();
    }

    private static void ExecuteNonQuery(this SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null) cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }
}
