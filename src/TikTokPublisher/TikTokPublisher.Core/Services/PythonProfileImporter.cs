using System.Text.Json;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

/// <summary>从 Python 客户端 SQLite（<c>tiktok_uploader.db</c>）导入账号档案。</summary>
public static class PythonProfileImporter
{
    private const string ActiveProfileKey = "active_tiktok_account_profile_id";

    public sealed class ImportBundle
    {
        public List<TikTokAccountProfile> Profiles { get; init; } = new();
        public string ActiveProfileId { get; init; } = "";
        public string SourceDescription { get; init; } = "";
    }

    public static ImportBundle Load(string? databasePath = null)
    {
        var path = string.IsNullOrWhiteSpace(databasePath)
            ? AppPaths.PythonDatabaseFile
            : Path.GetFullPath(databasePath);

        if (!File.Exists(path))
            throw new FileNotFoundException($"未找到 Python 客户端数据库：{path}");

        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();

        var profiles = LoadFromProfilesTable(conn);
        var activeId = LoadActiveProfileId(conn);

        if (profiles.Count == 0)
        {
            var legacy = LoadFromLegacyClientSettings(conn);
            profiles = legacy.Profiles;
            if (string.IsNullOrWhiteSpace(activeId))
                activeId = legacy.ActiveProfileId;
        }

        if (profiles.Count == 0)
            throw new InvalidOperationException("Python 数据库中未找到任何 TikTok 账号档案。");

        if (string.IsNullOrWhiteSpace(activeId))
            activeId = profiles[0].Id;

        return new ImportBundle
        {
            Profiles = profiles,
            ActiveProfileId = activeId,
            SourceDescription = path,
        };
    }

    private static List<TikTokAccountProfile> LoadFromProfilesTable(SqliteConnection conn)
    {
        var results = new List<TikTokAccountProfile>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                p.profile_id,
                p.name,
                p.payload_json,
                a.auth_state_path,
                a.last_login_email,
                a.last_login_at
            FROM tiktok_account_profiles p
            LEFT JOIN account_auth_states a ON a.profile_id = p.profile_id
            ORDER BY p.display_order ASC, p.profile_id ASC
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var profileId = reader.GetString(0).Trim();
            if (string.IsNullOrEmpty(profileId)) continue;
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var payloadJson = reader.IsDBNull(2) ? "{}" : reader.GetString(2);
            var authPath = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var loginEmail = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var loginAt = reader.IsDBNull(5) ? "" : reader.GetString(5);

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            results.Add(TikTokAccountProfileMapper.FromPythonPayload(
                doc.RootElement,
                profileId,
                displayName: name,
                authStatePath: authPath,
                lastLoginEmail: loginEmail,
                lastLoginAt: loginAt));
        }
        return results;
    }

    private static string LoadActiveProfileId(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_state WHERE key = $key LIMIT 1";
        cmd.Parameters.AddWithValue("$key", ActiveProfileKey);
        var value = cmd.ExecuteScalar();
        return value?.ToString()?.Trim() ?? "";
    }

    private sealed record LegacyImportResult(List<TikTokAccountProfile> Profiles, string ActiveProfileId);

    private static LegacyImportResult LoadFromLegacyClientSettings(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM app_settings WHERE key = 'client_settings' LIMIT 1";
        var json = cmd.ExecuteScalar()?.ToString();
        if (string.IsNullOrWhiteSpace(json)) return new LegacyImportResult(new List<TikTokAccountProfile>(), "");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tiktok_account_profiles_json", out var profilesNode))
            return new LegacyImportResult(new List<TikTokAccountProfile>(), "");

        List<JsonElement> items;
        if (profilesNode.ValueKind == JsonValueKind.String)
        {
            var inner = profilesNode.GetString();
            if (string.IsNullOrWhiteSpace(inner)) return new LegacyImportResult(new List<TikTokAccountProfile>(), "");
            using var innerDoc = JsonDocument.Parse(inner);
            items = innerDoc.RootElement.ValueKind == JsonValueKind.Array
                ? innerDoc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList()
                : new List<JsonElement>();
        }
        else if (profilesNode.ValueKind == JsonValueKind.Array)
        {
            items = profilesNode.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        else return new LegacyImportResult(new List<TikTokAccountProfile>(), "");

        var activeId = root.TryGetProperty("active_tiktok_account_profile_id", out var activeNode)
            ? activeNode.GetString()?.Trim() ?? ""
            : "";

        var results = new List<TikTokAccountProfile>();
        foreach (var item in items)
        {
            var profileId = item.TryGetProperty("id", out var idNode) ? idNode.GetString()?.Trim() ?? "" : "";
            if (string.IsNullOrEmpty(profileId)) continue;
            var name = item.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : profileId;
            results.Add(TikTokAccountProfileMapper.FromPythonPayload(item, profileId, displayName: name));
        }

        return new LegacyImportResult(results, activeId);
    }
}
