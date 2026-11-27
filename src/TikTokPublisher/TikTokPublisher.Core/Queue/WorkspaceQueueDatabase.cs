using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TikTokPublisher.Core.Queue;

/// <summary>读写工作目录 <c>.tiktok-task-queue.db</c>，兼容 Python <c>load/save_queue_state_from_database</c>。</summary>
public static class WorkspaceQueueDatabase
{
    private const string QueueOptionsKey = "queue_run_options";
    private const string QueueAccountOptionsKey = "queue_run_options_by_account";
    private const string DeletedProjectDirsKey = "deleted_project_dirs";
    private const string RemovedProjectDirsKey = "removed_project_dirs";
    internal static object WriteSyncRoot { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static WorkspaceQueueState Load(string workspaceRoot)
    {
        var location = WorkspaceDatabaseLocation.Resolve(workspaceRoot);
        var dbPath = location.Path;
        if (File.Exists(dbPath))
        {
            EnsureDatabase(dbPath);
            return LoadFromDatabase(location, workspaceRoot);
        }

        var legacyPath = WorkspaceQueuePaths.LegacyQueueJsonPath(workspaceRoot);
        if (File.Exists(legacyPath))
            return LoadFromLegacyJson(legacyPath);

        return new WorkspaceQueueState();
    }

    public static HashSet<string> LoadDeletedProjectDirs(string workspaceRoot)
    {
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return deleted;

        var location = WorkspaceDatabaseLocation.Resolve(workspaceRoot);
        var dbPath = location.Path;
        if (!File.Exists(dbPath))
            return deleted;

        lock (WriteSyncRoot)
        {
            EnsureDatabaseCore(dbPath);
            using var conn = Open(dbPath);
            deleted = ReadDeletionPaths(conn, location.SettingKey(DeletedProjectDirsKey));
            if (location.IsInMainDatabase)
                deleted.UnionWith(ReadDeletionPaths(conn, location.SettingKey(RemovedProjectDirsKey)));
        }

        return deleted;
    }

    private static HashSet<string> ReadDeletionPaths(SqliteConnection connection, string key, SqliteTransaction? transaction = null)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value_json FROM app_settings WHERE key = $key LIMIT 1";
        command.Parameters.AddWithValue("$key", key);
        if (command.ExecuteScalar() is not string json || string.IsNullOrWhiteSpace(json))
            return paths;

        try
        {
            foreach (var path in JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [])
            {
                if (TryNormalizePath(path, out var full))
                    paths.Add(full);
                else if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path.Trim());
            }
        }
        catch (JsonException) { }
        return paths;
    }

    public static void SaveDeletedProjectDirs(string workspaceRoot, IReadOnlyCollection<string> projectDirs)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return;

        var location = WorkspaceDatabaseLocation.Resolve(workspaceRoot);
        var dbPath = location.Path;
        var paths = projectDirs
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (WriteSyncRoot)
        {
            EnsureDatabaseCore(dbPath);
            using var conn = Open(dbPath);
            UpsertRawSetting(conn, location.SettingKey(DeletedProjectDirsKey), JsonSerializer.Serialize(paths, JsonOptions), DateTimeOffset.Now.ToString("o"));
        }
    }

    public static void Save(string workspaceRoot, IReadOnlyList<QueueProjectItem> items, Dictionary<string, object?>? options = null)
    {
        var owned = new List<QueueProjectItem>(items.Count);
        foreach (var item in items)
        {
            if (item is not null &&
                WorkspaceQueueService.IsProjectInsideWorkspace(workspaceRoot, item.ProjectDir))
            {
                owned.Add(item);
            }
        }

        // 只带来了别的工作目录的路径时，不能写成空队列，否则会把这个工作目录里原有的项目清掉。
        if (owned.Count == 0 && items.Count > 0)
            return;

        var location = WorkspaceDatabaseLocation.Resolve(workspaceRoot);
        var dbPath = location.Path;
        lock (WriteSyncRoot)
        {
            EnsureDatabaseCore(dbPath);
            SaveToDatabase(location, workspaceRoot, owned, options ?? new Dictionary<string, object?>());
        }
    }

    private static WorkspaceQueueState LoadFromDatabase(WorkspaceDatabaseLocation location, string workspaceRoot)
    {
        var workspaceKey = WorkspaceKey(location, workspaceRoot);
        var workspaceAliases = WorkspaceKeyAliases(location, workspaceRoot);
        var state = new WorkspaceQueueState();

        using var conn = OpenConnection(location.Path, readOnly: true);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT value_json FROM app_settings WHERE key = $key LIMIT 1";
            cmd.Parameters.AddWithValue("$key", location.SettingKey(QueueOptionsKey));
            var json = cmd.ExecuteScalar()?.ToString();
            if (!string.IsNullOrWhiteSpace(json))
                state.Options = DeserializeObject(json);
        }

        using (var cmd = conn.CreateCommand())
        {
            var placeholders = string.Join(", ", workspaceAliases.Select((_, index) => $"$workspace{index}"));
            cmd.CommandText = $"""
                SELECT payload_json, project_dir, created_at, updated_at, workspace_path
                FROM upload_projects
                WHERE workspace_path IN ({placeholders})
                ORDER BY updated_at ASC, created_at ASC, project_dir ASC
                """;
            for (var i = 0; i < workspaceAliases.Count; i++)
                cmd.Parameters.AddWithValue($"$workspace{i}", workspaceAliases[i]);

            var rowsByProject = new Dictionary<string, QueueDatabaseRow>(StringComparer.OrdinalIgnoreCase);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var payloadJson = reader.IsDBNull(0) ? "{}" : reader.GetString(0);
                var payload = DeserializeObject(payloadJson);
                if (payload.Count == 0)
                    continue;

                var projectDir = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (payload.TryGetValue("project_dir", out var rawProjectDir))
                    projectDir = rawProjectDir?.ToString() ?? projectDir;
                if (string.IsNullOrWhiteSpace(projectDir))
                    continue;

                var normalizedProjectDir = Path.GetFullPath(projectDir);
                var row = new QueueDatabaseRow(
                    payload,
                    normalizedProjectDir,
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4));

                if (!rowsByProject.TryGetValue(normalizedProjectDir, out var current) ||
                    IsPreferredRow(row, current, workspaceKey))
                {
                    rowsByProject[normalizedProjectDir] = row;
                }
            }

            state.Items.AddRange(rowsByProject.Values
                .OrderBy(row => string.IsNullOrWhiteSpace(row.CreatedAt) ? "9999" : row.CreatedAt, StringComparer.Ordinal)
                .ThenBy(row => row.ProjectDir, StringComparer.OrdinalIgnoreCase)
                .Select(row => QueueProjectItem.FromPayload(row.Payload)));
        }

        return state;
    }

    private static void SaveToDatabase(
        WorkspaceDatabaseLocation location,
        string workspaceRoot,
        IReadOnlyList<QueueProjectItem> items,
        Dictionary<string, object?> options)
    {
        using var conn = Open(location.Path);
        using var tx = conn.BeginTransaction();
        SaveQueueRows(conn, tx, location, workspaceRoot, items, options);
        tx.Commit();
    }

    internal static void ImportLegacyQueueJson(
        SqliteConnection connection, SqliteTransaction transaction, WorkspaceDatabaseLocation location,
        string workspaceRoot, string jsonPath)
    {
        var state = LoadFromLegacyJson(jsonPath);
        if (state.Items.Any(item => !WorkspaceQueueService.IsProjectInsideWorkspace(workspaceRoot, item.ProjectDir)))
            throw new InvalidDataException("旧队列备份包含工作区外的项目。");
        SaveQueueRows(connection, transaction, location, workspaceRoot, state.Items, state.Options);
    }

    private static void SaveQueueRows(
        SqliteConnection conn, SqliteTransaction tx, WorkspaceDatabaseLocation location, string workspaceRoot,
        IReadOnlyList<QueueProjectItem> items, Dictionary<string, object?> options)
    {
        var workspaceKey = WorkspaceKey(location, workspaceRoot);
        var workspaceAliases = WorkspaceKeyAliases(location, workspaceRoot);
        var now = DateTimeOffset.Now.ToString("o");

        var deleted = ReadDeletionPaths(conn, location.SettingKey(DeletedProjectDirsKey), tx);
        if (location.IsInMainDatabase)
            deleted.UnionWith(ReadDeletionPaths(conn, location.SettingKey(RemovedProjectDirsKey), tx));

        UpsertAppSetting(conn, tx, location.SettingKey(QueueOptionsKey), options, now);
        UpsertAppSetting(conn, tx, location.SettingKey(QueueAccountOptionsKey), new Dictionary<string, object?>(), now);

        var seenIds = new List<string>();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ProjectDir)) continue;
            var projectDir = Path.GetFullPath(item.ProjectDir);
            if (deleted.Contains(projectDir)) continue;
            var projectId = StableProjectId(workspaceKey, projectDir);
            seenIds.Add(projectId);
            var payload = item.ToPayload();
            payload["project_dir"] = projectDir;
            var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            var queuedAt = string.IsNullOrWhiteSpace(item.QueuedAt) ? now : item.QueuedAt;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO upload_projects (
                    project_id, account_profile_id, workspace_path, project_dir,
                    original_title, new_title, synopsis, status, payload_json, created_at, updated_at
                ) VALUES (
                    $project_id, $account_profile_id, $workspace_path, $project_dir,
                    $original_title, $new_title, $synopsis, $status, $payload_json, $created_at, $updated_at
                )
                ON CONFLICT(project_id) DO UPDATE SET
                    account_profile_id = excluded.account_profile_id,
                    workspace_path = excluded.workspace_path,
                    project_dir = excluded.project_dir,
                    original_title = excluded.original_title,
                    new_title = excluded.new_title,
                    synopsis = excluded.synopsis,
                    status = excluded.status,
                    payload_json = excluded.payload_json,
                    created_at = excluded.created_at,
                    updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$project_id", projectId);
            cmd.Parameters.AddWithValue("$account_profile_id", item.AccountProfileId);
            cmd.Parameters.AddWithValue("$workspace_path", workspaceKey);
            cmd.Parameters.AddWithValue("$project_dir", projectDir);
            cmd.Parameters.AddWithValue("$original_title", item.OriginalTitle);
            cmd.Parameters.AddWithValue("$new_title", item.NewTitle);
            cmd.Parameters.AddWithValue("$synopsis", item.Description);
            cmd.Parameters.AddWithValue("$status", item.StatusText);
            cmd.Parameters.AddWithValue("$payload_json", payloadJson);
            cmd.Parameters.AddWithValue("$created_at", queuedAt);
            cmd.Parameters.AddWithValue("$updated_at", now);
            cmd.ExecuteNonQuery();
        }

        using (var deleteCmd = conn.CreateCommand())
        {
            var aliasPlaceholders = string.Join(", ", workspaceAliases.Select((_, i) => $"$workspace{i}"));
            if (seenIds.Count > 0)
            {
                var placeholders = string.Join(", ", seenIds.Select((_, i) => $"$id{i}"));
                deleteCmd.CommandText = $"""
                    DELETE FROM upload_projects
                    WHERE workspace_path IN ({aliasPlaceholders})
                      AND (workspace_path <> $workspace OR project_id NOT IN ({placeholders}))
                    """;
                for (var i = 0; i < seenIds.Count; i++)
                    deleteCmd.Parameters.AddWithValue($"$id{i}", seenIds[i]);
            }
            else
            {
                deleteCmd.CommandText = $"DELETE FROM upload_projects WHERE workspace_path IN ({aliasPlaceholders})";
            }

            deleteCmd.Parameters.AddWithValue("$workspace", workspaceKey);
            for (var i = 0; i < workspaceAliases.Count; i++)
                deleteCmd.Parameters.AddWithValue($"$workspace{i}", workspaceAliases[i]);
            deleteCmd.ExecuteNonQuery();
        }
    }

    private static WorkspaceQueueState LoadFromLegacyJson(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;
        var state = new WorkspaceQueueState();
        if (root.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
            state.Options = JsonElementToDictionary(options);
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
                state.Items.Add(QueueProjectItem.FromPayload(JsonElementToDictionary(item)));
        }
        return state;
    }

    public static void DeleteProjects(string workspaceRoot, IReadOnlyCollection<string> projectDirs)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectDir in projectDirs)
        {
            if (TryNormalizePath(projectDir, out var full))
                targets.Add(full);
        }
        if (targets.Count == 0)
            return;

        var location = WorkspaceDatabaseLocation.Resolve(workspaceRoot);
        var dbPath = location.Path;
        if (!File.Exists(dbPath))
            return;

        lock (WriteSyncRoot)
        {
            EnsureDatabaseCore(dbPath);
            var aliases = WorkspaceKeyAliases(location, workspaceRoot);
            using var conn = Open(dbPath);
            using var tx = conn.BeginTransaction();
            var ids = new List<string>();
            using (var select = conn.CreateCommand())
            {
                select.Transaction = tx;
                var placeholders = string.Join(", ", aliases.Select((_, index) => $"$workspace{index}"));
                select.CommandText = $"""
                    SELECT project_id, project_dir
                    FROM upload_projects
                    WHERE workspace_path IN ({placeholders})
                    """;
                for (var i = 0; i < aliases.Count; i++)
                    select.Parameters.AddWithValue($"$workspace{i}", aliases[i]);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var projectDir = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (!TryNormalizePath(projectDir, out var full) || !targets.Contains(full))
                        continue;
                    if (!reader.IsDBNull(0))
                        ids.Add(reader.GetString(0));
                }
            }

            if (ids.Count == 0)
            {
                tx.Commit();
                return;
            }

            using (var delete = conn.CreateCommand())
            {
                delete.Transaction = tx;
                var placeholders = string.Join(", ", ids.Select((_, index) => $"$id{index}"));
                delete.CommandText = $"DELETE FROM upload_projects WHERE project_id IN ({placeholders})";
                for (var i = 0; i < ids.Count; i++)
                    delete.Parameters.AddWithValue($"$id{i}", ids[i]);
                delete.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public static void EnsureDatabase(string dbPath)
    {
        lock (WriteSyncRoot)
            EnsureDatabaseCore(dbPath);
    }

    private static void EnsureDatabaseCore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var conn = Open(dbPath);
        ExecuteNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            )
            """);
        ExecuteNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
            """);
        ExecuteNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS upload_projects (
                project_id TEXT PRIMARY KEY,
                account_profile_id TEXT NOT NULL DEFAULT '',
                workspace_path TEXT NOT NULL DEFAULT '',
                project_dir TEXT NOT NULL DEFAULT '',
                original_title TEXT NOT NULL DEFAULT '',
                new_title TEXT NOT NULL DEFAULT '',
                synopsis TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT '',
                payload_json TEXT NOT NULL DEFAULT '{}',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
            """);
        ExecuteNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_upload_projects_workspace
                ON upload_projects(workspace_path, project_dir)
            """);
        ExecuteNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS project_state_documents (
                document_id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL DEFAULT '',
                workspace_path TEXT NOT NULL DEFAULT '',
                project_dir TEXT NOT NULL DEFAULT '',
                workflow_project_dir TEXT NOT NULL DEFAULT '',
                document_type TEXT NOT NULL DEFAULT '',
                payload_json TEXT NOT NULL DEFAULT '{}',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
            """);
        // Python/早期 C# 版本可能已经创建过同名表。CREATE TABLE IF NOT EXISTS
        // 不会补字段，随后保存证明材料断点就会在 PDF 已生成后抛出 “no column named ...”，
        // 导致界面误报生成失败。这里对已有工作目录执行幂等字段迁移。
        EnsureColumn(conn, "project_state_documents", "document_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "project_state_documents", "project_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "project_state_documents", "workspace_path", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "project_state_documents", "project_dir", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "project_state_documents", "workflow_project_dir", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "project_state_documents", "document_type", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "project_state_documents", "payload_json", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumn(conn, "project_state_documents", "created_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "project_state_documents", "updated_at", "TEXT NOT NULL DEFAULT ''");
        ExecuteNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_project_state_documents_project_type
                ON project_state_documents(project_id, document_type)
            """);
        ExecuteNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS archive_projects (
                archive_id TEXT PRIMARY KEY,
                account_profile_id TEXT NOT NULL DEFAULT '',
                original_title TEXT NOT NULL DEFAULT '',
                new_title TEXT NOT NULL DEFAULT '',
                archive_source TEXT NOT NULL DEFAULT '',
                archived_at TEXT NOT NULL DEFAULT '',
                archived_source_dir TEXT NOT NULL DEFAULT '',
                archived_workflow_dir TEXT NOT NULL DEFAULT '',
                metadata_path TEXT NOT NULL DEFAULT '',
                payload_json TEXT NOT NULL DEFAULT '{}',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
            """);
        EnsureColumn(conn, "archive_projects", "workspace_id", "TEXT NOT NULL DEFAULT ''");
        ExecuteNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_archive_projects_workspace
                ON archive_projects(workspace_id, archived_at DESC);
            """);
        ExecuteNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_archive_projects_archived_at
                ON archive_projects(archived_at DESC, created_at DESC)
            """);
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void EnsureColumn(
        SqliteConnection conn,
        string tableName,
        string columnName,
        string declaration)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info([{tableName}])";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        reader.Close();
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE [{tableName}] ADD COLUMN [{columnName}] {declaration}";
        alter.ExecuteNonQuery();
    }

    internal static SqliteConnection OpenConnection(string dbPath, bool readOnly = false)
    {
        var mode = readOnly ? ";Mode=ReadOnly" : "";
        var conn = new SqliteConnection($"Data Source={dbPath}{mode};Default Timeout=30");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = readOnly
            ? "PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;"
            : "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static SqliteConnection Open(string dbPath) => OpenConnection(dbPath);

    private static void UpsertAppSetting(SqliteConnection conn, SqliteTransaction transaction, string key,
        Dictionary<string, object?> payload, string updatedAt) =>
        UpsertRawSetting(conn, key, JsonSerializer.Serialize(payload, JsonOptions), updatedAt, transaction);

    private static void UpsertRawSetting(SqliteConnection conn, string key, string json, string updatedAt, SqliteTransaction? transaction = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO app_settings (key, value_json, updated_at)
            VALUES ($key, $json, $updated_at)
            ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);
        cmd.ExecuteNonQuery();
    }

    internal static string WorkspaceKey(string workspaceRoot)
    {
        var fullPath = Path.GetFullPath(workspaceRoot.Trim());
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string WorkspaceKey(WorkspaceDatabaseLocation location, string workspaceRoot) =>
        location.IsInMainDatabase ? location.WorkspaceId : WorkspaceKey(workspaceRoot);

    private static IReadOnlyList<string> WorkspaceKeyAliases(WorkspaceDatabaseLocation location, string workspaceRoot) =>
        location.IsInMainDatabase ? [location.WorkspaceId] : WorkspaceKeyAliases(workspaceRoot);

    private static IReadOnlyList<string> WorkspaceKeyAliases(string workspaceRoot)
    {
        var aliases = new List<string>();
        var canonical = WorkspaceKey(workspaceRoot);
        AddAlias(canonical);

        var fullPath = Path.GetFullPath(workspaceRoot.Trim());
        AddAlias(fullPath);

        var root = Path.GetPathRoot(canonical);
        if (string.IsNullOrWhiteSpace(root) ||
            !string.Equals(canonical, root, StringComparison.OrdinalIgnoreCase))
        {
            AddAlias(canonical + Path.DirectorySeparatorChar);
            AddAlias(canonical + Path.AltDirectorySeparatorChar);
        }

        return aliases;

        void AddAlias(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !aliases.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                aliases.Add(value);
            }
        }
    }

    private static bool TryNormalizePath(string? path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            full = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }
    }

    private static bool IsPreferredRow(QueueDatabaseRow candidate, QueueDatabaseRow current, string workspaceKey)
    {
        var comparison = string.Compare(
            string.IsNullOrWhiteSpace(candidate.UpdatedAt) ? candidate.CreatedAt : candidate.UpdatedAt,
            string.IsNullOrWhiteSpace(current.UpdatedAt) ? current.CreatedAt : current.UpdatedAt,
            StringComparison.Ordinal);
        if (comparison != 0)
            return comparison > 0;

        var candidateCanonical = string.Equals(candidate.WorkspacePath, workspaceKey, StringComparison.OrdinalIgnoreCase);
        var currentCanonical = string.Equals(current.WorkspacePath, workspaceKey, StringComparison.OrdinalIgnoreCase);
        return candidateCanonical && !currentCanonical;
    }

    private sealed record QueueDatabaseRow(
        Dictionary<string, object?> Payload,
        string ProjectDir,
        string CreatedAt,
        string UpdatedAt,
        string WorkspacePath);

    internal static string StableProjectId(string workspaceKey, string projectDir)
    {
        var bytes = Encoding.UTF8.GetBytes($"{workspaceKey}\n{projectDir}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static Dictionary<string, object?> DeserializeObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonElementToDictionary(doc.RootElement);
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    private static Dictionary<string, object?> JsonElementToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in element.EnumerateObject())
            dict[prop.Name] = JsonElementToObject(prop.Value);
        return dict;
    }

    private static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object => JsonElementToDictionary(element),
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
        _ => null,
    };
}
