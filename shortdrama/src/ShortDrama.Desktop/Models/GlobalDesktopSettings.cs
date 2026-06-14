namespace ShortDrama.Desktop.Models;

public sealed class GlobalDesktopSettings
{
    public string DramaSourceChain { get; set; } = "hgnew";
    public string DramaServiceOrderSearch { get; set; } = "hgnew,hglocal,pikachu";
    public string DramaServiceOrderDownload { get; set; } = "hgnew,hglocal,pikachu";
    public string DramaServiceOrderNewRelease { get; set; } = "hgnew,hglocal";
    public string DramaServiceOrderRanking { get; set; } = "hglocal,pikachu";
    public bool XingeEnabled { get; set; }
    public string XingeServerUrl { get; set; } = string.Empty;
    public string XingeUsername { get; set; } = string.Empty;
    public string XingePassword { get; set; } = string.Empty;
    public string XingeClientId { get; set; } = string.Empty;
    public string XingeClientToken { get; set; } = string.Empty;
    public string XingeUserRole { get; set; } = string.Empty;
    public string XingeClientName { get; set; } = string.Empty;
    public bool XingeWsEnabled { get; set; } = true;
    public int XingePollIntervalSeconds { get; set; } = 3;
    public bool XingeUploadLoginQr { get; set; } = true;
    public string HgnewAccount { get; set; } = string.Empty;
    public string HgnewPassword { get; set; } = string.Empty;
    public string HgnewUdid { get; set; } = string.Empty;
    public string HgnewClientVersion { get; set; } = "1.3.4";
    public string HongguoLocalBaseUrl { get; set; } = string.Empty;
    public string HongguoLocalApiKey { get; set; } = string.Empty;
    public string PikachuServerUrl { get; set; } = "http://8.138.192.128/start-prod-api";
    public string PikachuFanqieCookie { get; set; } = string.Empty;
    public string PikachuDramaType { get; set; } = "short";
    public string AiTextEndpoint { get; set; } = string.Empty;
    public string AiTextApiKey { get; set; } = string.Empty;
    public string AiTextModel { get; set; } = string.Empty;
    public string AiTextTimeoutSeconds { get; set; } = string.Empty;
    public string AiTextMaxBatchSize { get; set; } = string.Empty;
    public string AiTextSystemPrompt { get; set; } = string.Empty;
    public string AiTextBatchPrompt { get; set; } = string.Empty;
    public string AiTextRetryPrompt { get; set; } = string.Empty;
    public string ImageModelId { get; set; } = string.Empty;
    public string ImageModelApiKey { get; set; } = string.Empty;
    public string ImageModelEndpoint { get; set; } = string.Empty;
    public string ImageEditModelId { get; set; } = string.Empty;
    public string ImageEditApiKey { get; set; } = string.Empty;
    public string ImageEditEndpoint { get; set; } = string.Empty;
    public string ImageEditPath { get; set; } = string.Empty;
    public string PosterLayoutDetectPrompt { get; set; } = string.Empty;
    public string PosterInpaintPrompt { get; set; } = string.Empty;
    public string PosterInpaintSafeRetryPrompt { get; set; } = string.Empty;
    public string PosterGenerationPrompt { get; set; } = string.Empty;
    public string PosterGenerationSafeRetryPrompt { get; set; } = string.Empty;
    public string PosterNameSystemPrompt { get; set; } = string.Empty;
    public string PosterNameUserPrompt { get; set; } = string.Empty;
    public bool FeishuNotificationEnabled { get; set; }
    public string FeishuAppId { get; set; } = string.Empty;
    public string FeishuAppSecret { get; set; } = string.Empty;
    public string FeishuReceiveId { get; set; } = string.Empty;
    public string FeishuReceiveIdType { get; set; } = "chat_id";
    public bool FeishuNotifyOnStepStart { get; set; }
    public bool FeishuNotifyOnStepSuccess { get; set; } = true;
    public bool FeishuNotifyOnStepFailure { get; set; } = true;
    public bool FeishuNotifyOnQueueSummary { get; set; } = true;
    public bool FeishuNotifyOnLoginQr { get; set; } = true;
    public string FeishuNotifyStepKeysText { get; set; } = string.Empty;
}
