namespace ShortDrama.Desktop.Models;

public sealed class GlobalDesktopSettings
{
    public string DramaSourceChain { get; set; } = "hgnew";
    public string DramaServiceOrderSearch { get; set; } = "hgnew,hglocal,pikachu";
    public string DramaServiceOrderDownload { get; set; } = "hgnew,hglocal,pikachu";
    public string DramaServiceOrderNewRelease { get; set; } = "hgnew,hglocal";
    public string DramaServiceOrderRanking { get; set; } = "hglocal,pikachu";
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
}
