using System.Text.Json;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Weixin.Publishing;

public sealed class WeixinPublishOptions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public string EpisodeSelectionMode { get; set; } = "range";
    public int StartEpisodeIndex { get; set; } = 1;
    public string EpisodeIndexes { get; set; } = string.Empty;
    public bool MergePublishEnabled { get; set; }
    public int MergePublishGroupSize { get; set; }
    public bool FillDescription { get; set; } = true;
    public bool AiDescriptionEnabled { get; set; }
    public bool AiDescriptionUseAsr { get; set; } = true;
    public bool PrependHashToDescription { get; set; }
    public string DescriptionTemplate { get; set; } = "热门短剧，精彩内容持续更新。";
    public string LocationOptionText { get; set; } = "不显示位置";
    public string LinkOptionText { get; set; } = string.Empty;
    public string LinkPickerButtonText { get; set; } = "选择需要添加的视频号剧集";
    public string LinkDialogTitle { get; set; } = "选择需要关联的视频号剧集";
    public string LinkSearchPlaceholder { get; set; } = "搜索内容";
    public string ActivityOptionText { get; set; } = string.Empty;
    public string TimingOptionText { get; set; } = "不定时";
    public bool ReplaceCoverWithLocalImage { get; set; }
    public string CoverImagePath { get; set; } = string.Empty;
    public bool FillShortTitle { get; set; }
    public int ShortTitleMaxLength { get; set; } = 16;
    public bool DeclareOriginal { get; set; } = true;
    public string FinalAction { get; set; } = "publish";
    public bool PauseOnError { get; set; } = true;
    public bool FastMode { get; set; }
    public bool CaptureScreenshots { get; set; } = true;
    public bool CaptureDebugDumps { get; set; } = true;

    public static WeixinPublishOptions FromJob(PublishJob job)
    {
        WeixinPublishOptions options;
        try
        {
            options = string.IsNullOrWhiteSpace(job.PlatformOptionsJson)
                ? new WeixinPublishOptions()
                : JsonSerializer.Deserialize<WeixinPublishOptions>(job.PlatformOptionsJson, JsonOptions)
                  ?? new WeixinPublishOptions();
        }
        catch (JsonException)
        {
            options = new WeixinPublishOptions();
        }

        if (string.IsNullOrWhiteSpace(job.PlatformOptionsJson))
        {
            options.DescriptionTemplate = job.PublishDescription;
            options.DeclareOriginal = job.DeclareOriginal;
            options.LocationOptionText = job.HideLocation ? "不显示位置" : string.Empty;
        }

        return options.Normalize();
    }

    public string ToJson() => JsonSerializer.Serialize(Normalize(), JsonOptions);

    public IReadOnlyList<int> ResolveEpisodeIndexes(int availableCount, int requestedCount)
    {
        var count = Math.Max(0, availableCount);
        if (count == 0) return [];
        if (EpisodeSelectionMode == "all") return Enumerable.Range(1, count).ToArray();
        if (EpisodeSelectionMode == "explicit")
        {
            return EpisodeIndexes
                .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.TryParse(value, out var index) ? index : 0)
                .Where(index => index >= 1 && index <= count)
                .Distinct()
                .ToArray();
        }

        var start = Math.Clamp(StartEpisodeIndex, 1, count);
        return Enumerable.Range(start, Math.Min(Math.Max(1, requestedCount), count - start + 1)).ToArray();
    }

    private WeixinPublishOptions Normalize()
    {
        EpisodeSelectionMode = EpisodeSelectionMode.Trim().ToLowerInvariant() switch
        {
            "all" => "all",
            "explicit" => "explicit",
            _ => "range",
        };
        StartEpisodeIndex = Math.Max(1, StartEpisodeIndex);
        MergePublishGroupSize = Math.Max(0, MergePublishGroupSize);
        ShortTitleMaxLength = Math.Clamp(ShortTitleMaxLength, 1, 30);
        FinalAction = FinalAction.Trim().ToLowerInvariant() switch
        {
            "draft" or "save" => "draft",
            "test" => "test",
            _ => "publish",
        };
        return this;
    }
}
