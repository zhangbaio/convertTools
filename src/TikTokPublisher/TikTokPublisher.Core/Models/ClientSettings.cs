namespace TikTokPublisher.Core.Models;

/// <summary>与 Python <c>ClientSettings</c> / <c>app_settings.client_settings</c> 对齐的全局配置。</summary>
public sealed class ClientSettings
{
    public const string DefaultHongguoClientVersion = "1.4.2";

    public string DramaSourceChain { get; set; } = "hgnew";
    public string DramaDownloadDefaultQuality { get; set; } = "1080P";
    public int DramaDownloadConcurrent { get; set; } = 5;
    public int DramaDownloadMaxParallelProjects { get; set; } = 1;
    public int DownloadFileSegments { get; set; } = 4;
    public int HongguoDownloadTimeoutSeconds { get; set; } = 60;
    public int HongguoEpisodeDownloadAttempts { get; set; } = 5;

    public string HgnewAccount { get; set; } = "";
    public string HgnewPassword { get; set; } = "";
    public string HgnewUdid { get; set; } = "";
    public string HgnewClientVersion { get; set; } = DefaultHongguoClientVersion;

    public string HghighAccount { get; set; } = "";
    public string HghighPassword { get; set; } = "";
    public string HghighDeviceId { get; set; } = "";
    public string HghighClientExe { get; set; } = "";

    public string HongguoLocalBaseUrl { get; set; } = "";
    public string HongguoLocalApiKey { get; set; } = "";
    public string HongguoLocalDownloadMode { get; set; } = "fast";
    public string HongguoLocalTranscodeEngine { get; set; } = "auto";

    public string PikachuServerUrl { get; set; } = "https://startvlog.cn/start-prod-api";
    public string PikachuFanqieCookie { get; set; } = "";
    public string PikachuDramaType { get; set; } = "short";
    public string PikachuDeviceId { get; set; } = "";
    public string PikachuClientVersion { get; set; } = "1.4.4";

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

    public string VideoTranslateEngine { get; set; } = "volc";
    public string VideoTranslateSourceLanguage { get; set; } = "en";
    public string VideoTranslateTargetLanguage { get; set; } = "zh";
    public string VideoTranslateVolcAccessKeyId { get; set; } = "";
    public string VideoTranslateVolcSecretAccessKey { get; set; } = "";
    public string VideoTranslateLlmBaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string VideoTranslateLlmApiKey { get; set; } = "";
    public string VideoTranslateLlmModel { get; set; } = "deepseek-chat";
    public string VideoTranslateFont { get; set; } = "微软雅黑";
    public int VideoTranslateFontSize { get; set; } = 50;
    public int VideoTranslateMarginV { get; set; } = 160;
    public bool VideoTranslateBilingual { get; set; }

    public string AiTextEndpoint { get; set; } = ClientSettingsDefaults.AiTextEndpoint;
    public string AiTextApiKey { get; set; } = "";
    public string AiTextModel { get; set; } = ClientSettingsDefaults.AiTextModel;
    public int AiTextTimeoutSeconds { get; set; } = ClientSettingsDefaults.AiTextTimeoutSeconds;
    public int AiTextMaxBatchSize { get; set; } = ClientSettingsDefaults.AiTextMaxBatchSize;
    public string TiktokRoleReferenceSelectionMode { get; set; } =
        ClientSettingsDefaults.TiktokRoleReferenceSelectionMode;
    public bool TiktokRoleReferenceAiFallbackEnabled { get; set; } =
        ClientSettingsDefaults.TiktokRoleReferenceAiFallbackEnabled;
    public string AiTagSystemPrompt { get; set; } = ClientSettingsDefaults.AiTagSystemPrompt;
    public string AiTagBatchPrompt { get; set; } = ClientSettingsDefaults.AiTagBatchPrompt;
    public string AiFullInfoSystemPrompt { get; set; } = ClientSettingsDefaults.AiFullInfoSystemPrompt;
    public string AiFullInfoBatchPrompt { get; set; } = ClientSettingsDefaults.AiFullInfoBatchPrompt;
    public string AiFullInfoRetryPrompt { get; set; } = ClientSettingsDefaults.AiFullInfoRetryPrompt;

    public string PosterMode { get; set; } = ClientSettingsDefaults.PosterMode;
    public string ImageProvider { get; set; } = ClientSettingsDefaults.ImageProvider;
    public string ImageModelId { get; set; } = ClientSettingsDefaults.ImageModelId;
    public string ImageModelApiKey { get; set; } = "";
    public string ImageModelEndpoint { get; set; } = ClientSettingsDefaults.ImageModelEndpoint;
    public string DoubaoImageResolution { get; set; } = ClientSettingsDefaults.DoubaoImageResolution;
    public string DoubaoImageRatio { get; set; } = ClientSettingsDefaults.DoubaoImageRatio;
    public string OfoxImage2ModelId { get; set; } = ClientSettingsDefaults.OfoxImage2ModelId;
    public string OfoxImage2ApiKey { get; set; } = "";
    public string OfoxImage2Endpoint { get; set; } = ClientSettingsDefaults.OfoxImage2Endpoint;
    public string OfoxImage2Quality { get; set; } = ClientSettingsDefaults.OfoxImage2Quality;
    public string OfoxImage2Size { get; set; } = ClientSettingsDefaults.OfoxImage2Size;
    public bool PosterTitleVerifyEnabled { get; set; } = ClientSettingsDefaults.PosterTitleVerifyEnabled;
    public string PosterTitleVerifyMode { get; set; } = ClientSettingsDefaults.PosterTitleVerifyMode;
    public int PosterTitleVerifyAiRetryCount { get; set; } = ClientSettingsDefaults.PosterTitleVerifyAiRetryCount;
    public int FrameExtractEpisodeIndex { get; set; } = ClientSettingsDefaults.FrameExtractEpisodeIndex;
    public double FrameExtractTime { get; set; } = ClientSettingsDefaults.FrameExtractTime;
    public string FrameExtractNeighborOffsetsSeconds { get; set; } = ClientSettingsDefaults.FrameExtractNeighborOffsetsSeconds;
    public string FrameExtractFallbackPercents { get; set; } = ClientSettingsDefaults.FrameExtractFallbackPercents;
    public string FrameCoverPrompt { get; set; } = ClientSettingsDefaults.FrameCoverPrompt;
    public string PosterLayoutDetectPrompt { get; set; } = ClientSettingsDefaults.PosterLayoutDetectPrompt;
    public string PosterInpaintPrompt { get; set; } = ClientSettingsDefaults.PosterInpaintPrompt;
    public string PosterInpaintSafeRetryPrompt { get; set; } = ClientSettingsDefaults.PosterInpaintSafeRetryPrompt;
    public string PosterGenerationPrompt { get; set; } = ClientSettingsDefaults.PosterGenerationPrompt;
    public string PosterGenerationSafeRetryPrompt { get; set; } = ClientSettingsDefaults.PosterGenerationSafeRetryPrompt;
    public string PosterNameSystemPrompt { get; set; } = ClientSettingsDefaults.PosterNameSystemPrompt;
    public string PosterNameUserPrompt { get; set; } = ClientSettingsDefaults.PosterNameUserPrompt;
    public string TiktokProjectImageGenerationMode { get; set; } = ClientSettingsDefaults.TiktokProjectImageGenerationMode;
    public string TiktokProjectImageTemplateRoot { get; set; } = "";
    public string TiktokProjectImageTemplateId { get; set; } = ClientSettingsDefaults.TiktokProjectImageTemplateId;
    public int TiktokProjectImageCount { get; set; } = ClientSettingsDefaults.TiktokProjectImageCount;
    public int TiktokProjectImageRenderEpisodeLimit { get; set; } = ClientSettingsDefaults.TiktokProjectImageRenderEpisodeLimit;
    public string TiktokProjectImageSubtitleAiMode { get; set; } = ClientSettingsDefaults.TiktokProjectImageSubtitleAiMode;
    public string TiktokProjectImageFableCutRoot { get; set; } = ClientSettingsDefaults.TiktokProjectImageFableCutRoot;
    public int TiktokProjectImageFableCutClipCount { get; set; } = ClientSettingsDefaults.TiktokProjectImageFableCutClipCount;
    public string TiktokProofTemplateDocxPath { get; set; } = ClientSettingsDefaults.TiktokProofTemplateDocxPath;
    public string TiktokProofWpsPath { get; set; } = ClientSettingsDefaults.TiktokProofWpsPath;
    // Legacy global values retained only for one-time/account-unmigrated compatibility.
    // New runtime configuration belongs to TikTokAccountProfile.
    public string TiktokProofDeclarantCompanyName { get; set; } = "";
    public string TiktokProofSealPath { get; set; } = "";
    public string TiktokProofPdfRenderer { get; set; } = ClientSettingsDefaults.TiktokProofPdfRenderer;
    public bool TiktokProofKeepDocx { get; set; } = ClientSettingsDefaults.TiktokProofKeepDocx;

    public string LastDownloadWorkspace { get; set; } = "";
    public string ArchiveRootDir { get; set; } = "";
    public string AuthServerUrl { get; set; } = "";
    public string AuthAccount { get; set; } = "";
    public string AuthPassword { get; set; } = "";
    public string AuthLastUsername { get; set; } = "";
    public string AuthLastLoginAt { get; set; } = "";
    public bool TiktokExcelAutoExportEnabled { get; set; } = true;
    public bool ManagementDedupEnabled { get; set; }
    public string ManagementDedupScope { get; set; } = "tiktok_username";
    public bool TiktokAllowOverLimitUploadImport { get; set; } = ClientSettingsDefaults.TiktokAllowOverLimitUploadImport;
    public int TiktokOverLimitDownloadEpisodeCount { get; set; } = ClientSettingsDefaults.TiktokOverLimitDownloadEpisodeCount;
    public bool FeishuCommandEnabled { get; set; }
    public string FeishuCommandAppId { get; set; } = "";
    public string FeishuCommandAppSecret { get; set; } = "";
    public string FeishuCommandBotName { get; set; } = "";
    public string FeishuCommandBotAliases { get; set; } = "";
    public bool FeishuCommandRequireBotMention { get; set; } = true;
    public string FeishuCommandAllowedChatIds { get; set; } = "";
    public bool FeishuCommandDirectMessageEnabled { get; set; } = true;
    public string FeishuCommandAllowedUserIds { get; set; } = "";
    public string FeishuCommandDefaultWorkspace { get; set; } = "";
    public bool FeishuCommandReplyEnabled { get; set; } = true;
    public int FeishuCommandCommandTtlSeconds { get; set; } = 60;
    public string FeishuCommandHelpText { get; set; } = ClientSettingsDefaults.FeishuCommandHelpText;
    public string FeishuTiktokUploadEnabledStepsJson { get; set; } = "";
    public bool FeishuTiktokUploadAutoArchiveAfterUpload { get; set; }
    public bool FeishuTiktokUploadForceRerunCompletedSteps { get; set; }
    public bool FeishuTiktokUploadPreferUploadWhenReady { get; set; }
    public bool XingeRemoteEnabled { get; set; }
    public string XingeServerUrl { get; set; } = "";
    public string XingeAccount { get; set; } = "";
    public string XingePassword { get; set; } = "";
    public string XingeClientId { get; set; } = "";
    public string XingeClientToken { get; set; } = "";
    public string XingeCredentialFingerprint { get; set; } = "";
    public string XingeClientName { get; set; } = "TikTokPublisher";
    public int XingePollIntervalSeconds { get; set; } = 3;

    public ClientSettings Clone() => new()
    {
        DramaSourceChain = DramaSourceChain,
        DramaDownloadDefaultQuality = DramaDownloadDefaultQuality,
        DramaDownloadConcurrent = DramaDownloadConcurrent,
        DramaDownloadMaxParallelProjects = DramaDownloadMaxParallelProjects,
        DownloadFileSegments = DownloadFileSegments,
        HongguoDownloadTimeoutSeconds = HongguoDownloadTimeoutSeconds,
        HongguoEpisodeDownloadAttempts = HongguoEpisodeDownloadAttempts,
        HgnewAccount = HgnewAccount,
        HgnewPassword = HgnewPassword,
        HgnewUdid = HgnewUdid,
        HgnewClientVersion = HgnewClientVersion,
        HghighAccount = HghighAccount,
        HghighPassword = HghighPassword,
        HghighDeviceId = HghighDeviceId,
        HghighClientExe = HghighClientExe,
        HongguoLocalBaseUrl = HongguoLocalBaseUrl,
        HongguoLocalApiKey = HongguoLocalApiKey,
        HongguoLocalDownloadMode = HongguoLocalDownloadMode,
        HongguoLocalTranscodeEngine = HongguoLocalTranscodeEngine,
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
        VideoTranslateEngine = VideoTranslateEngine,
        VideoTranslateSourceLanguage = VideoTranslateSourceLanguage,
        VideoTranslateTargetLanguage = VideoTranslateTargetLanguage,
        VideoTranslateVolcAccessKeyId = VideoTranslateVolcAccessKeyId,
        VideoTranslateVolcSecretAccessKey = VideoTranslateVolcSecretAccessKey,
        VideoTranslateLlmBaseUrl = VideoTranslateLlmBaseUrl,
        VideoTranslateLlmApiKey = VideoTranslateLlmApiKey,
        VideoTranslateLlmModel = VideoTranslateLlmModel,
        VideoTranslateFont = VideoTranslateFont,
        VideoTranslateFontSize = VideoTranslateFontSize,
        VideoTranslateMarginV = VideoTranslateMarginV,
        VideoTranslateBilingual = VideoTranslateBilingual,
        AiTextEndpoint = AiTextEndpoint,
        AiTextApiKey = AiTextApiKey,
        AiTextModel = AiTextModel,
        AiTextTimeoutSeconds = AiTextTimeoutSeconds,
        AiTextMaxBatchSize = AiTextMaxBatchSize,
        TiktokRoleReferenceSelectionMode = TiktokRoleReferenceSelectionMode,
        TiktokRoleReferenceAiFallbackEnabled = TiktokRoleReferenceAiFallbackEnabled,
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
        PosterTitleVerifyAiRetryCount = PosterTitleVerifyAiRetryCount,
        FrameExtractEpisodeIndex = FrameExtractEpisodeIndex,
        FrameExtractTime = FrameExtractTime,
        FrameExtractNeighborOffsetsSeconds = FrameExtractNeighborOffsetsSeconds,
        FrameExtractFallbackPercents = FrameExtractFallbackPercents,
        FrameCoverPrompt = FrameCoverPrompt,
        PosterLayoutDetectPrompt = PosterLayoutDetectPrompt,
        PosterInpaintPrompt = PosterInpaintPrompt,
        PosterInpaintSafeRetryPrompt = PosterInpaintSafeRetryPrompt,
        PosterGenerationPrompt = PosterGenerationPrompt,
        PosterGenerationSafeRetryPrompt = PosterGenerationSafeRetryPrompt,
        PosterNameSystemPrompt = PosterNameSystemPrompt,
        PosterNameUserPrompt = PosterNameUserPrompt,
        TiktokProjectImageGenerationMode = TiktokProjectImageGenerationMode,
        TiktokProjectImageTemplateRoot = TiktokProjectImageTemplateRoot,
        TiktokProjectImageTemplateId = TiktokProjectImageTemplateId,
        TiktokProjectImageCount = TiktokProjectImageCount,
        TiktokProjectImageRenderEpisodeLimit = TiktokProjectImageRenderEpisodeLimit,
        TiktokProjectImageSubtitleAiMode = TiktokProjectImageSubtitleAiMode,
        TiktokProjectImageFableCutRoot = TiktokProjectImageFableCutRoot,
        TiktokProjectImageFableCutClipCount = TiktokProjectImageFableCutClipCount,
        TiktokProofTemplateDocxPath = TiktokProofTemplateDocxPath,
        TiktokProofWpsPath = TiktokProofWpsPath,
        TiktokProofDeclarantCompanyName = TiktokProofDeclarantCompanyName,
        TiktokProofSealPath = TiktokProofSealPath,
        TiktokProofPdfRenderer = TiktokProofPdfRenderer,
        TiktokProofKeepDocx = TiktokProofKeepDocx,
        LastDownloadWorkspace = LastDownloadWorkspace,
        ArchiveRootDir = ArchiveRootDir,
        AuthServerUrl = AuthServerUrl,
        AuthAccount = AuthAccount,
        AuthPassword = AuthPassword,
        AuthLastUsername = AuthLastUsername,
        AuthLastLoginAt = AuthLastLoginAt,
        TiktokExcelAutoExportEnabled = TiktokExcelAutoExportEnabled,
        ManagementDedupEnabled = ManagementDedupEnabled,
        ManagementDedupScope = ManagementDedupScope,
        TiktokAllowOverLimitUploadImport = TiktokAllowOverLimitUploadImport,
        TiktokOverLimitDownloadEpisodeCount = TiktokOverLimitDownloadEpisodeCount,
        FeishuCommandEnabled = FeishuCommandEnabled,
        FeishuCommandAppId = FeishuCommandAppId,
        FeishuCommandAppSecret = FeishuCommandAppSecret,
        FeishuCommandBotName = FeishuCommandBotName,
        FeishuCommandBotAliases = FeishuCommandBotAliases,
        FeishuCommandRequireBotMention = FeishuCommandRequireBotMention,
        FeishuCommandAllowedChatIds = FeishuCommandAllowedChatIds,
        FeishuCommandDirectMessageEnabled = FeishuCommandDirectMessageEnabled,
        FeishuCommandAllowedUserIds = FeishuCommandAllowedUserIds,
        FeishuCommandDefaultWorkspace = FeishuCommandDefaultWorkspace,
        FeishuCommandReplyEnabled = FeishuCommandReplyEnabled,
        FeishuCommandCommandTtlSeconds = FeishuCommandCommandTtlSeconds,
        FeishuCommandHelpText = FeishuCommandHelpText,
        FeishuTiktokUploadEnabledStepsJson = FeishuTiktokUploadEnabledStepsJson,
        FeishuTiktokUploadAutoArchiveAfterUpload = FeishuTiktokUploadAutoArchiveAfterUpload,
        FeishuTiktokUploadForceRerunCompletedSteps = FeishuTiktokUploadForceRerunCompletedSteps,
        FeishuTiktokUploadPreferUploadWhenReady = FeishuTiktokUploadPreferUploadWhenReady,
        XingeRemoteEnabled = XingeRemoteEnabled,
        XingeServerUrl = XingeServerUrl,
        XingeAccount = XingeAccount,
        XingePassword = XingePassword,
        XingeClientId = XingeClientId,
        XingeClientToken = XingeClientToken,
        XingeCredentialFingerprint = XingeCredentialFingerprint,
        XingeClientName = XingeClientName,
        XingePollIntervalSeconds = XingePollIntervalSeconds,
    };
}
