using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TikTokPublisher.Core.Services;

/// <summary>读写 Python <c>tiktok_uploader.db</c> 的 <c>app_settings</c> 表。</summary>
public static class PythonAppSettingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static bool TryLoadJson<T>(string key, out T? value, string? databasePath = null)
    {
        value = default;
        var path = ResolvePath(databasePath);
        if (!File.Exists(path)) return false;

        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM app_settings WHERE key = $key LIMIT 1";
        cmd.Parameters.AddWithValue("$key", key);
        var json = cmd.ExecuteScalar()?.ToString();
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return value is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void SaveJson(string key, object value, string? databasePath = null)
    {
        var path = ResolvePath(databasePath);
        PythonDatabaseInitializer.EnsureInitialized(path);
        var now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var json = JsonSerializer.Serialize(value, JsonOptions);

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_settings (key, value_json, updated_at)
            VALUES ($key, $json, $now)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    private static string ResolvePath(string? databasePath) =>
        string.IsNullOrWhiteSpace(databasePath) ? AppPaths.PythonDatabaseFile : Path.GetFullPath(databasePath);
}
