using System.Text.Json;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokExecutionHistoryService
{
    public const int DefaultRetentionDays = 3;
    public const int FailureRetentionDays = 90;
    private const string SnapshotMigrationKey = "upload-history-snapshots-v1";
    private static readonly HashSet<string> OptimizedDatabases = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static string NewBatchId() =>
        $"tiktok-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..31];

    public static void AppendEvent(
        string eventType,
        string status,
        string workspace,
        QueueProjectItem? item = null,
        string stepKey = "",
        string message = "",
        string error = "",
        string batchId = "",
        IReadOnlyDictionary<string, object?>? metadata = null,
        TikTokAccountProfile? account = null)
    {
        try
        {
            var payload = BuildPayload(
                eventType,
                status,
                workspace,
                item,
                stepKey,
                message,
                error,
                batchId,
                metadata,
                account);
            AppendPayload(payload);
        }
        catch
        {
            // History export must not break queue execution.
        }
    }

    public static void PersistDeletionSnapshot(
        string workspace,
        QueueProjectItem item,
        TikTokAccountProfile? account = null,
        string? databasePath = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var payload = BuildPayload(
            "project_deleted",
            "deleted",
            workspace,
            item,
            stepKey: "",
            message: "用户删除项目，已保存版权恢复快照",
            error: "",
            batchId: "",
            metadata: null,
            account: account);
        AppendPayload(payload, databasePath);
    }

    public static IReadOnlyList<Dictionary<string, object?>> LoadEvents(
        int? limit = null,
        string? databasePath = null)
    {
        try
        {
            var path = ResolveDatabasePath(databasePath);
            if (!File.Exists(path)) return [];
            EnsureStorageOptimized(path);

            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = limit is > 0
                ? "SELECT payload_json FROM upload_task_events ORDER BY created_at DESC LIMIT $limit"
                : "SELECT payload_json FROM upload_task_events ORDER BY created_at ASC";
            if (limit is > 0)
                cmd.Parameters.AddWithValue("$limit", limit.Value);

            var events = new List<Dictionary<string, object?>>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var payload = Deserialize(reader.GetString(0));
                if (payload.Count > 0) events.Add(payload);
            }

            if (limit is > 0)
                events.Reverse();
            return events;
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<TikTokExecutionProjectSnapshot> LoadProjectSnapshots(
        string? databasePath = null)
    {
        try
        {
            var path = ResolveDatabasePath(databasePath);
            if (!File.Exists(path)) return [];
            EnsureStorageOptimized(path);

            using var conn = AppDatabaseInitializer.OpenConnection(path, readOnly: true);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT workspace, updated_at, payload_json
                FROM upload_project_snapshots
                ORDER BY updated_at, snapshot_key
                """;
            var snapshots = new List<TikTokExecutionProjectSnapshot>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var payload = Deserialize(reader.GetString(2));
                if (payload.Count == 0) continue;
                var item = QueueProjectItem.FromPayload(payload);
                snapshots.Add(new TikTokExecutionProjectSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    item));
            }

            return snapshots
                .OrderBy(snapshot => FirstNonEmpty(snapshot.Item.QueuedAt, snapshot.Timestamp), StringComparer.Ordinal)
                .ThenBy(snapshot => snapshot.Item.AccountProfileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(snapshot => snapshot.Item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static int PruneOldEvents(
        string? databasePath = null,
        DateTime? now = null,
        int retentionDays = DefaultRetentionDays)
    {
        var path = string.IsNullOrWhiteSpace(databasePath) ? ClientSettingsStore.MainDatabasePath : databasePath;
        try
        {
            EnsureStorageOptimized(path);
            var current = now ?? DateTime.Now;
            var normalCutoff = current.AddDays(-Math.Max(1, retentionDays)).ToString("yyyy-MM-ddTHH:mm:ss");
            var failureCutoff = current.AddDays(-FailureRetentionDays).ToString("yyyy-MM-ddTHH:mm:ss");
            using var conn = AppDatabaseInitializer.OpenConnection(path);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM upload_task_events
                WHERE (created_at < $normal_cutoff AND
                       COALESCE(json_extract(payload_json, '$.error'), '') = '' AND
                       COALESCE(json_extract(payload_json, '$.last_error'), '') = '' AND
                       COALESCE(json_extract(payload_json, '$.status'), '') NOT IN ($failed, $stopped, 'failed', 'stopped') AND
                       COALESCE(json_extract(payload_json, '$.status_text'), '') NOT IN ($failed, $stopped, 'failed', 'stopped'))
                   OR created_at < $failure_cutoff
                """;
            cmd.Parameters.AddWithValue("$normal_cutoff", normalCutoff);
            cmd.Parameters.AddWithValue("$failure_cutoff", failureCutoff);
            cmd.Parameters.AddWithValue("$failed", QueueStepStatus.Failed);
            cmd.Parameters.AddWithValue("$stopped", QueueStepStatus.Stopped);
            return cmd.ExecuteNonQuery();
        }
        catch
        {
            return 0;
        }
    }

    private static Dictionary<string, object?> BuildPayload(
        string eventType,
        string status,
        string workspace,
        QueueProjectItem? item,
        string stepKey,
        string message,
        string error,
        string batchId,
        IReadOnlyDictionary<string, object?>? metadata,
        TikTokAccountProfile? account)
    {
        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var accountId = account?.Id ?? item?.AccountProfileId ?? "";
        var accountName = account?.DisplayName ?? item?.AccountProfileName ?? "";
        var payload = new Dictionary<string, object?>
        {
            ["event_id"] = Guid.NewGuid().ToString("N"),
            ["event_type"] = eventType,
            ["status"] = status,
            ["timestamp"] = now,
            ["workspace"] = workspace,
            ["batch_id"] = batchId,
            ["project_dir"] = item?.ProjectDir ?? "",
            ["display_name"] = item?.DisplayName ?? "",
            ["original_title"] = item?.OriginalTitle ?? "",
            ["new_title"] = item?.NewTitle ?? "",
            ["episode_count"] = item?.EpisodeCount ?? 0,
            ["genre_category"] = item?.GenreCategory ?? "",
            ["description"] = item?.Description ?? "",
            ["queued_at"] = item?.QueuedAt ?? "",
            ["upload_completed_at"] = item?.UploadCompletedAt ?? "",
            ["current_step"] = item?.CurrentStep ?? "",
            ["status_text"] = item?.StatusText ?? status,
            ["last_error"] = item?.LastError ?? error,
            ["archived"] = item?.Archived ?? false,
            ["step_key"] = stepKey,
            ["step_label"] = string.IsNullOrWhiteSpace(stepKey) ? "" : QueueStepRegistry.LabelOf(stepKey),
            ["message"] = message,
            ["error"] = error,
            ["account_profile_id"] = accountId,
            ["account_profile_name"] = accountName,
            ["machine_name"] = Environment.MachineName,
            ["step_states"] = item is null ? new Dictionary<string, string>() : new Dictionary<string, string>(item.StepStates),
        };
        if (metadata is not null)
            payload["metadata"] = new Dictionary<string, object?>(metadata);
        return payload;
    }

    private static void AppendPayload(
        Dictionary<string, object?> payload,
        string? databasePath = null)
    {
        var path = ResolveDatabasePath(databasePath);
        EnsureStorageOptimized(path);
        lock (AppDatabaseInitializer.WriteSyncRoot)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var createdAt = payload.GetValueOrDefault("timestamp")?.ToString() ?? DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            using var conn = AppDatabaseInitializer.OpenConnection(path);
            using var tx = conn.BeginTransaction();
            UpsertSnapshot(conn, tx, payload, createdAt);

            if (!ShouldPersistEvent(payload))
            {
                tx.Commit();
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO upload_task_events(event_id, payload_json, created_at)
                VALUES($event_id, $payload_json, $created_at)
                """;
            cmd.Parameters.AddWithValue("$event_id", payload["event_id"]?.ToString() ?? Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$payload_json", json);
            cmd.Parameters.AddWithValue("$created_at", createdAt);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public static void EnsureStorageOptimized(string? databasePath = null)
    {
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(databasePath)
            ? ClientSettingsStore.MainDatabasePath
            : databasePath);

        lock (AppDatabaseInitializer.WriteSyncRoot)
        {
            if (OptimizedDatabases.Contains(path)) return;
            AppDatabaseInitializer.EnsureInitialized(path);

            using var conn = AppDatabaseInitializer.OpenConnection(path);
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT 1 FROM app_migrations WHERE migration_key = $key LIMIT 1";
                check.Parameters.AddWithValue("$key", SnapshotMigrationKey);
                if (check.ExecuteScalar() is not null)
                {
                    OptimizedDatabases.Add(path);
                    return;
                }
            }

            var latestByKey = new Dictionary<string, (Dictionary<string, object?> Payload, string UpdatedAt)>(StringComparer.OrdinalIgnoreCase);
            using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT payload_json, created_at FROM upload_task_events ORDER BY created_at, rowid";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    var payload = Deserialize(reader.GetString(0));
                    if (!TryBuildProjectSnapshot(payload, out var snapshot)) continue;
                    latestByKey[BuildSnapshotKey(snapshot.Item)] = (snapshot.Item.ToPayload(), reader.GetString(1));
                    latestByKey[BuildSnapshotKey(snapshot.Item)].Payload["workspace"] = snapshot.Workspace;
                }
            }

            using (var tx = conn.BeginTransaction())
            {
                foreach (var (_, value) in latestByKey)
                    UpsertSnapshot(conn, tx, value.Payload, value.UpdatedAt);

                using var delete = conn.CreateCommand();
                delete.Transaction = tx;
                delete.CommandText = """
                    DELETE FROM upload_task_events
                    WHERE json_extract(payload_json, '$.event_type') = 'queue_progress'
                      AND COALESCE(json_extract(payload_json, '$.error'), '') = ''
                      AND COALESCE(json_extract(payload_json, '$.last_error'), '') = ''
                      AND COALESCE(json_extract(payload_json, '$.status'), '') NOT IN ($failed, $stopped, 'failed', 'stopped')
                      AND COALESCE(json_extract(payload_json, '$.status_text'), '') NOT IN ($failed, $stopped, 'failed', 'stopped')
                    """;
                delete.Parameters.AddWithValue("$failed", QueueStepStatus.Failed);
                delete.Parameters.AddWithValue("$stopped", QueueStepStatus.Stopped);
                delete.ExecuteNonQuery();

                using var mark = conn.CreateCommand();
                mark.Transaction = tx;
                mark.CommandText = """
                    INSERT INTO app_migrations(migration_key, completed_at)
                    VALUES($key, $completed_at)
                    """;
                mark.Parameters.AddWithValue("$key", SnapshotMigrationKey);
                mark.Parameters.AddWithValue("$completed_at", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
                mark.ExecuteNonQuery();
                tx.Commit();
            }

            using (var checkpoint = conn.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                checkpoint.ExecuteNonQuery();
            }
            try
            {
                using var vacuum = conn.CreateCommand();
                vacuum.CommandText = "VACUUM";
                vacuum.CommandTimeout = 300;
                vacuum.ExecuteNonQuery();
            }
            catch
            {
                // Compaction needs temporary disk space. The data migration remains valid if it cannot run.
            }
            OptimizedDatabases.Add(path);
        }
    }

    private static void UpsertSnapshot(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyDictionary<string, object?> eventPayload,
        string updatedAt)
    {
        if (!TryBuildProjectSnapshot(eventPayload, out var snapshot)) return;
        var item = snapshot.Item;
        var snapshotPayload = JsonSerializer.Serialize(item.ToPayload(), JsonOptions);
        var snapshotKey = BuildSnapshotKey(item);
        if (snapshotKey == "|") return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO upload_project_snapshots(
                snapshot_key, account_profile_id, workspace, project_dir, payload_json, updated_at)
            VALUES($snapshot_key, $account_profile_id, $workspace, $project_dir, $payload_json, $updated_at)
            ON CONFLICT(snapshot_key) DO UPDATE SET
                account_profile_id = excluded.account_profile_id,
                workspace = excluded.workspace,
                project_dir = excluded.project_dir,
                payload_json = excluded.payload_json,
                updated_at = excluded.updated_at
            WHERE upload_project_snapshots.payload_json <> excluded.payload_json
               OR upload_project_snapshots.workspace <> excluded.workspace
            """;
        cmd.Parameters.AddWithValue("$snapshot_key", snapshotKey);
        cmd.Parameters.AddWithValue("$account_profile_id", item.AccountProfileId ?? "");
        cmd.Parameters.AddWithValue("$workspace", GetString(eventPayload, "workspace"));
        cmd.Parameters.AddWithValue("$project_dir", item.ProjectDir ?? "");
        cmd.Parameters.AddWithValue("$payload_json", snapshotPayload);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);
        cmd.ExecuteNonQuery();
    }

    private static bool ShouldPersistEvent(IReadOnlyDictionary<string, object?> payload)
    {
        if (!string.Equals(GetString(payload, "event_type"), "queue_progress", StringComparison.Ordinal))
            return true;
        var status = FirstNonEmpty(GetString(payload, "status"), GetString(payload, "status_text"));
        return !string.IsNullOrWhiteSpace(GetString(payload, "error")) ||
               !string.IsNullOrWhiteSpace(GetString(payload, "last_error")) ||
               string.Equals(status, QueueStepStatus.Failed, StringComparison.Ordinal) ||
               string.Equals(status, QueueStepStatus.Stopped, StringComparison.Ordinal) ||
               string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBuildProjectSnapshot(
        IReadOnlyDictionary<string, object?> payload,
        out TikTokExecutionProjectSnapshot snapshot)
    {
        snapshot = default!;
        var itemPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["project_dir"] = GetString(payload, "project_dir"),
            ["display_name"] = GetString(payload, "display_name"),
            ["original_title"] = GetString(payload, "original_title"),
            ["new_title"] = GetString(payload, "new_title"),
            ["episode_count"] = payload.TryGetValue("episode_count", out var episodeCount) ? episodeCount : 0,
            ["genre_category"] = GetString(payload, "genre_category"),
            ["description"] = GetString(payload, "description"),
            ["account_profile_id"] = GetString(payload, "account_profile_id"),
            ["account_profile_name"] = GetString(payload, "account_profile_name"),
            ["queued_at"] = GetString(payload, "queued_at"),
            ["upload_completed_at"] = GetString(payload, "upload_completed_at"),
            ["current_step"] = GetString(payload, "current_step"),
            ["status_text"] = FirstNonEmpty(GetString(payload, "status_text"), GetString(payload, "status")),
            ["last_error"] = FirstNonEmpty(GetString(payload, "last_error"), GetString(payload, "error")),
            ["archived"] = payload.TryGetValue("archived", out var archived) && archived is bool archivedBool && archivedBool,
            ["step_states"] = payload.TryGetValue("step_states", out var stepStates)
                ? stepStates ?? new Dictionary<string, string>()
                : new Dictionary<string, string>(),
        };

        var item = QueueProjectItem.FromPayload(itemPayload);
        if (string.IsNullOrWhiteSpace(item.ProjectDir) &&
            string.IsNullOrWhiteSpace(item.OriginalTitle) &&
            string.IsNullOrWhiteSpace(item.NewTitle) &&
            string.IsNullOrWhiteSpace(item.DisplayName))
        {
            return false;
        }

        var stepKey = GetString(payload, "step_key");
        var status = GetString(payload, "status");
        var timestamp = GetString(payload, "timestamp");
        if (string.Equals(stepKey, QueueStepKeys.UploadSeries, StringComparison.Ordinal) &&
            string.Equals(status, QueueStepStatus.Completed, StringComparison.Ordinal))
        {
            item.StepStates[QueueStepKeys.UploadSeries] = QueueStepStatus.Completed;
            item.StatusText = QueueStepStatus.Completed;
            if (string.IsNullOrWhiteSpace(item.UploadCompletedAt) &&
                !IsCopyrightProofOnlyEvent(payload))
                item.UploadCompletedAt = timestamp;
        }

        item.NormalizeStepStates();
        snapshot = new TikTokExecutionProjectSnapshot(
            GetString(payload, "workspace"),
            timestamp,
            item);
        return true;
    }

    private static bool IsCopyrightProofOnlyEvent(IReadOnlyDictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("metadata", out var rawMetadata) ||
            rawMetadata is not IReadOnlyDictionary<string, object?> metadata)
        {
            return false;
        }

        var entryMode = GetString(metadata, "upload_entry_mode");
        return string.Equals(
                   entryMode,
                   QueueRunOptions.CopyrightProofOnlyEntryMode,
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   entryMode,
                   QueueRunOptions.AiOutlineSupplementEntryMode,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> Deserialize(string json)
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
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in element.EnumerateObject())
            result[prop.Name] = JsonElementToObject(prop.Value);
        return result;
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

    private static string BuildSnapshotKey(QueueProjectItem item)
    {
        var account = FirstNonEmpty(item.AccountProfileId, item.AccountProfileName);
        var project = FirstNonEmpty(
            NormalizeProjectKey(item.ProjectDir),
            item.OriginalTitle,
            item.NewTitle,
            item.DisplayName);
        return $"{account}|{project}";
    }

    private static string NormalizeProjectKey(string? value)
    {
        var text = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return "";
        try { return Path.GetFullPath(text).Replace('\\', '/').ToLowerInvariant(); }
        catch { return text.Replace('\\', '/').ToLowerInvariant(); }
    }

    private static string GetString(IReadOnlyDictionary<string, object?> payload, string key) =>
        payload.TryGetValue(key, out var value) ? (value?.ToString() ?? "").Trim() : "";

    private static string ResolveDatabasePath(string? databasePath) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(databasePath)
            ? ClientSettingsStore.MainDatabasePath
            : databasePath);

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return "";
    }
}

public sealed record TikTokExecutionProjectSnapshot(
    string Workspace,
    string Timestamp,
    QueueProjectItem Item);
