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
        EnsureDatabase(dbPath);
        SaveToDatabase(dbPath, workspaceRoot, items, options ?? new Dictionary<string, object?>());
    }

    private static WorkspaceQueueState LoadFromDatabase(string dbPath, string workspaceRoot)
    {
        var workspaceKey = WorkspaceKey(workspaceRoot);
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
            cmd.CommandText = """
                SELECT payload_json
                FROM upload_projects
                WHERE workspace_path = $workspace
                ORDER BY created_at ASC, project_dir ASC
                """;
            cmd.Parameters.AddWithValue("$workspace", workspaceKey);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var payloadJson = reader.IsDBNull(0) ? "{}" : reader.GetString(0);
                var payload = DeserializeObject(payloadJson);
                if (payload.Count > 0)
                    state.Items.Add(QueueProjectItem.FromPayload(payload));
            }
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
            if (seenIds.Count > 0)
            {
                var placeholders = string.Join(", ", seenIds.Select((_, i) => $"$id{i}"));
                deleteCmd.CommandText = $"DELETE FROM upload_projects WHERE workspace_path = $workspace AND project_id NOT IN ({placeholders})";
                deleteCmd.Parameters.AddWithValue("$workspace", workspaceKey);
                for (var i = 0; i < seenIds.Count; i++)
                    deleteCmd.Parameters.AddWithValue($"$id{i}", seenIds[i]);
            }
            else
            {
                deleteCmd.CommandText = "DELETE FROM upload_projects WHERE workspace_path = $workspace";
                deleteCmd.Parameters.AddWithValue("$workspace", workspaceKey);
            }
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
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

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

    private static string WorkspaceKey(string workspaceRoot) => Path.GetFullPath(workspaceRoot);

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
