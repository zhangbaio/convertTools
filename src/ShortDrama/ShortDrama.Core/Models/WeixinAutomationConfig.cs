namespace ShortDrama.Core.Models;

public sealed record WeixinAutomationConfig(
    string? ConfigPath,
    string ConfigDirectory,
    string BaseUrl,
    string AuthFilePath,
    string OutputDirectory,
    string TaskType,
    WeixinBrowserOptions Browser,
    WeixinDebugOptions Debug,
    WeixinLoginOptions Login,
    bool PauseOnError,
    WeixinNavigationOptions Navigation,
    WeixinFirstPageOptions FirstPage,
    WeixinSecondPageOptions SecondPage,
    WeixinSubmitOptions Submit,
    WeixinVideoPublishOptions VideoPublish);

public sealed record WeixinBrowserOptions(
    bool Headless,
    int SlowMoMs,
    int KeepOpenSeconds,
    string UserDataDirectory,
    string UserAgent,
    WeixinViewportOptions Viewport);

public sealed record WeixinViewportOptions(
    int Width,
    int Height);

public sealed record WeixinDebugOptions(
    string LogFilePath,
    bool SaveHtml,
    bool SaveText);

public sealed record WeixinLoginOptions(
    int TimeoutSeconds);

public sealed record WeixinNavigationOptions(
    string Section,
    string Item,
    string EntryButton);

public sealed record WeixinFirstPageOptions(
    string ReadyText,
    string NextButtonText,
    IReadOnlyList<string> ReadyLabels,
    IReadOnlyList<WeixinFormAction> Actions);

public sealed record WeixinFormAction(
    string Type,
    string? Label,
    string? Control,
    string? Value,
    string? Selector,
    string? FieldLabel,
    string? OptionText,
    string? Text,
    bool Exact,
    bool? Enabled,
    string? Name,
    string? Message,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> WaitForTexts);

public sealed record WeixinSecondPageOptions(
    string ReadyText,
    IReadOnlyList<WeixinFormAction> ActionsBeforeUpload,
    WeixinUploadAction Upload,
    WeixinUploadQueueOptions UploadQueue,
    WeixinSubmitPageEntryOptions EnterSubmitPage,
    IReadOnlyList<WeixinFormAction> ActionsAfterUpload);

public sealed record WeixinUploadQueueOptions(
    string Text,
    string Selector,
    string Mode,
    IReadOnlyList<WeixinUploadQueueItem> Items,
    int ItemTimeoutSeconds,
    string OnItemError,
    string OnItemTimeout,
    bool RetryFailedUploads,
    int RetryMaxRounds,
    int RetryIntervalSeconds,
    string RetryActionText,
    string RetryDeleteText,
    int RetryStableRounds,
    IReadOnlyList<string> SuccessTexts,
    IReadOnlyList<string> ErrorTexts);

public sealed record WeixinUploadQueueItem(
    string Path,
    bool Enabled);

public sealed record WeixinUploadAction(
    string InputSelector,
    IReadOnlyList<string> Paths,
    int TimeoutSeconds,
    IReadOnlyList<string> SuccessTexts,
    IReadOnlyList<string> ErrorTexts);

public sealed record WeixinSubmitPageEntryOptions(
    bool Enabled,
    string Text,
    string WaitText);

public sealed record WeixinSubmitOptions(
    bool Enabled,
    string Text,
    string ReadyText);

public sealed record WeixinVideoPublishOptions(
    bool Enabled,
    WeixinNavigationOptions Navigation,
    string ReadyText,
    string RunStrategy,
    string StateFile,
    bool AllowDuplicatePublish,
    bool PauseOnError,
    string VideoSourceMode,
    bool FillDescription,
    bool FillShortTitle,
    string DescriptionTemplate,
    bool PrependHashToDescription,
    string LocationOptionText,
    string LinkOptionText,
    string LinkPickerButtonText,
    string LinkPickerSelector,
    string LinkDialogTitle,
    string LinkSearchPlaceholder,
    string ActivityOptionText,
    string TimingOptionText,
    int ShortTitleMaxLength,
    string FinalAction,
    string FinalActionText,
    double WaitAfterUploadSeconds,
    double WaitAfterFinalActionSeconds,
    string EpisodeSelectionMode,
    int StartEpisodeIndex,
    int PublishCount,
    IReadOnlyList<int> EpisodeIndexes,
    string VideoUploadSelector)
{
    public IReadOnlyList<string> CustomVideoFiles { get; init; } = [];
    public IReadOnlyDictionary<string, string> VideoDescriptionMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool MergePublishEnabled { get; init; }
    public int MergePublishGroupSize { get; init; }
    public bool DeclareOriginal { get; init; }
    public bool ReplaceCoverWithLocalImage { get; init; }
    public string CoverImagePath { get; init; } = string.Empty;
    public string SystemHighlightDramaTitle { get; init; } = string.Empty;
    public string SystemHighlightPublishTargetMode { get; init; } = "count";
    public IReadOnlyList<string> SystemHighlightPublishVideoTypes { get; init; } = [];
    public bool SystemHighlightRegenerateAfterPublish { get; init; }
    public IReadOnlyList<string> SystemHighlightRegenerateVideoTypes { get; init; } = [];
    public string NewDramaMountTitle { get; init; } = string.Empty;
    public string NewDramaMountProjectDir { get; init; } = string.Empty;
    public string NewDramaMountResolvedTitle { get; init; } = string.Empty;
    public string NewDramaMountResolvedBookId { get; init; } = string.Empty;
    public bool AiDescriptionEnabled { get; init; }
    public bool AiDescriptionUseAsr { get; init; } = true;
    public bool AiDescriptionFallbackToOriginal { get; init; } = true;
    public bool AiDescriptionCacheEnabled { get; init; } = true;
    public int AiDescriptionRetryAttempts { get; init; } = 3;
    public int AiDescriptionTimeoutSeconds { get; init; } = 60;
    public string AiTextEndpoint { get; init; } = string.Empty;
    public string AiTextApiKey { get; init; } = string.Empty;
    public string AiTextModel { get; init; } = string.Empty;
    public string AiDescriptionAsrEngine { get; init; } = "volcengine";
    public string AiDescriptionAsrLanguage { get; init; } = "zh-CN";
    public string AiDescriptionVolcengineAppId { get; init; } = string.Empty;
    public string AiDescriptionVolcengineAccessToken { get; init; } = string.Empty;
    public string AiDescriptionLocalModelDir { get; init; } = string.Empty;
    public string AiDescriptionLocalVadPath { get; init; } = string.Empty;
    public bool AiDescriptionLocalUseItn { get; init; }
    public double AiDescriptionHybridMinCharsPerSec { get; init; } = 1.0;
    public bool PublishOriginalityEnabled { get; init; }
    public bool PublishOriginalityReuseAcrossRuns { get; init; } = true;
    public bool PublishOriginalityZoom { get; init; } = true;
    public bool PublishOriginalityColor { get; init; }
    public bool PublishOriginalitySpeed { get; init; }
    public bool PublishOriginalityFade { get; init; }
    public string PublishOriginalityStickerDir { get; init; } = string.Empty;
    public string FfmpegPath { get; init; } = "ffmpeg";
    public string FfprobePath { get; init; } = "ffprobe";
}
