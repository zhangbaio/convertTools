using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace LicenseTranslate.App;

public sealed class TranslateAppSettings
{
    public string AuthServerUrl { get; set; } = "";
    public string LlmBaseUrl { get; set; } = "https://ark.cn-beijing.volces.com/api/v3";
    public string LlmModel { get; set; } = "doubao-seed-2-1-lite-260915";
    public string LlmApiKey { get; set; } = "";

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "license-translate.json");

    public static TranslateAppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            var created = new TranslateAppSettings();
            UseVolcanoWhenUnconfigured(created);
            return created;
        }
        try
        {
            var settings = JsonSerializer.Deserialize<TranslateAppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new TranslateAppSettings();
            UseVolcanoWhenUnconfigured(settings);
            return settings;
        }
        catch (JsonException)
        {
            var settings = new TranslateAppSettings();
            UseVolcanoWhenUnconfigured(settings);
            return settings;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static void UseVolcanoWhenUnconfigured(TranslateAppSettings settings)
    {
        if (settings.LlmModel is "" or "doubao-seed-2-0-lite-260215")
            settings.LlmModel = "doubao-seed-2-1-lite-260915";
        if (!string.IsNullOrWhiteSpace(settings.LlmApiKey))
            return;
        var database = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YunfanPlatformPublisher",
            "app.db");
        if (!File.Exists(database))
            return;
        try
        {
            using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value_json FROM app_settings WHERE key = 'client_settings' LIMIT 1";
            var node = JsonNode.Parse(command.ExecuteScalar()?.ToString() ?? "")?.AsObject();
            if (node is null)
                return;
            var key = node["ai_text_api_key"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(key))
                return;
            settings.LlmApiKey = key;
            var endpoint = node["ai_text_endpoint"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(endpoint))
                settings.LlmBaseUrl = endpoint;
        }
        catch (SqliteException)
        {
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}
