using System.Text.Json;

namespace LicenseTranslate.App;

public sealed class TranslateAppSettings
{
    public string AuthServerUrl { get; set; } = "";
    public string LlmBaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string LlmModel { get; set; } = "deepseek-chat";
    public string LlmApiKey { get; set; } = "";

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "license-translate.json");

    public static TranslateAppSettings Load()
    {
        if (!File.Exists(FilePath))
            return new TranslateAppSettings();
        try
        {
            return JsonSerializer.Deserialize<TranslateAppSettings>(File.ReadAllText(FilePath)) ?? new TranslateAppSettings();
        }
        catch (JsonException)
        {
            return new TranslateAppSettings();
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}
