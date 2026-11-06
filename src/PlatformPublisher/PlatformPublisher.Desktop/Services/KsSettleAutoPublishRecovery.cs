namespace PlatformPublisher.Desktop.Services;

public static class KsSettleAutoPublishRecovery
{
    public const string MarkerFileName = ".xinge-ks-settle.json";

    public enum Action
    {
        None,
        Resume,
        ReportSuccess,
        ReportInterrupted,
        ReportFailed,
    }

    public static Action Decide(string? serverStatus, bool markerActive, string? markerFinalStatus, bool projectExists)
    {
        var status = (serverStatus ?? "").Trim().ToLowerInvariant();
        if (status is "success" or "failed" or "released")
            return Action.None;
        if (string.Equals(markerFinalStatus, "success", StringComparison.OrdinalIgnoreCase))
            return Action.ReportSuccess;
        // 已经失败过的本地项目不能再拉起来。否则下载账号失效时，启动后会每隔几分钟重跑同一部，界面一直停住。
        if (string.Equals(markerFinalStatus, "failed", StringComparison.OrdinalIgnoreCase))
            return Action.ReportFailed;
        if (markerActive && projectExists)
            return Action.Resume;
        if (status is "accepted" or "running")
            return Action.ReportInterrupted;
        return Action.None;
    }
}
