using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouPersonalConfig
{
    public string EntryUrl { get; set; } = "https://kdj.kuaishou.com/home/content/content-management";
    public string AuthStatePath { get; set; } = string.Empty;
    public string BrowserProfileDirectory { get; set; } = string.Empty;
    public bool Headless { get; set; }
    public bool KeepBrowserOpenOnFailure { get; set; } = true;

    public static KuaishouPersonalConfig Load(PublishJob job)
    {
        var accountKey = string.IsNullOrWhiteSpace(job.AccountId) ? "default" : Safe(job.AccountId);
        var accountRoot = Path.Combine(PlatformPublisherPaths.DataRoot, "kuaishou-personal", "accounts", accountKey);
        Directory.CreateDirectory(accountRoot);
        KuaishouPersonalConfig config;
        if (!string.IsNullOrWhiteSpace(job.ConfigPath) && File.Exists(job.ConfigPath))
        {
            try
            {
                config = JsonSerializer.Deserialize<KuaishouPersonalConfig>(
                             File.ReadAllText(job.ConfigPath),
                             new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                         ?? new KuaishouPersonalConfig();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"快手分账个人版配置文件格式错误：{ex.Message}", ex);
            }
        }
        else config = new KuaishouPersonalConfig();

        config.EntryUrl = string.IsNullOrWhiteSpace(config.EntryUrl)
            ? "https://kdj.kuaishou.com/home/content/content-management"
            : config.EntryUrl.Trim();
        config.AuthStatePath = Resolve(config.AuthStatePath, accountRoot, "kuaishou_personal_kdj_auth_state.json");
        config.BrowserProfileDirectory = Resolve(config.BrowserProfileDirectory, accountRoot, "browser-profile");
        Directory.CreateDirectory(Path.GetDirectoryName(config.AuthStatePath)!);
        Directory.CreateDirectory(config.BrowserProfileDirectory);
        return config;
    }

    private static string Resolve(string value, string root, string fallback) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(value)
            ? Path.Combine(root, fallback)
            : Path.IsPathRooted(value) ? value : Path.Combine(root, value));

    private static string Safe(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) value = value.Replace(ch, '_');
        return value;
    }
}
