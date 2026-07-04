using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Publishing;

public static class TikTokPublishConstants
{
    public const string ContractIdModeManual = "manual";
    public const string ContractIdModeFirstAvailable = "first_available";

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

    public static readonly IReadOnlyDictionary<string, string> SourceLanguageLabels = new Dictionary<string, string>
    {
        ["zh"] = "中文",
        ["en"] = "English",
        ["id"] = "Indonesian",
        ["pt"] = "Portuguese",
        ["ja"] = "Japanese",
        ["th"] = "Thai",
        ["es"] = "Spanish",
        ["ko"] = "Korean",
        ["tr"] = "Turkish",
        ["hi"] = "Hindi",
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
    public string ContractId { get; set; } = "";
    public string ContractIdMode { get; set; } = TikTokPublishConstants.ContractIdModeManual;
    public bool AnchorPromotionEnabled { get; set; }
    public string TargetAudienceMode { get; set; } = "female";
    public int GenreCount { get; set; } = 1;
    public string SourceLanguage { get; set; } = "zh";
    public bool IsAiDrama { get; set; } = true;
    public string PublishMode { get; set; } = "auto_after_review";
    public bool ConsignmentEnabled { get; set; }
    public bool PaidEnabled { get; set; }
    public int ProfilePreviewEpisodes { get; set; } = 1;
    public int FreePreviewEpisodes { get; set; } = 1;
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
            if (TikTokPublishConstants.SourceLanguageLabels.TryGetValue(key, out var primary))
                labels.Add(primary);
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
        TargetAudienceMode = string.IsNullOrWhiteSpace(account.TiktokTargetAudienceMode) ? "female" : account.TiktokTargetAudienceMode,
        GenreCount = account.TiktokGenreCount > 0 ? account.TiktokGenreCount : 1,
        SourceLanguage = string.IsNullOrWhiteSpace(account.TiktokSourceLanguage) ? "zh" : account.TiktokSourceLanguage,
        IsAiDrama = account.TiktokIsAiDrama,
        PublishMode = string.IsNullOrWhiteSpace(account.TiktokPublishMode) ? "auto_after_review" : account.TiktokPublishMode,
        ConsignmentEnabled = account.TiktokConsignmentEnabled,
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
        var maxCount = Math.Max(1, GenreCount);

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
