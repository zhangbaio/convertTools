using System.Text.Json;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokExecutionHistoryService
{
    public const int DefaultRetentionDays = 3;

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

    public static IReadOnlyList<Dictionary<string, object?>> LoadEvents(int? limit = null)
    {
        try
        {
            var path = ClientSettingsStore.MainDatabasePath;
            if (!File.Exists(path)) return [];
            AppDatabaseInitializer.EnsureInitialized(path);

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

    public static IReadOnlyList<TikTokExecutionProjectSnapshot> LoadProjectSnapshots()
    {
        var latestByKey = new Dictionary<string, TikTokExecutionProjectSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var payload in LoadEvents())
        {
            if (!TryBuildProjectSnapshot(payload, out var snapshot))
                continue;

            latestByKey[BuildSnapshotKey(snapshot.Item)] = snapshot;
        }

        return latestByKey.Values
            .OrderBy(snapshot => FirstNonEmpty(snapshot.Item.QueuedAt, snapshot.Timestamp), StringComparer.Ordinal)
            .ThenBy(snapshot => snapshot.Item.AccountProfileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(snapshot => snapshot.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static int PruneOldEvents(
        string? databasePath = null,
        DateTime? now = null,
        int retentionDays = DefaultRetentionDays)
        => 0;

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

    private static void AppendPayload(Dictionary<string, object?> payload)
    {
        var path = ClientSettingsStore.MainDatabasePath;
        AppDatabaseInitializer.EnsureInitialized(path);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var createdAt = payload.GetValueOrDefault("timestamp")?.ToString() ?? DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO upload_task_events(event_id, payload_json, created_at)
            VALUES($event_id, $payload_json, $created_at)
            """;
        cmd.Parameters.AddWithValue("$event_id", payload["event_id"]?.ToString() ?? Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$payload_json", json);
        cmd.Parameters.AddWithValue("$created_at", createdAt);
        cmd.ExecuteNonQuery();
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
            if (string.IsNullOrWhiteSpace(item.UploadCompletedAt))
                item.UploadCompletedAt = timestamp;
        }

        item.NormalizeStepStates();
        snapshot = new TikTokExecutionProjectSnapshot(
            GetString(payload, "workspace"),
            timestamp,
            item);
        return true;
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
