using System.Text.Json.Serialization;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Models;

/// <summary>
/// TikTok 发布账号档案。字段对齐 Python <c>account_profiles.py</c> 的核心子集；
/// 每个账号独立 WebView2 会话目录 + 可选 Playwright storage_state 文件。
/// </summary>
public sealed class TikTokAccountProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    // 登录 / 会话
    public string TiktokAccountNickname { get; set; } = "";
    public string TiktokLoginEmail { get; set; } = "";
    public string TiktokLoginPassword { get; set; } = "";
    public string TiktokLastLoginEmail { get; set; } = "";
    public string TiktokLastLoginAt { get; set; } = "";
    public string TiktokStorageStatePath { get; set; } = "";
    public string TiktokLoginBrowserMode { get; set; } = "embedded"; // embedded | cdp
    public string TiktokFingerprintBrowserCdpEndpoint { get; set; } = "";
    public string TiktokSeriesUrl { get; set; } = TikTokUrls.DefaultSeriesDraftUrl;

    // 工作目录（与 Python last_workspace / tiktok_upload_profile_path 对齐）
    public string LastWorkspace { get; set; } = "";
    public string LastDownloadWorkspace { get; set; } = "";
    public string TiktokUploadProfilePath { get; set; } = "";

    // 代理
    public bool TiktokProxyEnabled { get; set; }
    public string TiktokProxyType { get; set; } = "http";
    public string TiktokProxyHost { get; set; } = "";
    public int TiktokProxyPort { get; set; }
    public string TiktokProxyUsername { get; set; } = "";
    public string TiktokProxyPassword { get; set; } = "";
    public string TiktokProxyLabel { get; set; } = "";
    public string TiktokStaticIpNote { get; set; } = "";
    public string TiktokFingerprintStartCommand { get; set; } = "";

    // 发布默认
    public bool TiktokSubmitEnabled { get; set; } = true;
    public string TiktokSubmitAction { get; set; } = "submit"; // none | submit | save
    /// <summary>上传剧集使用的浏览器：embedded=内置 WebView2；external=外部浏览器（经 CDP 端点接入）；playwright=程序用 Playwright 独立启动的浏览器。</summary>
    public string TiktokUploadBrowserMode { get; set; } = "external"; // embedded | external | playwright
    /// <summary>playwright 模式：独立浏览器是否无头运行（false=有头可见窗口）。</summary>
    public bool TiktokPlaywrightUploadHeadless { get; set; }
    public string TiktokContractId { get; set; } = "";
    public string TiktokContractIdMode { get; set; } = "manual";
    public bool TiktokPaidEnabled { get; set; }
    public bool TiktokPaidRatioEnabled { get; set; }
    public double TiktokPaidRatioPercent { get; set; } = 20.0;
    public int TiktokProjectConcurrency { get; set; } = 4;

    // 发布表单扩展（对齐 Python account_profiles 字段）
    public bool TiktokAnchorPromotionEnabled { get; set; } = true;
    public string TiktokTargetAudienceMode { get; set; } = "ai_recommend";
    public int TiktokGenreCount { get; set; } = 3;
    public string TiktokSourceLanguage { get; set; } = "zh";
    public bool TiktokIsAiDrama { get; set; } = true;
    public string TiktokPublishMode { get; set; } = "auto_after_review";
    public bool TiktokConsignmentEnabled { get; set; } = true;
    public int TiktokProfilePreviewEpisodes { get; set; } = 3;
    public int TiktokFreePreviewEpisodes { get; set; } = 3;
    public string TiktokExpectedFullPriceMode { get; set; } = "manual";
    public int TiktokExpectedFullPriceOptionIndex { get; set; } = 1;
    public string TiktokExpectedFullPriceValue { get; set; } = "";
    public string TiktokExpectedFullPriceLabel { get; set; } = "";
    public string TiktokExpectedFullPriceOptionsJson { get; set; } = "";
    public int TiktokUploadStallSeconds { get; set; } = 180;
    public string TiktokUploadStrategy { get; set; } = "classic";
    public int TiktokUploadBatchSize { get; set; } = 3;
    public int TiktokUploadBatchStallSeconds { get; set; } = 75;
    public int TiktokUploadBatchMaxRetries { get; set; } = 3;
    public bool TiktokSilenceValidationEnabled { get; set; } = true;
    public int TiktokMaxContinuousSilenceSeconds { get; set; } = 20;
    public double TiktokSilenceThresholdDb { get; set; } = -45.0;
    public string TiktokExcelReportPath { get; set; } = "";
    public List<string>? TiktokQueueEnabledSteps { get; set; }
    public bool ManagementDedupEnabled { get; set; }
    public string ManagementDedupScope { get; set; } = "tiktok_username";

    /// <summary>WebView2 UserDataFolder（每账号独立浏览器会话）。</summary>
    public string ProfileDir { get; set; } = "";

    /// <summary>运行期状态，不持久化。</summary>
    [JsonIgnore]
    public AccountStatus Status { get; set; } = AccountStatus.Offline;

    public string DisplayName =>
        FirstNonEmpty(TiktokAccountNickname, ResolveTikTokAccountName(), Name, Id);

    public string ResolveTikTokAccountName() =>
        FirstNonEmpty(TiktokLoginEmail, TiktokLastLoginEmail);

    public string ResolveWorkspacePath()
    {
        foreach (var candidate in new[] { TiktokUploadProfilePath, LastWorkspace })
        {
            var path = (candidate ?? "").Trim();
            if (string.IsNullOrEmpty(path)) continue;
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
                if (Directory.Exists(full)) return full;
            }
            catch
            {
                // 忽略非法路径
            }
        }
        return "";
    }

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

public static class TikTokUrls
{
    public const string DefaultLoginUrl = "https://www.tiktokdramacenter.com/login";
    public const string DefaultSeriesDraftUrl = "https://www.tiktokdramacenter.com/series/draft";
    public const string DefaultSeriesListUrl = "https://www.tiktokdramacenter.com/series/list";
}
