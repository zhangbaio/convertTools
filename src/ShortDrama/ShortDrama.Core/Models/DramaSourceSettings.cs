namespace ShortDrama.Core.Models;

/// <summary>短剧数据链路配置（从 Desktop GlobalConfigSnapshot / TikTok ClientSettings 投影）。</summary>
public sealed record DramaSourceSettings
{
    public string DramaSourceChain { get; init; } = "hgnew";
    public string DramaServiceOrderSearch { get; init; } = "hgnew,hglocal,pikachu";
    public string DramaServiceOrderDownload { get; init; } = "hgnew,hglocal,pikachu";
    public string DramaServiceOrderNewRelease { get; init; } = "hgnew,hglocal";
    public string DramaServiceOrderRanking { get; init; } = "hglocal,pikachu";

    public string HgnewAccount { get; init; } = "";
    public string HgnewPassword { get; init; } = "";
    public string HgnewUdid { get; init; } = "";
    public string HgnewClientVersion { get; init; } = "1.3.8";
    public string HongguoDownloadTimeoutSeconds { get; init; } = "60";
    public string HongguoEpisodeDownloadAttempts { get; init; } = "5";

    public string HongguoLocalBaseUrl { get; init; } = "";
    public string HongguoLocalApiKey { get; init; } = "";

    public string PikachuServerUrl { get; init; } = "https://startvlog.cn/start-prod-api";
    public string PikachuFanqieCookie { get; init; } = "";
    public string PikachuDramaType { get; init; } = "short";
    public string PikachuDeviceId { get; init; } = "";
    public string PikachuClientVersion { get; init; } = "1.4.4";

    public DramaSourceSettings WithPikachuDeviceId(string deviceId) =>
        this with { PikachuDeviceId = deviceId.Trim() };
}
