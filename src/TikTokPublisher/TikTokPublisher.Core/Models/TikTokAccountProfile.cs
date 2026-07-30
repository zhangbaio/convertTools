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
    public string TiktokExternalBrowserCdpEndpoint { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TiktokFingerprintBrowserCdpEndpoint
    {
        get => null;
        set => SetExternalBrowserCdpEndpointIfMissing(value);
    }

    [JsonPropertyName("tiktok_fingerprint_browser_cdp_endpoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TiktokLegacyExternalBrowserCdpEndpoint
    {
        get => null;
        set => SetExternalBrowserCdpEndpointIfMissing(value);
    }
    public string TiktokSeriesUrl { get; set; } = TikTokUrls.DefaultSeriesDraftUrl;

    // 工作目录（与 Python last_workspace / tiktok_upload_profile_path 对齐）
    public string LastWorkspace { get; set; } = "";
    public string LastDownloadWorkspace { get; set; } = "";
    public string TiktokUploadProfilePath { get; set; } = "";
    /// <summary>账号独立归档根目录；留空时默认使用上传工作目录下的 archive。</summary>
    public string TiktokArchiveRootDir { get; set; } = "";
    /// <summary>旧全局归档目录是否已迁移为账号级配置。</summary>
    public bool TiktokArchiveRootConfigMigrated { get; set; }

    // 代理
    public bool TiktokProxyEnabled { get; set; }
    public string TiktokProxyType { get; set; } = "http";
    public string TiktokProxyHost { get; set; } = "";
    public int TiktokProxyPort { get; set; }
    public string TiktokProxyUsername { get; set; } = "";
    public string TiktokProxyPassword { get; set; } = "";
    public string TiktokProxyLabel { get; set; } = "";
    public string TiktokStaticIpNote { get; set; } = "";

    // 发布默认
    public bool TiktokSubmitEnabled { get; set; } = true;
    public string TiktokSubmitAction { get; set; } = "submit"; // none | submit | save
    /// <summary>上传剧集使用的浏览器：embedded=内置 WebView2；playwright=程序自动启动的外部浏览器。</summary>
    public string TiktokUploadBrowserMode { get; set; } = "embedded"; // embedded | playwright
    /// <summary>playwright 模式：程序自动启动的外部浏览器是否无头运行（false=有头可见窗口）。</summary>
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
    public bool TiktokIsOriginalRightsHolder { get; set; } = true;
    public string TiktokContentOriginalityType { get; set; } = "original";
    public List<string> TiktokCopyrightMaterialTypes { get; set; } =
    [
        "production_agreement",
    ];
    public string TiktokCopyrightMaterialFilePath { get; set; } = "";
    /// <summary>证明材料抬头中的版权公司名称（“致【...】”）。</summary>
    public string TiktokProofCopyrightCompanyName { get; set; } = "";
    /// <summary>证明材料正文及声明人位置使用的本公司名称。</summary>
    public string TiktokProofDeclarantCompanyName { get; set; } = "";
    /// <summary>与本公司名称匹配的印章图片；留空时仅允许保留模板固定公司的印章。</summary>
    public string TiktokProofSealPath { get; set; } = "";
    /// <summary>旧全局证明材料配置是否已迁移为账号级配置。</summary>
    public bool TiktokProofAccountConfigMigrated { get; set; }

    /// <summary>旧代码兼容别名；新代码请使用 <see cref="TiktokProofCopyrightCompanyName"/>。</summary>
    [JsonIgnore]
    public string TiktokProofSubjectCompanyName
    {
        get => TiktokProofCopyrightCompanyName;
        set => TiktokProofCopyrightCompanyName = value ?? "";
    }

    /// <summary>兼容旧 accounts.json 的 tiktokProofSubjectCompanyName，只读取、不再写出。</summary>
    [JsonPropertyName("tiktokProofSubjectCompanyName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TiktokProofLegacySubjectCompanyName
    {
        get => null;
        set
        {
            if (string.IsNullOrWhiteSpace(TiktokProofCopyrightCompanyName))
                TiktokProofCopyrightCompanyName = value ?? "";
        }
    }
    public bool TiktokAiRewriteSynopsis { get; set; } = true;
    public string TiktokPublishMode { get; set; } = "auto_after_review";
    public bool TiktokConsignmentEnabled { get; set; } = true;
    public bool TiktokZeroCostAdsEnabled { get; set; }
    public double TiktokDayZeroRoi { get; set; } = 1.05;
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
    public bool? TiktokQueueAutoArchiveAfterUpload { get; set; }
    public bool? TiktokQueuePreferUploadWhenReady { get; set; }
    public bool? TiktokQueueSyncManagementAfterUpload { get; set; }
    public bool TiktokDeleteVideosOnArchive { get; set; } = true;
    public bool TiktokDeleteVideosOnArchiveConfigured { get; set; }
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

    public string ResolveArchiveRootPath(string? workspacePath = null)
    {
        var configured = (TiktokArchiveRootDir ?? "").Trim();
        if (configured.Length > 0)
        {
            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
            }
            catch
            {
                return "";
            }
        }

        var workspace = (workspacePath ?? "").Trim();
        if (workspace.Length == 0)
            workspace = ResolveWorkspacePath();
        if (workspace.Length == 0)
            return "";

        try
        {
            return Path.Combine(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(workspace)),
                "archive");
        }
        catch
        {
            return "";
        }
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

    private void SetExternalBrowserCdpEndpointIfMissing(string? value)
    {
        var text = (value ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(TiktokExternalBrowserCdpEndpoint))
            TiktokExternalBrowserCdpEndpoint = text;
    }
}

public static class TikTokUrls
{
    public const string DefaultLoginUrl = "https://www.tiktokdramacenter.com/login";
    public const string DefaultSeriesDraftUrl = "https://www.tiktokdramacenter.com/series/draft";
    public const string DefaultSeriesListUrl = "https://www.tiktokdramacenter.com/series/list";
}
