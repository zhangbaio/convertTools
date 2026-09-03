using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Publishing.Models;

public enum MaterialSourceKind
{
    Project,
    LocalDirectory,
    DirectoryGroups,
    CustomFiles,
    AdxBatch,
    SystemHighlight,
    DownloadedWork,
}

public enum MaterialDistributionMode { Broadcast, Balanced }
public enum PublishFailurePolicy { Continue, StopAll }
public enum UnifiedFinalAction { Draft, Publish }
public enum MediaVariantMode { Shared, PerAccount }
public enum MediaEncoder { Auto, Cpu, Nvidia }

public enum UnifiedPublishItemStatus
{
    Pending,
    Preparing,
    Running,
    Success,
    DraftSaved,
    Failed,
    Cancelled,
    SubmissionUnknown,
    Skipped,
}

public enum PublishErrorKind
{
    None,
    Recoverable,
    AccountFatal,
    SubmissionUnknown,
    Cancelled,
}

public sealed record MaterialOrigin(
    MaterialSourceKind Kind,
    string SourceId = "",
    string BatchId = "",
    string ManifestPath = "",
    string ProjectKey = "",
    string PayloadJson = "{}");

public sealed class ResolvedMaterial
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Sequence { get; set; }
    public string VideoPath { get; set; } = string.Empty;
    public string? CoverPath { get; set; }
    public string? Description { get; set; }
    public string? ShortTitle { get; set; }
    public bool ContentFinalized { get; set; }
    public MaterialOrigin Origin { get; set; } = new(MaterialSourceKind.CustomFiles);
}

public sealed class MaterialSourceSpec
{
    public MaterialSourceKind Kind { get; set; }
    public string Label { get; set; } = string.Empty;
    public string WorkflowDirectory { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public string NewTitle { get; set; } = string.Empty;
    public List<string> Files { get; set; } = [];
    public string PayloadJson { get; set; } = "{}";
}

public sealed class UnifiedPublishForm
{
    public int SchemaVersion { get; set; } = 1;
    public string SeriesName { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public string NewTitle { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public bool FillDescription { get; set; } = true;
    public bool AiDescriptionEnabled { get; set; }
    public string DescriptionTemplate { get; set; } = "热门短剧，精彩内容持续更新。";
    public bool FillShortTitle { get; set; }
    public int ShortTitleMaxLength { get; set; } = 16;
    public bool DeclareOriginal { get; set; } = true;
    public string VideoAnnotation { get; set; } = "无需标注";
    public string LocationOption { get; set; } = "不显示位置";
    public bool LinkSeries { get; set; }
    public string LinkSeriesName { get; set; } = string.Empty;
    public string ActivityOption { get; set; } = string.Empty;
    public DateTimeOffset? PlatformScheduledAt { get; set; }
    public int IntervalMinutes { get; set; }
    public string CoverMode { get; set; } = "sidecar";
    public string CoverImagePath { get; set; } = string.Empty;
    public UnifiedFinalAction FinalAction { get; set; } = UnifiedFinalAction.Draft;
    public bool Headless { get; set; }
    public bool StopOnError { get; set; }
}

public sealed class MediaProcessingProfile
{
    public int SchemaVersion { get; set; } = 1;
    public bool Enabled { get; set; }
    public string Preset { get; set; } = "balanced";
    public MediaVariantMode VariantMode { get; set; } = MediaVariantMode.PerAccount;
    public MediaEncoder Encoder { get; set; } = MediaEncoder.Auto;
    public int VideoCrf { get; set; } = 23;
    public bool ClearMetadata { get; set; } = true;
    public bool AudioEnhance { get; set; } = true;
    public bool Mirror { get; set; }
    public bool SubtitleRebuild { get; set; }
    public bool FixedTextMask { get; set; } = true;
    public int MaskOpacity { get; set; } = 60;
    public bool ZoomCrop { get; set; } = true;
    public bool ColorAdjust { get; set; } = true;
    public bool SpeedAdjust { get; set; } = true;
    public bool Fade { get; set; }
    public bool ForegroundZoom { get; set; } = true;
    public double CropVerticalPercent { get; set; }
    public double CropHorizontalPercent { get; set; }
    public double ForegroundZoomPercent { get; set; } = 8;
    public bool BlurBackground { get; set; }
    public bool StickerStrip { get; set; } = true;
    public bool Noise { get; set; } = true;
    public string StickerDirectory { get; set; } = string.Empty;
}

public sealed class PublishDraft
{
    public int SchemaVersion { get; set; } = 2;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MaterialSourceSpec Source { get; set; } = new();
    public List<ResolvedMaterial> Items { get; set; } = [];
    public UnifiedPublishForm Form { get; set; } = new();
    public MediaProcessingProfile MediaProcessing { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record PublishTarget(string AccountId, string AccountName, string SessionDirectory, int Order, string ConfigPath = "");

public sealed record AccountPublishPlan(
    PublishTarget Target,
    IReadOnlyList<ResolvedMaterial> Items,
    MaterialSourceSpec Source,
    UnifiedPublishForm Form,
    MediaProcessingProfile MediaProcessing);

public sealed class PublishBatchRequest
{
    public string BatchId { get; set; } = Guid.NewGuid().ToString("N");
    public PublishDraft Draft { get; set; } = new();
    public List<PublishTarget> Targets { get; set; } = [];
    public MaterialDistributionMode DistributionMode { get; set; }
    public PublishFailurePolicy FailurePolicy { get; set; } = PublishFailurePolicy.Continue;
    public int MaxParallelAccounts { get; set; } = 2;
    public string? RetryOfBatchId { get; set; }
    public Dictionary<string, List<string>>? FrozenAssignments { get; set; }
}

public sealed record PublishItemOutcome(
    string ItemId,
    UnifiedPublishItemStatus Status,
    string Message,
    PublishErrorKind ErrorKind,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int Attempts);

public sealed record AccountPublishOutcome(
    string AccountId,
    UnifiedPublishItemStatus Status,
    string Message,
    IReadOnlyList<PublishItemOutcome> Items);

public sealed record PublishBatchOutcome(
    string BatchId,
    UnifiedPublishItemStatus Status,
    string Message,
    IReadOnlyList<AccountPublishOutcome> Accounts,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

public sealed record UnifiedPublishProgress(
    string BatchId,
    string AccountId,
    string? ItemId,
    string Stage,
    string Message,
    int Completed,
    int Total);
