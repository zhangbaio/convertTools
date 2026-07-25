using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Publishing;

public static class TikTokPublishConstants
{
    public const string ProductionAgreementMaterialType = "production_agreement";
    public const string ContractIdModeManual = "manual";
    public const string ContractIdModeFirstAvailable = "first_available";

    public static readonly IReadOnlyDictionary<string, string> CopyrightMaterialLabels = new Dictionary<string, string>
    {
        ["production_agreement"] = "制作协议、联合出品协议等合作协议",
        ["work_registration_certificate"] = "作品登记证书",
        ["filing_or_distribution_license"] = "网络剧片备案、发行许可、监管审批文件",
        ["opening_ending_rights_notice"] = "片头片尾及权利标识",
        ["ai_generation_screenshots"] = "AI 生成过程截图",
        ["editing_project_files"] = "剪辑工程文件",
        ["source_file_information"] = "原始文件或素材文件信息",
    };

    public static readonly IReadOnlySet<string> CoreCopyrightMaterialTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        ProductionAgreementMaterialType,
        "work_registration_certificate",
    };

    public static readonly IReadOnlySet<string> AuxiliaryCopyrightMaterialTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "filing_or_distribution_license",
        "opening_ending_rights_notice",
        "ai_generation_screenshots",
        "editing_project_files",
        "source_file_information",
    };

    public static IReadOnlyList<string> NormalizeCopyrightMaterialTypes(IEnumerable<string>? materialTypes)
    {
        var normalized = new List<string>();
        foreach (var value in materialTypes ?? [])
        {
            var candidate = (value ?? string.Empty).Trim();
            var canonical = CopyrightMaterialLabels.Keys.FirstOrDefault(key =>
                string.Equals(key, candidate, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null && !normalized.Contains(canonical, StringComparer.Ordinal))
                normalized.Add(canonical);
        }

        return normalized.Count > 0
            ? normalized
            : [ProductionAgreementMaterialType];
    }

    public static IReadOnlyList<string> ValidateCopyrightMaterialTypes(IEnumerable<string>? materialTypes)
    {
        var normalized = NormalizeCopyrightMaterialTypes(materialTypes);
        var coreCount = normalized.Count(CoreCopyrightMaterialTypes.Contains);
        var auxiliaryCount = normalized.Count(AuxiliaryCopyrightMaterialTypes.Contains);
        if (coreCount == 0 && auxiliaryCount < 2)
        {
            throw new InvalidOperationException(
                "TikTok 上传材料类型配置无效：请至少选择 1 个核心材料，或至少 2 个辅助材料。");
        }

        return normalized;
    }

    public static bool RequiresGeneratedProofMaterial(IEnumerable<string>? materialTypes) =>
        NormalizeCopyrightMaterialTypes(materialTypes)
            .Contains(ProductionAgreementMaterialType, StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, string> PublishModeLabels = new Dictionary<string, string>
    {
        ["auto_after_review"] = "过审后自动发布",
        ["manual"] = "手动发布",
        ["scheduled"] = "定时发布",
    };

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> TargetAudienceAliases = new Dictionary<string, IReadOnlyList<string>>
    {
        ["female"] = new[] { "女性", "女", "Female", "Women" },
        ["male"] = new[] { "男性", "男", "Male", "Men" },
    };

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> SourceLanguageAliases =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = new[] { "中文", "Chinese", "简体中文" },
        ["en"] = new[] { "英语", "English" },
        ["id"] = new[] { "印尼语", "印度尼西亚语", "Indonesian", "Bahasa Indonesia" },
        ["pt"] = new[] { "葡语", "葡萄牙语", "Portuguese", "Português" },
        ["ja"] = new[] { "日语", "Japanese", "日本語" },
        ["th"] = new[] { "泰语", "Thai", "ไทย" },
        ["es"] = new[] { "西语", "西班牙语", "Spanish", "Español" },
        ["ko"] = new[] { "韩语", "韩国语", "Korean", "한국어" },
        ["tr"] = new[] { "土耳其语", "Turkish", "Türkçe" },
        ["hi"] = new[] { "印地语", "Hindi", "हिन्दी" },
    };

    /// <summary>对齐 Python <c>GENRE_OPTIONS</c>。</summary>
    public static readonly IReadOnlyList<string> GenreOptions =
    [
        "年龄差", "Alpha", "古风", "虐恋", "出轨", "替身", "商战", "青春", "娱乐圈", "总裁",
        "都市", "头目", "萌宝", "年代", "亲情", "豪门", "玄幻", "禁忌恋", "大女主", "闪婚",
        "一见钟情", "将军", "团宠", "后宫", "千金", "超级英雄", "马甲", "神医", "伦理", "一夜情",
        "扮猪吃虎", "怀孕", "总统", "破镜重圆", "重逢", "复仇", "心动拉扯", "重生", "暗恋", "赘婿",
        "异能", "系统", "悬疑", "穿越", "三角恋", "逆袭", "吸血鬼", "阿尔法狼人", "职场",
    ];
}

public sealed class TikTokPublishRecommendation
{
    public string TargetAudience { get; init; } = "female";
    public IReadOnlyList<string> Genres { get; init; } = new[] { "都市" };
}

public sealed class TikTokPublishOptions
{
    public const int DefaultGenreCount = 3;
    public const int MinGenreCount = 1;
    public const int MaxGenreCount = 8;
    public const double DefaultDayZeroRoi = 1.05;
    public const double MinDayZeroRoi = 1.0;
    public const double MaxDayZeroRoi = 1.5;

    public string ContractId { get; set; } = "";
    public string ContractIdMode { get; set; } = TikTokPublishConstants.ContractIdModeManual;
    public bool AnchorPromotionEnabled { get; set; } = true;
    public string TargetAudienceMode { get; set; } = "ai_recommend";
    public int GenreCount { get; set; } = DefaultGenreCount;
    public string SourceLanguage { get; set; } = "zh";
    public bool IsAiDrama { get; set; } = true;
    public bool IsOriginalRightsHolder { get; set; } = true;
    public string ContentOriginalityType { get; set; } = "original";
    public IReadOnlyList<string> CopyrightMaterialTypes { get; set; } = new[] { TikTokPublishConstants.ProductionAgreementMaterialType };
    public IReadOnlyDictionary<string, string> CopyrightMaterialFilePaths { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    /// <summary>旧版单文件字段，仅兼容合作协议；其他材料不得复用此路径。</summary>
    public string CopyrightMaterialFilePath { get; set; } = "";
    public string PublishMode { get; set; } = "auto_after_review";
    public bool ConsignmentEnabled { get; set; } = true;
    public bool ZeroCostAdsEnabled { get; set; }
    public double DayZeroRoi { get; set; } = DefaultDayZeroRoi;
    public bool PaidEnabled { get; set; }
    public int ProfilePreviewEpisodes { get; set; } = 3;
    public int FreePreviewEpisodes { get; set; } = 3;
    public string ExpectedFullPriceMode { get; set; } = "manual";
    public int ExpectedFullPriceOptionIndex { get; set; } = 1;
    public string ExpectedFullPriceValue { get; set; } = "";
    public string ExpectedFullPriceLabel { get; set; } = "";
    public int UploadStallSeconds { get; set; } = 180;
    public string UploadStrategy { get; set; } = "classic";
    public int UploadBatchSize { get; set; } = 3;
    public int UploadBatchStallSeconds { get; set; } = 75;
    public int UploadBatchMaxRetries { get; set; } = 3;

    public bool UseBatchUpload =>
        string.Equals(UploadStrategy?.Trim(), "batch", StringComparison.OrdinalIgnoreCase);

    public string ResolveCopyrightMaterialFilePath(string? materialType)
    {
        var key = (materialType ?? string.Empty).Trim();
        if (CopyrightMaterialFilePaths.TryGetValue(key, out var configured) &&
            !string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return string.Equals(
                key,
                TikTokPublishConstants.ProductionAgreementMaterialType,
                StringComparison.Ordinal)
            ? CopyrightMaterialFilePath?.Trim() ?? string.Empty
            : string.Empty;
    }

    public string PublishModeLabel =>
        TikTokPublishConstants.PublishModeLabels.TryGetValue(PublishMode, out var label)
            ? label
            : TikTokPublishConstants.PublishModeLabels["auto_after_review"];

    public IReadOnlyList<string> SourceLanguageLabels
    {
        get
        {
            var key = string.IsNullOrWhiteSpace(SourceLanguage) ? "zh" : SourceLanguage.Trim();
            var labels = new List<string>();
            if (TikTokPublishConstants.SourceLanguageAliases.TryGetValue(key, out var aliases))
                labels.AddRange(aliases);
            labels.Add(key);
            return labels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public static TikTokPublishOptions FromAccount(TikTokAccountProfile account) => new()
    {
        ContractId = account.TiktokContractId,
        ContractIdMode = string.IsNullOrWhiteSpace(account.TiktokContractIdMode)
            ? TikTokPublishConstants.ContractIdModeManual
            : account.TiktokContractIdMode,
        AnchorPromotionEnabled = account.TiktokAnchorPromotionEnabled,
        TargetAudienceMode = string.IsNullOrWhiteSpace(account.TiktokTargetAudienceMode) ? "ai_recommend" : account.TiktokTargetAudienceMode,
        GenreCount = NormalizeGenreCount(account.TiktokGenreCount),
        SourceLanguage = string.IsNullOrWhiteSpace(account.TiktokSourceLanguage) ? "zh" : account.TiktokSourceLanguage,
        IsAiDrama = account.TiktokIsAiDrama,
        IsOriginalRightsHolder = account.TiktokIsOriginalRightsHolder,
        ContentOriginalityType = string.IsNullOrWhiteSpace(account.TiktokContentOriginalityType)
            ? "original"
            : account.TiktokContentOriginalityType.Trim(),
        CopyrightMaterialTypes = TikTokPublishConstants.NormalizeCopyrightMaterialTypes(
            account.TiktokCopyrightMaterialTypes),
        // 账号级测试文件不再参与正式上传。合作协议只能由当前项目生成的证明材料提供。
        CopyrightMaterialFilePath = "",
        PublishMode = string.IsNullOrWhiteSpace(account.TiktokPublishMode) ? "auto_after_review" : account.TiktokPublishMode,
        ConsignmentEnabled = account.TiktokConsignmentEnabled,
        ZeroCostAdsEnabled = account.TiktokZeroCostAdsEnabled,
        DayZeroRoi = NormalizeDayZeroRoi(account.TiktokDayZeroRoi),
        PaidEnabled = account.TiktokPaidEnabled,
        ProfilePreviewEpisodes = Math.Max(0, account.TiktokProfilePreviewEpisodes > 0
            ? account.TiktokProfilePreviewEpisodes
            : 3),
        FreePreviewEpisodes = Math.Max(0, account.TiktokFreePreviewEpisodes > 0
            ? account.TiktokFreePreviewEpisodes
            : 3),
        ExpectedFullPriceMode = string.IsNullOrWhiteSpace(account.TiktokExpectedFullPriceMode) ? "manual" : account.TiktokExpectedFullPriceMode,
        ExpectedFullPriceOptionIndex = account.TiktokExpectedFullPriceOptionIndex > 0 ? account.TiktokExpectedFullPriceOptionIndex : 1,
        ExpectedFullPriceValue = account.TiktokExpectedFullPriceValue,
        ExpectedFullPriceLabel = account.TiktokExpectedFullPriceLabel,
        UploadStallSeconds = account.TiktokUploadStallSeconds > 0 ? account.TiktokUploadStallSeconds : 180,
        UploadStrategy = string.IsNullOrWhiteSpace(account.TiktokUploadStrategy) ? "classic" : account.TiktokUploadStrategy.Trim(),
        UploadBatchSize = account.TiktokUploadBatchSize > 0 ? Math.Clamp(account.TiktokUploadBatchSize, 1, 20) : 3,
        UploadBatchStallSeconds = account.TiktokUploadBatchStallSeconds > 0 ? Math.Clamp(account.TiktokUploadBatchStallSeconds, 20, 600) : 75,
        UploadBatchMaxRetries = account.TiktokUploadBatchMaxRetries > 0 ? Math.Clamp(account.TiktokUploadBatchMaxRetries, 1, 10) : 3,
    };

    public TikTokPublishRecommendation BuildRecommendation(PublishItem item)
    {
        var projectPayload = TikTokProjectPayloadFactory.BuildFromPublishItem(item);
        var maxCount = NormalizeGenreCount(GenreCount);

        var targetAudience = !string.IsNullOrWhiteSpace(projectPayload.TargetAudience)
            ? projectPayload.TargetAudience
            : TargetAudienceMode is "male" or "female"
                ? TargetAudienceMode
                : "female";

        var genres = projectPayload.Genres.Count > 0
            ? projectPayload.Genres.Take(maxCount).ToList()
            : TikTokProjectPayloadFactory.ParseGenreTokens(item.GenreCategory ?? "").Take(maxCount).ToList();
        if (genres.Count == 0)
            genres.Add("都市");

        return new TikTokPublishRecommendation
        {
            TargetAudience = targetAudience,
            Genres = genres,
        };
    }

    public static int NormalizeGenreCount(int value) =>
        Math.Clamp(value > 0 ? value : DefaultGenreCount, MinGenreCount, MaxGenreCount);

    public static double NormalizeDayZeroRoi(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) ||
            value < MinDayZeroRoi || value > MaxDayZeroRoi)
        {
            return DefaultDayZeroRoi;
        }

        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }
}

public sealed class TikTokPublishPayload
{
    public string Title { get; init; } = "";
    public string OriginalTitle { get; init; } = "";
    public string Description { get; init; } = "";
    public int EpisodeCount { get; init; } = 1;
    public IReadOnlyList<string> VideoPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UploadVideoPaths { get; init; } = Array.Empty<string>();

    public static TikTokPublishPayload FromPublishItem(PublishItem item)
    {
        var projectPayload = TikTokProjectPayloadFactory.BuildFromPublishItem(item);

        IReadOnlyList<string> uploadPaths;
        if (!string.IsNullOrWhiteSpace(item.ProjectDir) && Directory.Exists(item.ProjectDir))
        {
            var resolved = ProjectVideoResolver.ResolveUploadVideos(item.ProjectDir, allowStagedFallback: true);
            uploadPaths = resolved.Count > 0 ? resolved : new[] { item.VideoPath };
        }
        else
        {
            uploadPaths = new[] { item.VideoPath };
        }

        return new TikTokPublishPayload
        {
            Title = projectPayload.Title,
            OriginalTitle = projectPayload.OriginalTitle,
            Description = projectPayload.Description,
            EpisodeCount = projectPayload.EpisodeCount,
            VideoPaths = uploadPaths,
            UploadVideoPaths = uploadPaths,
        };
    }
}
