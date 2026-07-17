namespace ShortDrama.Desktop.Models;

public sealed class GlobalDesktopSettings
{
    public string DramaSourceChain { get; set; } = "hgnew";
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
    public string HgnewClientVersion { get; set; } = "1.5.0";
    public int HongguoDownloadTimeoutSeconds { get; set; } = 60;
    public int HongguoEpisodeDownloadAttempts { get; set; } = 5;
    public string HongguoLocalBaseUrl { get; set; } = string.Empty;
    public string HongguoLocalApiKey { get; set; } = string.Empty;
    public string HongguoLocalDownloadMode { get; set; } = "fast";
    public string HongguoLocalTranscodeEngine { get; set; } = "auto";
    public string PikachuServerUrl { get; set; } = "https://startvlog.cn/start-prod-api";
    public string PikachuFanqieCookie { get; set; } = string.Empty;
    public string PikachuDramaType { get; set; } = "short";
    public string PikachuDeviceId { get; set; } = string.Empty;
    public string PikachuClientVersion { get; set; } = "1.4.4";
    public string AiTextEndpoint { get; set; } = string.Empty;
    public string AiTextApiKey { get; set; } = string.Empty;
    public string AiTextModel { get; set; } = string.Empty;
    public string AiTextTimeoutSeconds { get; set; } = string.Empty;
    public string AiTextMaxBatchSize { get; set; } = string.Empty;
    public string AiTextSystemPrompt { get; set; } = string.Empty;
    public string AiTextBatchPrompt { get; set; } = string.Empty;
    public string AiTextRetryPrompt { get; set; } = string.Empty;
    public string AiTitleSystemPrompt { get; set; } = string.Empty;
    public string AiTitleBatchPrompt { get; set; } = string.Empty;
    public string AiTagSystemPrompt { get; set; } = string.Empty;
    public string AiTagBatchPrompt { get; set; } = string.Empty;
    public string AiFullInfoSystemPrompt { get; set; } = string.Empty;
    public string AiFullInfoBatchPrompt { get; set; } = string.Empty;
    public string AiFullInfoRetryPrompt { get; set; } = string.Empty;
    public string ImageModelId { get; set; } = string.Empty;
    public string ImageModelApiKey { get; set; } = string.Empty;
    public string ImageModelEndpoint { get; set; } = string.Empty;
    public string ImageEditModelId { get; set; } = string.Empty;
    public string ImageEditApiKey { get; set; } = string.Empty;
    public string ImageEditEndpoint { get; set; } = string.Empty;
    public string ImageEditPath { get; set; } = string.Empty;
    public string FrameCoverPrompt { get; set; } = string.Empty;
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
    public string LastMaterialClipWorkspace { get; set; } = string.Empty;
    public string MaterialClipAsrProvider { get; set; } = "volcengine_stt";
    public string MaterialClipAsrLanguage { get; set; } = "zh-CN";
    public string MaterialClipVolcengineAppId { get; set; } = string.Empty;
    public string MaterialClipVolcengineAccessToken { get; set; } = string.Empty;
    public string MaterialClipDoubaoAppId { get; set; } = string.Empty;
    public string MaterialClipDoubaoAccessToken { get; set; } = string.Empty;
    // ASR 引擎：volcengine(在线) / local(本地 SenseVoice 离线) / hybrid(本地优先+火山复核)
    public string MaterialClipAsrEngine { get; set; } = "volcengine";
    public string MaterialClipAsrLocalModel { get; set; } = "sensevoice"; // sensevoice / paraformer
    public string MaterialClipAsrLocalModelDir { get; set; } = string.Empty; // 空=自动在 models/ 下按模型名查找
    public string MaterialClipAsrLocalVadPath { get; set; } = string.Empty; // 空=自动查找 silero_vad.onnx
    public bool MaterialClipAsrLocalUseItn { get; set; } // SenseVoice 数字逆归一化；默认关
    public double MaterialClipAsrHybridMinCharsPerSec { get; set; } = 1.0d; // 混合判据：每秒语音识别字数低于此值改用火山
    public string MaterialClipMode { get; set; } = "multi_video_merge";
    public string MaterialClipTargetDurationMode { get; set; } = "adaptive_range";
    public int MaterialClipTargetDurationSec { get; set; } = 30;
    public double MaterialClipTargetDurationRatioPercent { get; set; } = 8.0d;
    public int MaterialClipMinOutputDurationSec { get; set; }
    public int MaterialClipMaxOutputDurationSec { get; set; } = 45;
    public int MaterialClipPerEpisodeTopN { get; set; } = 2;
    public bool MaterialClipEnableLlm { get; set; }
    public int MaterialClipSplitClipLimit { get; set; } = 4;
}
