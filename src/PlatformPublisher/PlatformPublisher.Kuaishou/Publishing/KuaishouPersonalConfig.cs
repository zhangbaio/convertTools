using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouPersonalConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    public string EntryUrl { get; set; } = "https://kdj.kuaishou.com/home/content/content-management";
    public string AuthStatePath { get; set; } = string.Empty;
    public string BrowserProfileDirectory { get; set; } = string.Empty;
    public bool Headless { get; set; }
    public bool KeepBrowserOpenOnFailure { get; set; } = true;
    public string CommitmentPdfPath { get; set; } = string.Empty;
    public string RealName { get; set; } = string.Empty;
    public string Gender { get; set; } = "男";
    public string KuaishouNickname { get; set; } = string.Empty;
    public string KuaishouId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ContentType { get; set; } = "漫剧";
    public string ProductionMethod { get; set; } = "AIGC剧";
    public string ProductionForm { get; set; } = "竖屏";
    public string ProductionYear { get; set; } = DateTime.Now.Year.ToString();
    public string ProductionCost { get; set; } = "10";
    public string AverageEpisodeMinutes { get; set; } = "2";
    public bool Finished { get; set; } = true;
    public bool HasRecordNumber { get; set; }
    public string BroadcastPlatform { get; set; } = "快手";
    public string BroadcastChannel { get; set; } = "小屏小程序";
    public string BroadcastDate { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public string SaleType { get; set; } = "观看广告解锁";
    public int FreeEpisodeCount { get; set; } = 3;
    public int UnlockEpisodeCount { get; set; } = 1;
    public string EpisodePrice { get; set; } = "1";
    public string Actors { get; set; } = "演员A:男:男主;演员B:女:女主";
    public string Directors { get; set; } = string.Empty;
    public string Screenwriters { get; set; } = string.Empty;
    public string ProductionOrganization { get; set; } = string.Empty;
    public string AudienceGender { get; set; } = "男频";
    public string PlotLabels { get; set; } = string.Empty;
    public string TagLabels { get; set; } = string.Empty;
    public string FirstPageAction { get; set; } = "draft";
    public string FinalAction { get; set; } = "keep";
    public int UploadTimeoutMinutes { get; set; } = 60;
    public bool ForceRerun { get; set; }
    public string RunMode { get; set; } = "auto";

    public static KuaishouPersonalConfig Load(PublishJob job)
    {
        var accountKey = string.IsNullOrWhiteSpace(job.AccountId) ? "default" : Safe(job.AccountId);
        var accountRoot = Path.Combine(PlatformPublisherPaths.DataRoot, "kuaishou-personal", "accounts", accountKey);
        Directory.CreateDirectory(accountRoot);
        var configuredPath = !string.IsNullOrWhiteSpace(job.ConfigPath) && File.Exists(job.ConfigPath)
            ? Path.GetFullPath(job.ConfigPath)
            : DefaultConfigPath(job.AccountId);
        KuaishouPersonalConfig config;
        if (File.Exists(configuredPath))
        {
            try
            {
                config = JsonSerializer.Deserialize<KuaishouPersonalConfig>(
                             File.ReadAllText(configuredPath),
                             JsonOptions)
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
        if (!string.IsNullOrWhiteSpace(config.CommitmentPdfPath))
            config.CommitmentPdfPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(config.CommitmentPdfPath));
        Directory.CreateDirectory(Path.GetDirectoryName(config.AuthStatePath)!);
        Directory.CreateDirectory(config.BrowserProfileDirectory);
        return config;
    }

    public static string DefaultConfigPath(string? accountId)
    {
        var accountKey = string.IsNullOrWhiteSpace(accountId) ? "default" : Safe(accountId);
        return Path.Combine(
            PlatformPublisherPaths.DataRoot,
            "kuaishou-personal",
            "accounts",
            accountKey,
            "kuaishou-personal-config.json");
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, this, JsonOptions, cancellationToken);
        File.Move(temporaryPath, fullPath, true);
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
