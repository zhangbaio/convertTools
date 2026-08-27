using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Publishing;

public static class TikTokPublishConstants
{
    public const string ProductionAgreementMaterialType = "production_agreement";
    public const string SourceFileInformationMaterialType = "source_file_information";
    public const string FilingOrDistributionLicenseMaterialType = "filing_or_distribution_license";
    public const string AiGenerationScreenshotsMaterialType = "ai_generation_screenshots";
    public const string EditingProjectFilesMaterialType = "editing_project_files";
    public const string ContractIdModeManual = "manual";
    public const string ContractIdModeFirstAvailable = "first_available";

    public static readonly IReadOnlyDictionary<string, string> CopyrightMaterialLabels = new Dictionary<string, string>
    {
        ["production_agreement"] = "制作协议、联合出品协议等合作协议",
        ["work_registration_certificate"] = "作品登记证书",
        [FilingOrDistributionLicenseMaterialType] = "网络剧片备案、发行许可、监管审批文件、可信时间戳认证证书",
        ["opening_ending_rights_notice"] = "片头片尾及权利标识",
        [AiGenerationScreenshotsMaterialType] = "AI 生成过程截图",
        [EditingProjectFilesMaterialType] = "剪辑工程文件",
        [SourceFileInformationMaterialType] = "原始文件或素材文件信息",
    };

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> CopyrightMaterialLabelAliases =
        new Dictionary<string, IReadOnlyList<string>>
        {
            [FilingOrDistributionLicenseMaterialType] =
                ["网络剧片备案、发行许可、监管审批文件"],
        };

    public static IReadOnlyList<string> GetCopyrightMaterialLabelCandidates(string materialType)
    {
        var candidates = new List<string>();
        if (CopyrightMaterialLabels.TryGetValue(materialType, out var label))
            candidates.Add(label);
        if (CopyrightMaterialLabelAliases.TryGetValue(materialType, out var aliases))
            candidates.AddRange(aliases);
        return candidates.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// TikTok 版权材料下拉项使用的稳定 i18n key。页面翻译资源异常时会直接显示这些 key，
    /// 正常的中文或英文页面也保留相同的表单结构。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CopyrightMaterialI18nKeys =
        new Dictionary<string, string>
        {
            ["production_agreement"] =
                "contentPartnerHub_seriesEditPage_copyrightProof_material_productionAgreement",
            ["work_registration_certificate"] =
                "contentPartnerHub_seriesEditPage_copyrightProof_material_copyrightRegistration",
            ["filing_or_distribution_license"] =
                "contentPartnerHub_seriesEditPage_copyrightProof_material_filingPermit",
            ["opening_ending_rights_notice"] =
                "contentPartnerHub_seriesEditPage_copyrightProof_material_openingClosingCredits",
            ["ai_generation_screenshots"] =
                "contentPartnerHub_seriesEditPage_copyrightProof_material_aiProcessScreenshot",
            ["editing_project_files"] =
                "contentPartnerHub_seriesEditPage_copyrightProof_material_editingProjectFiles",
            ["source_file_information"] =
                "contentPartnerHub_seriesEditPage_copyrightProof_material_rawMaterialInfo",
        };

    public static readonly IReadOnlySet<string> CoreCopyrightMaterialTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        ProductionAgreementMaterialType,
        "work_registration_certificate",
    };

    public static readonly IReadOnlySet<string> AuxiliaryCopyrightMaterialTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        FilingOrDistributionLicenseMaterialType,
        "opening_ending_rights_notice",
        AiGenerationScreenshotsMaterialType,
        EditingProjectFilesMaterialType,
        SourceFileInformationMaterialType,
    };

    public static readonly IReadOnlyList<string> AutoManagedCopyrightMaterialTypes =
    [
        ProductionAgreementMaterialType,
        FilingOrDistributionLicenseMaterialType,
        SourceFileInformationMaterialType,
        AiGenerationScreenshotsMaterialType,
        EditingProjectFilesMaterialType,
    ];

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

    public static IReadOnlyList<string> ValidateAutoManagedCopyrightMaterialTypes(
        IEnumerable<string>? materialTypes)
    {
        var validated = ValidateCopyrightMaterialTypes(materialTypes);
        var supported = new HashSet<string>(AutoManagedCopyrightMaterialTypes, StringComparer.Ordinal);
        var unsupported = validated.Where(type => !supported.Contains(type)).ToArray();
        if (unsupported.Length > 0)
        {
            var labels = unsupported.Select(type => CopyrightMaterialLabels[type]);
            throw new NotSupportedException(
                $"以下版权材料不支持自动清空后重建：{string.Join("、", labels)}。");
        }

        return validated;
    }

    public static bool RequiresGeneratedProofMaterial(IEnumerable<string>? materialTypes) =>
        NormalizeCopyrightMaterialTypes(materialTypes)
            .Contains(ProductionAgreementMaterialType, StringComparer.Ordinal);

    public static bool RequiresAutoGeneratedCopyrightMaterial(IEnumerable<string>? materialTypes)
    {
        var normalized = NormalizeCopyrightMaterialTypes(materialTypes);
        return normalized.Contains(ProductionAgreementMaterialType, StringComparer.Ordinal) ||
               normalized.Contains(SourceFileInformationMaterialType, StringComparer.Ordinal) ||
               normalized.Contains(AiGenerationScreenshotsMaterialType, StringComparer.Ordinal) ||
               normalized.Contains(EditingProjectFilesMaterialType, StringComparer.Ordinal);
    }

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

    public static readonly IReadOnlyDictionary<string, int> ContentCreationTypeValues =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["original"] = 0,
            ["remake"] = 1,
            ["novel_adaptation"] = 3,
        };

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ContentCreationTypeLabels =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["original"] =
            [
                "原创",
                "contentPartnerHub_seriesEditPage_contentCreationType_original",
            ],
            ["remake"] =
            [
                "成片重制",
                "contentPartnerHub_seriesEditPage_contentCreationType_repainting",
            ],
            ["novel_adaptation"] =
            [
                "小说改编",
                "contentPartnerHub_seriesEditPage_contentCreationType_novelAdaptation",
            ],
        };

    public static string NormalizeContentCreationType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return ContentCreationTypeValues.ContainsKey(normalized) ? normalized : "original";
    }

    public static void ValidatePublishConfiguration(TikTokAccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var sourceLanguage = string.IsNullOrWhiteSpace(account.TiktokSourceLanguage)
            ? "zh"
            : account.TiktokSourceLanguage.Trim();
        var contentCreationType = NormalizeContentCreationType(account.TiktokContentCreationType);
        if (account.TiktokIsAiDrama &&
            string.Equals(sourceLanguage, "zh", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(contentCreationType, "remake", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "TikTok 发布配置不合理：仅源语言为非中文的短剧可选择「成片重制」。" +
                "请将源语言改为非中文，或将内容创作类型改为「原创/小说改编」。");
        }
    }

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
    public string ContentCreationType { get; set; } = "original";
    public bool IsOriginalRightsHolder { get; set; } = true;
    public string ContentOriginalityType { get; set; } = "original";
    public IReadOnlyList<string> CopyrightMaterialTypes { get; set; } = new[] { TikTokPublishConstants.ProductionAgreementMaterialType };
    public IReadOnlyDictionary<string, string> CopyrightMaterialFilePaths { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public bool UploadAiScriptOutlineWithScreenshots { get; set; }
    public bool UploadSourceInfoRoleSceneScreenshot { get; set; }
    public TikTokSourceFileInfoPackageSelection SourceInfoPackageSelection { get; set; } =
        TikTokSourceFileInfoPackageSelection.LegacyDefault();
    public string AiScriptOutlineFilePath { get; set; } = "";
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

    public IReadOnlyList<string> ResolveCopyrightMaterialFilePaths(string? materialType)
    {
        var path = ResolveCopyrightMaterialFilePath(materialType);
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            return Directory.EnumerateFiles(fullPath)
                .Where(file =>
                {
                    var ext = Path.GetExtension(file);
                    return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return File.Exists(fullPath) ? [fullPath] : [];
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
        ContentCreationType = TikTokPublishConstants.NormalizeContentCreationType(
            account.TiktokContentCreationType),
        IsOriginalRightsHolder = account.TiktokIsOriginalRightsHolder,
        ContentOriginalityType = string.IsNullOrWhiteSpace(account.TiktokContentOriginalityType)
            ? "original"
            : account.TiktokContentOriginalityType.Trim(),
        CopyrightMaterialTypes = TikTokPublishConstants.NormalizeCopyrightMaterialTypes(
            account.TiktokCopyrightMaterialTypes),
        UploadAiScriptOutlineWithScreenshots = account.TiktokUploadAiScriptOutlineWithScreenshots,
        UploadSourceInfoRoleSceneScreenshot = account.TiktokUploadSourceInfoRoleSceneScreenshot,
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
