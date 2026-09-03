using Microsoft.Data.Sqlite;

namespace PlatformPublisher.Persistence;

public static class PlatformDatabaseInitializer
{
    public static void EnsureMainDatabase(PlatformDatabase database)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(database.Path)!);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_migrations(
              version INTEGER PRIMARY KEY, name TEXT NOT NULL, checksum TEXT NOT NULL, applied_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS app_settings(
              key TEXT PRIMARY KEY, value_json TEXT NOT NULL, schema_version INTEGER NOT NULL DEFAULT 1, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS secure_settings(
              key TEXT PRIMARY KEY, identity TEXT NOT NULL, encrypted_blob BLOB NOT NULL,
              encryption_kind TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS platform_accounts(
              account_id TEXT PRIMARY KEY, platform INTEGER NOT NULL, display_name TEXT NOT NULL,
              owner TEXT NOT NULL DEFAULT 'platform',
              platform_user_id TEXT NOT NULL DEFAULT '', session_directory TEXT NOT NULL DEFAULT '',
              config_json TEXT NOT NULL DEFAULT '{}', status TEXT NOT NULL DEFAULT 'offline',
              last_login_at TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT);
            CREATE INDEX IF NOT EXISTS idx_platform_accounts_platform ON platform_accounts(platform,deleted_at);
            CREATE TABLE IF NOT EXISTS account_settings(
              account_id TEXT NOT NULL, key TEXT NOT NULL, value_json TEXT NOT NULL,
              schema_version INTEGER NOT NULL DEFAULT 1, updated_at TEXT NOT NULL,
              PRIMARY KEY(account_id,key));
            CREATE TABLE IF NOT EXISTS publish_jobs(
              job_id TEXT PRIMARY KEY, platform INTEGER NOT NULL, job_kind INTEGER NOT NULL,
              account_id TEXT NOT NULL DEFAULT '', project_name TEXT NOT NULL DEFAULT '',
              project_directory TEXT NOT NULL DEFAULT '', status INTEGER NOT NULL,
              scheduled_at TEXT, attempt_count INTEGER NOT NULL DEFAULT 0, payload_json TEXT NOT NULL,
              created_at TEXT NOT NULL, updated_at TEXT NOT NULL, row_version INTEGER NOT NULL DEFAULT 1);
            CREATE INDEX IF NOT EXISTS idx_publish_jobs_status_schedule ON publish_jobs(status,scheduled_at);
            CREATE INDEX IF NOT EXISTS idx_publish_jobs_account ON publish_jobs(account_id,updated_at);
            CREATE TABLE IF NOT EXISTS publish_job_steps(
              job_id TEXT NOT NULL, step_key TEXT NOT NULL, status INTEGER NOT NULL,
              label TEXT NOT NULL DEFAULT '', message TEXT NOT NULL DEFAULT '',
              started_at TEXT, completed_at TEXT, updated_at TEXT NOT NULL,
              PRIMARY KEY(job_id,step_key), FOREIGN KEY(job_id) REFERENCES publish_jobs(job_id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS publish_item_events(
              event_id TEXT PRIMARY KEY, job_id TEXT NOT NULL, account_id TEXT NOT NULL DEFAULT '',
              item_key TEXT NOT NULL, status TEXT NOT NULL, message TEXT NOT NULL DEFAULT '',
              occurred_at TEXT NOT NULL, payload_json TEXT NOT NULL DEFAULT '{}');
            CREATE INDEX IF NOT EXISTS idx_publish_item_events_job ON publish_item_events(job_id,occurred_at);
            CREATE TABLE IF NOT EXISTS app_migrations(
              migration_key TEXT PRIMARY KEY, source_path TEXT NOT NULL DEFAULT '', source_hash TEXT NOT NULL DEFAULT '',
              imported_count INTEGER NOT NULL DEFAULT 0, skipped_count INTEGER NOT NULL DEFAULT 0,
              completed_at TEXT NOT NULL);
            """);
        EnsureColumn(connection, transaction, "platform_accounts", "owner", "TEXT NOT NULL DEFAULT 'platform'");
        Execute(connection, transaction, """
            UPDATE platform_accounts SET owner='channels'
            WHERE platform=0 AND owner='platform'
              AND (config_json LIKE '%\"ProfileDir\"%' OR config_json LIKE '%\"profileDir\"%')
            """);
        RecordMigration(connection, transaction, 1, "initial-main-schema", "main-v1");
        RecordMigration(connection, transaction, 2, "account-settings-and-owner", "main-v2");
        transaction.Commit();
    }

    public static void EnsureWorkspaceDatabase(PlatformDatabase database)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(database.Path)!);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_migrations(
              version INTEGER PRIMARY KEY, name TEXT NOT NULL, checksum TEXT NOT NULL, applied_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS workspace_projects(
              project_id TEXT PRIMARY KEY, project_directory TEXT NOT NULL COLLATE NOCASE,
              workflow_directory TEXT NOT NULL DEFAULT '' COLLATE NOCASE, original_title TEXT NOT NULL DEFAULT '',
              new_title TEXT NOT NULL DEFAULT '', account_id TEXT NOT NULL DEFAULT '', status TEXT NOT NULL DEFAULT '',
              payload_json TEXT NOT NULL DEFAULT '{}', created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_workspace_projects_directory ON workspace_projects(project_directory);
            CREATE TABLE IF NOT EXISTS project_state_documents(
              document_id TEXT PRIMARY KEY, project_id TEXT NOT NULL DEFAULT '', project_directory TEXT NOT NULL COLLATE NOCASE,
              document_type TEXT NOT NULL, payload_json TEXT NOT NULL DEFAULT '{}', created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_project_state_type ON project_state_documents(project_directory,document_type);
            CREATE TABLE IF NOT EXISTS adx_batches(
              batch_id TEXT PRIMARY KEY, project_directory TEXT NOT NULL COLLATE NOCASE,
              manifest_path TEXT NOT NULL, original_title TEXT NOT NULL DEFAULT '', new_title TEXT NOT NULL DEFAULT '',
              created_at TEXT NOT NULL, updated_at TEXT NOT NULL, payload_json TEXT NOT NULL DEFAULT '{}');
            CREATE TABLE IF NOT EXISTS adx_batch_items(
              batch_id TEXT NOT NULL, material_id TEXT NOT NULL, rank INTEGER NOT NULL,
              video_path TEXT NOT NULL, cover_path TEXT, status TEXT NOT NULL,
              payload_json TEXT NOT NULL DEFAULT '{}', PRIMARY KEY(batch_id,material_id),
              FOREIGN KEY(batch_id) REFERENCES adx_batches(batch_id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS adx_publish_results(
              batch_id TEXT NOT NULL, account_id TEXT NOT NULL, material_id TEXT NOT NULL,
              status TEXT NOT NULL, message TEXT NOT NULL DEFAULT '', updated_at TEXT NOT NULL,
              PRIMARY KEY(batch_id,account_id,material_id),
              FOREIGN KEY(batch_id,material_id) REFERENCES adx_batch_items(batch_id,material_id) ON DELETE CASCADE);
            """);
        RecordMigration(connection, transaction, 1, "initial-workspace-schema", "workspace-v1");
        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void RecordMigration(SqliteConnection connection, SqliteTransaction transaction,
        int version, string name, string checksum)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations(version,name,checksum,applied_at)
            VALUES($version,$name,$checksum,$at)
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$checksum", checksum);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, SqliteTransaction transaction,
        string tableName, string columnName, string declaration)
    {
        using var check=connection.CreateCommand();check.Transaction=transaction;check.CommandText=$"PRAGMA table_info([{tableName}])";
        using var reader=check.ExecuteReader();while(reader.Read())if(reader.GetString(1).Equals(columnName,StringComparison.OrdinalIgnoreCase))return;reader.Close();
        using var alter=connection.CreateCommand();alter.Transaction=transaction;alter.CommandText=$"ALTER TABLE [{tableName}] ADD COLUMN [{columnName}] {declaration}";alter.ExecuteNonQuery();
    }
}
