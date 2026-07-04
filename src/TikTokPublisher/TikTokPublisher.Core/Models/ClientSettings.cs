namespace TikTokPublisher.Core.Models;

/// <summary>与 Python <c>ClientSettings</c> / <c>app_settings.client_settings</c> 对齐的全局配置。</summary>
public sealed class ClientSettings
{
    public const string DefaultHongguoClientVersion = "1.3.8";

    public string DramaSourceChain { get; set; } = "hgnew";
    public string DramaDownloadDefaultQuality { get; set; } = "1080P";
    public int DramaDownloadConcurrent { get; set; } = 3;
    public int HongguoDownloadTimeoutSeconds { get; set; } = 60;
    public int HongguoEpisodeDownloadAttempts { get; set; } = 5;

    public string HgnewAccount { get; set; } = "";
    public string HgnewPassword { get; set; } = "";
    public string HgnewUdid { get; set; } = "";
    public string HgnewClientVersion { get; set; } = DefaultHongguoClientVersion;

    public string Hongguo52ApiKey { get; set; } = "";
    public string Hongguo52ApiBaseUrl { get; set; } = "https://www.52api.cn/api/hg_duanju";
    public string Hongguo52ApiSearchType { get; set; } = "search";

    public string HongguoLocalBaseUrl { get; set; } = "";
    public string HongguoLocalApiKey { get; set; } = "";

    public string PikachuServerUrl { get; set; } = "http://8.138.192.128/start-prod-api";
    public string PikachuFanqieCookie { get; set; } = "";
    public string PikachuDramaType { get; set; } = "short";
    public string PikachuDeviceId { get; set; } = "";
    public string PikachuClientVersion { get; set; } = "1.4.2";

    public string TiktokSilenceAsrEngine { get; set; } = "local";
    public string TiktokSilenceLocalModelDir { get; set; } = "";
    public string TiktokSilenceLocalVadPath { get; set; } = "";
    public double TiktokSilenceHybridLowSeconds { get; set; } = 15.0;
    public double TiktokSilenceHybridHighSeconds { get; set; } = 25.0;
    public string TiktokSilenceAsrAppId { get; set; } = "";
    public string TiktokSilenceAsrAccessToken { get; set; } = "";
    public int TiktokSilenceAsrThresholdSeconds { get; set; } = 20;
    public string TiktokSilenceRepairMode { get; set; } = "auto";
    public double TiktokSilenceRepairTargetSeconds { get; set; } = 17.0;
    public double TiktokSilenceRepairMaxSpeed { get; set; } = 2.0;
    public bool TiktokSilenceRepairBlocking { get; set; }
    public int TiktokSilenceDetectConcurrency { get; set; } = 5;
    public int TiktokMaterialValidateConcurrency { get; set; } = 4;
    public string TiktokSilenceAsrLanguage { get; set; } = "zh-CN";
    public bool TiktokManualInterventionOnSingleFailure { get; set; } = true;

    public string AiTextEndpoint { get; set; } = "https://ark.cn-beijing.volces.com/api/v3";
    public string AiTextApiKey { get; set; } = "";
    public string AiTextModel { get; set; } = "doubao-seed-2-0-lite-260215";
    public int AiTextTimeoutSeconds { get; set; } = 120;
    public int AiTextMaxBatchSize { get; set; } = 10;
    public string AiTagSystemPrompt { get; set; } = "";
    public string AiTagBatchPrompt { get; set; } = "";
    public string AiFullInfoSystemPrompt { get; set; } = "";
    public string AiFullInfoBatchPrompt { get; set; } = "";
    public string AiFullInfoRetryPrompt { get; set; } = "";

    public string PosterMode { get; set; } = "original";
    public string ImageProvider { get; set; } = "doubao";
    public string ImageModelId { get; set; } = "doubao-seedream-5-0-lite-260128";
    public string ImageModelApiKey { get; set; } = "";
    public string ImageModelEndpoint { get; set; } = "";
    public string DoubaoImageResolution { get; set; } = "2K";
    public string DoubaoImageRatio { get; set; } = "3:4";
    public string OfoxImage2ModelId { get; set; } = "openai/gpt-image-2";
    public string OfoxImage2ApiKey { get; set; } = "";
    public string OfoxImage2Endpoint { get; set; } = "https://api.ofox.ai/v1";
    public string OfoxImage2Quality { get; set; } = "medium";
    public string OfoxImage2Size { get; set; } = "auto";
    public bool PosterTitleVerifyEnabled { get; set; } = true;
    public string PosterTitleVerifyMode { get; set; } = "fallback_repaint";
    public string FrameCoverPrompt { get; set; } = "";
    public string PosterLayoutDetectPrompt { get; set; } = "";
    public string PosterInpaintPrompt { get; set; } = "";
    public string PosterInpaintSafeRetryPrompt { get; set; } = "";
    public string PosterGenerationPrompt { get; set; } = "";
    public string PosterGenerationSafeRetryPrompt { get; set; } = "";
    public string PosterNameSystemPrompt { get; set; } = "";
    public string PosterNameUserPrompt { get; set; } = "";

    public string LastDownloadWorkspace { get; set; } = "";
    public string ArchiveRootDir { get; set; } = "";
    public string AuthServerUrl { get; set; } = "";
    public string AuthAccount { get; set; } = "";
    public string AuthPassword { get; set; } = "";
    public bool TiktokExcelAutoExportEnabled { get; set; } = true;
    public bool ManagementDedupEnabled { get; set; }
    public string ManagementDedupScope { get; set; } = "tiktok_username";

    public ClientSettings Clone() => new()
    {
        DramaSourceChain = DramaSourceChain,
        DramaDownloadDefaultQuality = DramaDownloadDefaultQuality,
        DramaDownloadConcurrent = DramaDownloadConcurrent,
        HongguoDownloadTimeoutSeconds = HongguoDownloadTimeoutSeconds,
        HongguoEpisodeDownloadAttempts = HongguoEpisodeDownloadAttempts,
        HgnewAccount = HgnewAccount,
        HgnewPassword = HgnewPassword,
        HgnewUdid = HgnewUdid,
        HgnewClientVersion = HgnewClientVersion,
        Hongguo52ApiKey = Hongguo52ApiKey,
        Hongguo52ApiBaseUrl = Hongguo52ApiBaseUrl,
        Hongguo52ApiSearchType = Hongguo52ApiSearchType,
        HongguoLocalBaseUrl = HongguoLocalBaseUrl,
        HongguoLocalApiKey = HongguoLocalApiKey,
        PikachuServerUrl = PikachuServerUrl,
        PikachuFanqieCookie = PikachuFanqieCookie,
        PikachuDramaType = PikachuDramaType,
        PikachuDeviceId = PikachuDeviceId,
        PikachuClientVersion = PikachuClientVersion,
        TiktokSilenceAsrEngine = TiktokSilenceAsrEngine,
        TiktokSilenceLocalModelDir = TiktokSilenceLocalModelDir,
        TiktokSilenceLocalVadPath = TiktokSilenceLocalVadPath,
        TiktokSilenceHybridLowSeconds = TiktokSilenceHybridLowSeconds,
        TiktokSilenceHybridHighSeconds = TiktokSilenceHybridHighSeconds,
        TiktokSilenceAsrAppId = TiktokSilenceAsrAppId,
        TiktokSilenceAsrAccessToken = TiktokSilenceAsrAccessToken,
        TiktokSilenceAsrThresholdSeconds = TiktokSilenceAsrThresholdSeconds,
        TiktokSilenceRepairMode = TiktokSilenceRepairMode,
        TiktokSilenceRepairTargetSeconds = TiktokSilenceRepairTargetSeconds,
        TiktokSilenceRepairMaxSpeed = TiktokSilenceRepairMaxSpeed,
        TiktokSilenceRepairBlocking = TiktokSilenceRepairBlocking,
        TiktokSilenceDetectConcurrency = TiktokSilenceDetectConcurrency,
        TiktokMaterialValidateConcurrency = TiktokMaterialValidateConcurrency,
        TiktokSilenceAsrLanguage = TiktokSilenceAsrLanguage,
        TiktokManualInterventionOnSingleFailure = TiktokManualInterventionOnSingleFailure,
        AiTextEndpoint = AiTextEndpoint,
        AiTextApiKey = AiTextApiKey,
        AiTextModel = AiTextModel,
        AiTextTimeoutSeconds = AiTextTimeoutSeconds,
        AiTextMaxBatchSize = AiTextMaxBatchSize,
        AiTagSystemPrompt = AiTagSystemPrompt,
        AiTagBatchPrompt = AiTagBatchPrompt,
        AiFullInfoSystemPrompt = AiFullInfoSystemPrompt,
        AiFullInfoBatchPrompt = AiFullInfoBatchPrompt,
        AiFullInfoRetryPrompt = AiFullInfoRetryPrompt,
        PosterMode = PosterMode,
        ImageProvider = ImageProvider,
        ImageModelId = ImageModelId,
        ImageModelApiKey = ImageModelApiKey,
        ImageModelEndpoint = ImageModelEndpoint,
        DoubaoImageResolution = DoubaoImageResolution,
        DoubaoImageRatio = DoubaoImageRatio,
        OfoxImage2ModelId = OfoxImage2ModelId,
        OfoxImage2ApiKey = OfoxImage2ApiKey,
        OfoxImage2Endpoint = OfoxImage2Endpoint,
        OfoxImage2Quality = OfoxImage2Quality,
        OfoxImage2Size = OfoxImage2Size,
        PosterTitleVerifyEnabled = PosterTitleVerifyEnabled,
        PosterTitleVerifyMode = PosterTitleVerifyMode,
        FrameCoverPrompt = FrameCoverPrompt,
        PosterLayoutDetectPrompt = PosterLayoutDetectPrompt,
        PosterInpaintPrompt = PosterInpaintPrompt,
        PosterInpaintSafeRetryPrompt = PosterInpaintSafeRetryPrompt,
        PosterGenerationPrompt = PosterGenerationPrompt,
        PosterGenerationSafeRetryPrompt = PosterGenerationSafeRetryPrompt,
        PosterNameSystemPrompt = PosterNameSystemPrompt,
        PosterNameUserPrompt = PosterNameUserPrompt,
        LastDownloadWorkspace = LastDownloadWorkspace,
        ArchiveRootDir = ArchiveRootDir,
        AuthServerUrl = AuthServerUrl,
        AuthAccount = AuthAccount,
        AuthPassword = AuthPassword,
        TiktokExcelAutoExportEnabled = TiktokExcelAutoExportEnabled,
        ManagementDedupEnabled = ManagementDedupEnabled,
        ManagementDedupScope = ManagementDedupScope,
    };
}
