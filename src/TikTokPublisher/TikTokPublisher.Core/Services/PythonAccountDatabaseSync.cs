using System.Text.Json;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

/// <summary>读写 Python <c>tiktok_uploader.db</c> 中的账号档案（双向同步）。</summary>
public static class PythonAccountDatabaseSync
{
    private const string ActiveProfileKey = "active_tiktok_account_profile_id";

    public sealed class SyncResult
    {
        public int Imported { get; init; }
        public int Updated { get; init; }
        public int Exported { get; init; }
        public string ActiveProfileId { get; init; } = "";
        public string Message { get; init; } = "";
    }

    public static bool DatabaseExists(string? databasePath = null) =>
        File.Exists(ResolvePath(databasePath));

    public static SyncResult ExportProfiles(
        IReadOnlyList<TikTokAccountProfile> profiles,
        string activeProfileId,
        string? databasePath = null)
    {
        var path = ResolvePath(databasePath);
        PythonDatabaseInitializer.EnsureInitialized(path);

        var now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var activeId = profiles.Any(p => p.Id == activeProfileId)
            ? activeProfileId
            : profiles.FirstOrDefault()?.Id ?? "";

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var tx = conn.BeginTransaction();

        var existingCreatedAt = LoadExistingCreatedAt(conn, tx);
        var seenIds = new List<string>();

        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            if (string.IsNullOrWhiteSpace(profile.Id)) continue;

            var payload = LoadExistingPayload(conn, tx, profile.Id)
                          ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            TikTokAccountProfileMapper.MergeIntoPythonPayload(payload, profile);

            var profileId = profile.Id.Trim();
            seenIds.Add(profileId);
            var name = string.IsNullOrWhiteSpace(profile.Name) ? profileId : profile.Name.Trim();
            var createdAt = !string.IsNullOrWhiteSpace(profile.CreatedAt)
                ? profile.CreatedAt
                : existingCreatedAt.GetValueOrDefault(profileId, now);
            var updatedAt = !string.IsNullOrWhiteSpace(profile.UpdatedAt) ? profile.UpdatedAt : now;

            UpsertProfile(conn, tx, profileId, name, index, payload, createdAt, updatedAt, now);
        }

        if (seenIds.Count > 0)
        {
            var placeholders = string.Join(",", seenIds.Select((_, i) => $"$id{i}"));
            using var deleteCmd = conn.CreateCommand();
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = $"DELETE FROM tiktok_account_profiles WHERE profile_id NOT IN ({placeholders})";
            for (var i = 0; i < seenIds.Count; i++)
                deleteCmd.Parameters.AddWithValue($"$id{i}", seenIds[i]);
            deleteCmd.ExecuteNonQuery();
        }
        else
        {
            using var deleteAll = conn.CreateCommand();
            deleteAll.Transaction = tx;
            deleteAll.CommandText = "DELETE FROM tiktok_account_profiles";
            deleteAll.ExecuteNonQuery();
        }

        SetAppState(conn, tx, ActiveProfileKey, activeId, now);
        tx.Commit();

        return new SyncResult
        {
            Exported = profiles.Count,
            ActiveProfileId = activeId,
            Message = $"已写入 Python 数据库 {profiles.Count} 个账号",
        };
    }

    private static string ResolvePath(string? databasePath) =>
        string.IsNullOrWhiteSpace(databasePath)
            ? AppPaths.PythonDatabaseFile
            : Path.GetFullPath(databasePath);

    private static Dictionary<string, string> LoadExistingCreatedAt(SqliteConnection conn, SqliteTransaction tx)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT profile_id, created_at FROM tiktok_account_profiles";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            map[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        return map;
    }

    private static Dictionary<string, object?>? LoadExistingPayload(
        SqliteConnection conn,
        SqliteTransaction tx,
        string profileId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT payload_json FROM tiktok_account_profiles WHERE profile_id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", profileId);
        var json = cmd.ExecuteScalar()?.ToString();
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return JsonObjectToDictionary(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static void UpsertProfile(
        SqliteConnection conn,
        SqliteTransaction tx,
        string profileId,
        string name,
        int displayOrder,
        Dictionary<string, object?> payload,
        string createdAt,
        string updatedAt,
        string now)
    {
        payload["id"] = profileId;
        payload["name"] = name;
        payload["created_at"] = createdAt;
        payload["updated_at"] = updatedAt;

        var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        using var profileCmd = conn.CreateCommand();
        profileCmd.Transaction = tx;
        profileCmd.CommandText = """
            INSERT INTO tiktok_account_profiles (
                profile_id, name, display_order, payload_json, created_at, updated_at
            )
            VALUES ($id, $name, $order, $payload, $created, $updated)
            ON CONFLICT(profile_id) DO UPDATE SET
                name = excluded.name,
                display_order = excluded.display_order,
                payload_json = excluded.payload_json,
                updated_at = excluded.updated_at
            """;
        profileCmd.Parameters.AddWithValue("$id", profileId);
        profileCmd.Parameters.AddWithValue("$name", name);
        profileCmd.Parameters.AddWithValue("$order", displayOrder);
        profileCmd.Parameters.AddWithValue("$payload", payloadJson);
        profileCmd.Parameters.AddWithValue("$created", createdAt);
        profileCmd.Parameters.AddWithValue("$updated", updatedAt);
        profileCmd.ExecuteNonQuery();

        var storagePath = payload.GetValueOrDefault("tiktok_storage_state_path")?.ToString() ?? "";
        var loginEmail = payload.GetValueOrDefault("tiktok_last_login_email")?.ToString() ?? "";
        var loginAt = payload.GetValueOrDefault("tiktok_last_login_at")?.ToString() ?? "";

        using var authCmd = conn.CreateCommand();
        authCmd.Transaction = tx;
        authCmd.CommandText = """
            INSERT INTO account_auth_states (
                profile_id, auth_state_path, last_login_email, last_login_at, auth_fingerprint, updated_at
            )
            VALUES ($id, $auth, $email, $at, '', $now)
            ON CONFLICT(profile_id) DO UPDATE SET
                auth_state_path = excluded.auth_state_path,
                last_login_email = excluded.last_login_email,
                last_login_at = excluded.last_login_at,
                updated_at = excluded.updated_at
            """;
        authCmd.Parameters.AddWithValue("$id", profileId);
        authCmd.Parameters.AddWithValue("$auth", storagePath);
        authCmd.Parameters.AddWithValue("$email", loginEmail);
        authCmd.Parameters.AddWithValue("$at", loginAt);
        authCmd.Parameters.AddWithValue("$now", now);
        authCmd.ExecuteNonQuery();
    }

    private static void SetAppState(SqliteConnection conn, SqliteTransaction tx, string key, string value, string now)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO app_state (key, value, updated_at)
            VALUES ($key, $value, $now)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    private static Dictionary<string, object?> JsonObjectToDictionary(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
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
        JsonValueKind.Object => JsonObjectToDictionary(element),
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
        _ => null,
    };
}
