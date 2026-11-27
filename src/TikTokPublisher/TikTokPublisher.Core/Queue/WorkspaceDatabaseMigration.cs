using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

public sealed record WorkspaceMigrationResult(
    string WorkspaceId, int QueueProjects, int Archives, int StateDocuments, bool AlreadyMigrated);

public static class WorkspaceDatabaseMigration
{
    private const string QueueColumns =
        "account_profile_id, project_dir, original_title, new_title, synopsis, status, payload_json, created_at, updated_at";
    private const string DocumentColumns =
        "project_dir, workflow_project_dir, document_type, payload_json, created_at, updated_at";
    private const string ArchiveColumns =
        "account_profile_id, original_title, new_title, archive_source, archived_at, archived_source_dir, " +
        "archived_workflow_dir, metadata_path, payload_json, created_at, updated_at";
    private const string WorkspaceSettings =
        "('queue_run_options', 'queue_run_options_by_account', 'deleted_project_dirs', 'removed_project_dirs')";

    /// <summary>Imports one consistent legacy database snapshot. Runtime routing changes only after the import commits.</summary>
    public static WorkspaceMigrationResult MigrateBackup(
        string workspaceRoot, string workspaceBackup, string mainDatabase, CancellationToken cancellationToken = default)
    {
        workspaceRoot = WorkspaceQueueDatabase.WorkspaceKey(workspaceRoot);
        ValidateSource(workspaceBackup, mainDatabase);
        var isLegacyJson = string.Equals(Path.GetExtension(workspaceBackup), ".json", StringComparison.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();
        lock (WorkspaceQueueDatabase.WriteSyncRoot)
        {
            AppDatabaseInitializer.EnsureInitialized(mainDatabase);
            WorkspaceQueueDatabase.EnsureDatabase(mainDatabase);
            using var connection = OpenSnapshot(mainDatabase, SqliteOpenMode.ReadWrite);
            if (!isLegacyJson)
                AttachBackup(connection, workspaceBackup);
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, """
                CREATE TABLE IF NOT EXISTS tiktok_workspaces (
                    workspace_id TEXT PRIMARY KEY,
                    path_key TEXT NOT NULL UNIQUE,
                    workspace_root TEXT NOT NULL,
                    migrated_at TEXT NOT NULL
                );
                """);
            var pathKey = WorkspaceDatabaseLocation.PathKey(workspaceRoot);
            using var lookup = Command(connection, transaction,
                "SELECT workspace_id FROM tiktok_workspaces WHERE path_key = $path", ("$path", pathKey));
            if (lookup.ExecuteScalar() is string existingId)
                return ReadResult(connection, transaction, existingId, alreadyMigrated: true);

            var id = Guid.NewGuid().ToString("N");
            if (isLegacyJson)
                WorkspaceQueueDatabase.ImportLegacyQueueJson(connection, transaction,
                    new WorkspaceDatabaseLocation(mainDatabase, id), workspaceRoot, workspaceBackup);
            else
                ImportRows(connection, transaction, workspaceRoot, id);
            Execute(connection, transaction, """
                INSERT INTO tiktok_workspaces (workspace_id, path_key, workspace_root, migrated_at)
                VALUES ($id, $path, $root, $at)
                """, ("$id", id), ("$path", pathKey), ("$root", workspaceRoot),
                ("$at", DateTimeOffset.UtcNow.ToString("o")));
            var result = ReadResult(connection, transaction, id, alreadyMigrated: false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }
    }

    /// <summary>Creates a consistent snapshot, including SQLite changes still in the WAL.</summary>
    public static void BackupDatabase(string source, string destination)
    {
        ValidateSource(source, destination);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using var input = OpenSnapshot(source, SqliteOpenMode.ReadOnly);
        using var output = OpenSnapshot(destination, SqliteOpenMode.ReadWriteCreate);
        input.BackupDatabase(output);
    }

    public static void BackupWorkspace(string workspaceRoot, string destination)
    {
        var location = WorkspaceDatabaseLocation.Resolve(workspaceRoot);
        if (location.IsInMainDatabase)
            ExportWorkspace(workspaceRoot, location, destination);
        else
            BackupDatabase(location.Path, destination);
    }

    private static void ExportWorkspace(string workspaceRoot, WorkspaceDatabaseLocation location, string destination)
    {
        ValidateSource(location.Path, destination);
        if (File.Exists(destination))
            throw new IOException($"工作区备份文件已存在：{destination}");
        WorkspaceQueueDatabase.EnsureDatabase(destination);
        using var connection = OpenSnapshot(destination, SqliteOpenMode.ReadWrite);
        AttachBackup(connection, location.Path);
        using var transaction = connection.BeginTransaction();
        var root = WorkspaceQueueDatabase.WorkspaceKey(workspaceRoot);
        var id = location.WorkspaceId;
        Execute(connection, transaction, $"""
            INSERT INTO upload_projects (project_id, workspace_path, {QueueColumns})
            SELECT project_id, $root, {QueueColumns} FROM workspace_backup.upload_projects WHERE workspace_path = $id
            """, ("$root", root), ("$id", id));
        Execute(connection, transaction, $"""
            INSERT INTO project_state_documents (document_id, project_id, workspace_path, {DocumentColumns})
            SELECT document_id, project_id, $root, {DocumentColumns}
            FROM workspace_backup.project_state_documents WHERE workspace_path = $id
            """, ("$root", root), ("$id", id));
        Execute(connection, transaction, $"""
            INSERT INTO archive_projects (archive_id, {ArchiveColumns})
            SELECT substr(archive_id, length($id) + 2), {ArchiveColumns}
            FROM workspace_backup.archive_projects WHERE workspace_id = $id
            """, ("$id", id));
        Execute(connection, transaction, """
            INSERT INTO app_settings (key, value_json, updated_at)
            SELECT substr(key, length($prefix) + 1), value_json, updated_at
            FROM workspace_backup.app_settings WHERE substr(key, 1, length($prefix)) = $prefix
            """, ("$prefix", SettingPrefix(id)));
        transaction.Commit();
    }

    internal static void RestoreWorkspace(string workspaceRoot, WorkspaceDatabaseLocation location, string backup)
    {
        ValidateSource(backup, location.Path);
        lock (WorkspaceQueueDatabase.WriteSyncRoot)
        {
            using var connection = OpenSnapshot(location.Path, SqliteOpenMode.ReadWrite);
            AttachBackup(connection, backup);
            using var transaction = connection.BeginTransaction();
            var id = location.WorkspaceId;
            Execute(connection, transaction, "DELETE FROM upload_projects WHERE workspace_path = $id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM project_state_documents WHERE workspace_path = $id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM archive_projects WHERE workspace_id = $id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM app_settings WHERE substr(key, 1, length($prefix)) = $prefix",
                ("$prefix", SettingPrefix(id)));
            ImportRows(connection, transaction, WorkspaceQueueDatabase.WorkspaceKey(workspaceRoot), id);
            transaction.Commit();
        }
    }

    private static void ImportRows(SqliteConnection connection, SqliteTransaction transaction, string workspaceRoot, string id)
    {
        if (!HasBackupTable(connection, transaction, "upload_projects") &&
            !HasBackupTable(connection, transaction, "project_state_documents") &&
            !HasBackupTable(connection, transaction, "archive_projects"))
            throw new InvalidDataException("备份中缺少工作区数据表。");

        connection.CreateFunction("workspace_project_id", (string project) =>
            WorkspaceQueueDatabase.StableProjectId(id, Path.GetFullPath(project)));
        connection.CreateFunction("workspace_document_id", (string project, string type) =>
            ProjectStateDocumentStore.StableDocumentId(id, Path.GetFullPath(project), type));
        foreach (var table in new[] { "upload_projects", "project_state_documents" })
        {
            if (!HasBackupTable(connection, transaction, table))
                continue;
            using var check = Command(connection, transaction, $"SELECT DISTINCT workspace_path FROM workspace_backup.{table}");
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                var sourceRoot = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(sourceRoot) &&
                    WorkspaceDatabaseLocation.PathKey(sourceRoot) != WorkspaceDatabaseLocation.PathKey(workspaceRoot))
                    throw new InvalidDataException($"备份中的工作区与目标目录不符：{sourceRoot}");
            }
        }

        if (HasBackupTable(connection, transaction, "upload_projects"))
            Execute(connection, transaction, $"""
                INSERT INTO upload_projects (project_id, workspace_path, {QueueColumns})
                SELECT workspace_project_id(project_dir), $id, {QueueColumns} FROM workspace_backup.upload_projects
                """, ("$id", id));
        if (HasBackupTable(connection, transaction, "project_state_documents"))
            Execute(connection, transaction, $"""
                INSERT INTO project_state_documents (document_id, project_id, workspace_path, {DocumentColumns})
                SELECT workspace_document_id(project_dir, document_type), workspace_project_id(project_dir), $id,
                    {DocumentColumns} FROM workspace_backup.project_state_documents
                """, ("$id", id));
        if (HasBackupTable(connection, transaction, "archive_projects"))
            Execute(connection, transaction, $"""
                INSERT INTO archive_projects (archive_id, workspace_id, {ArchiveColumns})
                SELECT $id || ':' || archive_id, $id, {ArchiveColumns} FROM workspace_backup.archive_projects
                """, ("$id", id));
        if (HasBackupTable(connection, transaction, "app_settings"))
            Execute(connection, transaction, $"""
                INSERT INTO app_settings (key, value_json, updated_at)
                SELECT $prefix || key, value_json, updated_at FROM workspace_backup.app_settings
                WHERE key IN {WorkspaceSettings}
                """, ("$prefix", SettingPrefix(id)));
    }

    private static WorkspaceMigrationResult ReadResult(
        SqliteConnection connection, SqliteTransaction transaction, string id, bool alreadyMigrated)
    {
        using var command = Command(connection, transaction, """
            SELECT (SELECT count(*) FROM upload_projects WHERE workspace_path = $id),
                   (SELECT count(*) FROM archive_projects WHERE workspace_id = $id),
                   (SELECT count(*) FROM project_state_documents WHERE workspace_path = $id)
            """, ("$id", id));
        using var reader = command.ExecuteReader();
        reader.Read();
        return new WorkspaceMigrationResult(id, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), alreadyMigrated);
    }

    private static string SettingPrefix(string id) => $"{WorkspaceDatabaseLocation.SettingsPrefix}{id}:";

    private static bool HasBackupTable(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = Command(connection, transaction,
            "SELECT 1 FROM workspace_backup.sqlite_master WHERE type = 'table' AND name = $table", ("$table", table));
        return command.ExecuteScalar() is not null;
    }

    private static void ValidateSource(string source, string destination)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("源数据库与目标数据库必须是不同的文件。", nameof(destination));
        if (!File.Exists(source))
            throw new FileNotFoundException("找不到待读取的 SQLite 文件。", source);
    }

    private static SqliteConnection OpenSnapshot(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri,
            Mode = mode, Pooling = false, DefaultTimeout = 30,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void AttachBackup(SqliteConnection connection, string path)
    {
        using var command = Command(connection, null, "ATTACH DATABASE $path AS workspace_backup",
            ("$path", new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro"));
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }
}
