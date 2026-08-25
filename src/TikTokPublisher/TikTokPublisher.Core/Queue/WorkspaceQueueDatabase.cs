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
    internal static object WriteSyncRoot { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static WorkspaceQueueState Load(string workspaceRoot)
    {
        var dbPath = WorkspaceQueuePaths.QueueDatabasePath(workspaceRoot);
        if (File.Exists(dbPath))
        {
            EnsureDatabase(dbPath);
            return LoadFromDatabase(dbPath, workspaceRoot);
        }

        var legacyPath = WorkspaceQueuePaths.LegacyQueueJsonPath(workspaceRoot);
        if (File.Exists(legacyPath))
            return LoadFromLegacyJson(legacyPath);

        return new WorkspaceQueueState();
    }

    public static void Save(string workspaceRoot, IReadOnlyList<QueueProjectItem> items, Dictionary<string, object?>? options = null)
    {
        var dbPath = WorkspaceQueuePaths.QueueDatabasePath(workspaceRoot);
        lock (WriteSyncRoot)
        {
            EnsureDatabaseCore(dbPath);
            SaveToDatabase(dbPath, workspaceRoot, items, options ?? new Dictionary<string, object?>());
        }
    }

    private static WorkspaceQueueState LoadFromDatabase(string dbPath, string workspaceRoot)
    {
        var workspaceKey = WorkspaceKey(workspaceRoot);
        var workspaceAliases = WorkspaceKeyAliases(workspaceRoot);
        var state = new WorkspaceQueueState();

        using var conn = Open(dbPath);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT value_json FROM app_settings WHERE key = $key LIMIT 1";
            cmd.Parameters.AddWithValue("$key", QueueOptionsKey);
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
        string dbPath,
        string workspaceRoot,
        IReadOnlyList<QueueProjectItem> items,
        Dictionary<string, object?> options)
    {
        var workspaceKey = WorkspaceKey(workspaceRoot);
        var workspaceAliases = WorkspaceKeyAliases(workspaceRoot);
        var now = DateTimeOffset.Now.ToString("o");

        using var conn = Open(dbPath);
        using var tx = conn.BeginTransaction();

        UpsertAppSetting(conn, QueueOptionsKey, options, now);
        UpsertAppSetting(conn, QueueAccountOptionsKey, new Dictionary<string, object?>(), now);

        var seenIds = new List<string>();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ProjectDir)) continue;
            var projectDir = Path.GetFullPath(item.ProjectDir);
            var projectId = StableProjectId(workspaceKey, projectDir);
            seenIds.Add(projectId);
            var payload = item.ToPayload();
            payload["project_dir"] = projectDir;
            var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            var queuedAt = string.IsNullOrWhiteSpace(item.QueuedAt) ? now : item.QueuedAt;

            using var cmd = conn.CreateCommand();
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

        tx.Commit();
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

    private static void UpsertAppSetting(SqliteConnection conn, string key, Dictionary<string, object?> payload, string updatedAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_settings (key, value_json, updated_at)
            VALUES ($key, $json, $updated_at)
            ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(payload, JsonOptions));
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

    private static string StableProjectId(string workspaceKey, string projectDir)
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
