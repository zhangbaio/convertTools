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

        if (!account.TiktokCustomPublishAccountsEnabled)
            return;

        var catalog = TikTokPublishAccountCatalog.Parse(account.TiktokPublishAccountCatalogJson);
        if (catalog.Count == 0)
        {
            throw new InvalidOperationException(
                "已启用自定义发布账号，但还没有获取已创建账号。请在「账号管理 → 发布配置」中点击「获取已创建账号」。");
        }

        var selected = TikTokPublishAccountCatalog.Retain(
            account.TiktokSelectedPublishAccountKeys,
            catalog);
        if (selected.KeptKeys.Count == 0)
        {
            throw new InvalidOperationException(
                "已启用自定义发布账号，但没有勾选任何账号。请至少选择一个发布账号。");
        }
    }

    /// <summary>
    /// 剧集详情「题材类型」(<c>themeTag</c>) 的真实选项。
    /// <see cref="TikTokGenreOption.Value"/> 是选项 <c>data-value</c>，不随界面语言变化；
    /// 翻译资源异常时页面会直接显示 <see cref="TikTokGenreOption.EnglishLabel"/>。
    /// 顺序与 2026-10-23 账号 2 草稿页抓到的下拉一致。
    /// </summary>
    public static readonly IReadOnlyList<TikTokGenreOption> GenreCatalog =
    [
        new("年龄差", "AgeGap", "7480113204073349175"),
        new("Alpha", "Alpha", "7480129235898695685"),
        new("古风", "Ancient", "7475195943084703799"),
        new("虐恋", "Angsty Love", "7480111504197664774"),
        new("出轨", "BetrayLove", "7480116372064698374"),
        new("替身", "Body Double", "7480116784972988422"),
        new("商战", "Business Competition", "7475195414552150021"),
        new("青春", "Campus", "7475195414552231941"),
        new("娱乐圈", "Celebrity", "7475196057492652087"),
        new("总裁", "CEO", "7480119548807315461"),
        new("都市", "City", "7475195626620272646"),
        new("头目", "Crime Lord", "7480129235898793989"),
        new("萌宝", "Cute Kids", "7480117166257799174"),
        new("年代", "Decade", "7475195626620436486"),
        new("亲情", "Family", "7480116475617935415"),
        new("豪门", "Family Feud", "7475195943084621879"),
        new("玄幻", "Fantasy", "7475196057492734007"),
        new("禁忌恋", "Fated Lovers", "7480115418862977079"),
        new("大女主", "Female Lead", "7480120358620790789"),
        new("闪婚", "Flash marriage", "7480116784973086726"),
        new("一见钟情", "Flipped", "7480116475618033719"),
        new("将军", "General", "7480120358620692485"),
        new("团宠", "Group Favorite", "7480119548807217157"),
        new("后宫", "Harem", "7475195414551986181"),
        new("千金", "Heiress", "7480129235898597381"),
        new("超级英雄", "Hero", "7480120358620889093"),
        new("马甲", "Hidden Identity", "7480116784973185030"),
        new("神医", "Miracle Doctor", "7480117166257897478"),
        new("伦理", "Morality", "7480113204073562167"),
        new("一夜情", "One-night stand", "7480116372064894982"),
        new("扮猪吃虎", "Playing Dumb", "7480117007484977157"),
        new("怀孕", "Pregnancy", "7480116372064796678"),
        new("总统", "Presient", "7480129001525313541"),
        new("破镜重圆", "Reconciliation", "7480113204073463863"),
        new("重逢", "Reunion", "7480116475618132023"),
        new("复仇", "Revenge", "7480117007485075461"),
        new("心动拉扯", "Romance", "7480116372064993286"),
        new("重生", "Second Chance", "7480116663103389751"),
        new("暗恋", "Secret Love", "7475195626620190726"),
        new("赘婿", "Son-in-Law", "7480117166257995782"),
        new("异能", "Superpowers", "7475195943084785719"),
        new("系统", "System", "7480117007485173765"),
        new("悬疑", "Thriller", "7475195626620354566"),
        new("穿越", "Time Travel", "7480116663103488055"),
        new("三角恋", "Triangle-love", "7475195943084867639"),
        new("逆袭", "Underdog Story", "7480116663103291447"),
        new("吸血鬼", "Vampire", "7480129001525215237"),
        new("阿尔法狼人", "Werewolf", "7480129001525116933"),
        new("职场", "Workplace", "7475195414552068101"),
    ];

    /// <summary>推荐和短剧信息仍使用中文题材名。</summary>
    public static readonly IReadOnlyList<string> GenreOptions =
        GenreCatalog.Select(static option => option.Canonical).ToArray();

    private static readonly Lazy<Dictionary<string, TikTokGenreOption>> GenreByAlias =
        new(BuildGenreAliasIndex);

    private static readonly Lazy<Dictionary<string, TikTokGenreOption>> GenreByValue =
        new(BuildGenreValueIndex);

    public static string NormalizeGenreAlias(string? text) =>
        string.Join(' ', (text ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string NormalizeGenreValue(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1].Trim();
        return text;
    }

    public static bool TryResolveGenre(string? text, out TikTokGenreOption option)
    {
        var alias = NormalizeGenreAlias(text);
        if (alias.Length > 0 && GenreByAlias.Value.TryGetValue(alias, out option))
            return true;

        var value = NormalizeGenreValue(text);
        if (value.Length > 0 && GenreByValue.Value.TryGetValue(value, out option))
            return true;

        option = default;
        return false;
    }

    public static string CanonicalGenreName(string? text) =>
        TryResolveGenre(text, out var option) ? option.Canonical : NormalizeGenreAlias(text);

    public static bool GenreOptionMatches(string? visibleText, string? dataValue, string? desired)
    {
        if (!TryResolveGenre(desired, out var desiredOption))
        {
            var wanted = NormalizeGenreAlias(desired);
            return wanted.Length > 0 &&
                   string.Equals(NormalizeGenreAlias(visibleText), wanted, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(NormalizeGenreValue(dataValue), desiredOption.Value, StringComparison.Ordinal))
            return true;

        return TryResolveGenre(visibleText, out var visibleOption) &&
               string.Equals(visibleOption.Canonical, desiredOption.Canonical, StringComparison.Ordinal);
    }

    private static Dictionary<string, TikTokGenreOption> BuildGenreAliasIndex()
    {
        var map = new Dictionary<string, TikTokGenreOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in GenreCatalog)
        {
            RegisterGenreAlias(map, option.Canonical, option);
            RegisterGenreAlias(map, option.EnglishLabel, option);
        }

        return map;
    }

    private static Dictionary<string, TikTokGenreOption> BuildGenreValueIndex()
    {
        var map = new Dictionary<string, TikTokGenreOption>(StringComparer.Ordinal);
        foreach (var option in GenreCatalog)
        {
            if (!map.TryAdd(option.Value, option))
                throw new InvalidOperationException($"题材选项值重复：{option.Value}");
        }

        return map;
    }

    private static void RegisterGenreAlias(
        Dictionary<string, TikTokGenreOption> map,
        string alias,
        TikTokGenreOption option)
    {
        var key = NormalizeGenreAlias(alias);
        if (key.Length == 0)
            throw new InvalidOperationException("题材别名为空。");
        if (map.TryGetValue(key, out var existing) &&
            !string.Equals(existing.Canonical, option.Canonical, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"题材别名冲突：{alias}");
        }

        map[key] = option;
    }
}

public readonly record struct TikTokGenreOption(string Canonical, string EnglishLabel, string Value);

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
    public bool UploadSourceInfoRoleVector { get; set; }
    public bool UploadSourceInfoRoleSceneScreenshot { get; set; }
    public TikTokSourceFileInfoPackageSelection SourceInfoPackageSelection { get; set; } =
        new(true, true, false, false);
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
    public bool CustomPublishAccountsEnabled { get; set; }
    public IReadOnlyList<string> SelectedPublishAccountKeys { get; set; } = [];

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
        UploadSourceInfoRoleVector = account.TiktokUploadSourceInfoRoleVector,
        UploadSourceInfoRoleSceneScreenshot = account.TiktokUploadSourceInfoRoleSceneScreenshot,
        SourceInfoPackageSelection = new TikTokSourceFileInfoPackageSelection(
            true,
            true,
            account.TiktokUploadSourceInfoRoleVector,
            account.TiktokUploadSourceInfoRoleSceneScreenshot),
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
        CustomPublishAccountsEnabled = account.TiktokCustomPublishAccountsEnabled,
        SelectedPublishAccountKeys = account.TiktokCustomPublishAccountsEnabled
            ? TikTokPublishAccountCatalog.Retain(
                account.TiktokSelectedPublishAccountKeys,
                TikTokPublishAccountCatalog.Parse(account.TiktokPublishAccountCatalogJson)).KeptKeys
            : [],
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
