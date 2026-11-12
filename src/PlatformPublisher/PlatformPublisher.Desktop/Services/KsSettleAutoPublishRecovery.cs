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

    public const string DownloadFailureKind = "download";

    // 下载失败按整部剧上报，服务端再把勾选队列交回原账号。登录、额度和授权问题留空，沿用原来的归类。
    public static string FailureKindForPipeline(string? error)
    {
        var text = error ?? "";
        if (text.Contains("无法播放", StringComparison.Ordinal) ||
            text.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("没有可播放", StringComparison.Ordinal) ||
            text.Contains("下载失败", StringComparison.Ordinal) ||
            text.Contains("下载剧集失败", StringComparison.Ordinal) ||
            text.Contains("下载未完成", StringComparison.Ordinal))
            return DownloadFailureKind;
        return "";
    }

    public static Action Decide(string? serverStatus, bool markerActive, string? markerFinalStatus, bool projectExists, int recoveryCount = 0)
    {
        var status = (serverStatus ?? "").Trim().ToLowerInvariant();
        if (status is "success" or "failed" or "released")
            return Action.None;
        if (string.Equals(markerFinalStatus, "success", StringComparison.OrdinalIgnoreCase))
            return Action.ReportSuccess;
        // 已经失败过的本地项目不能在启动时自己拉起来。待重试和已经排进下一轮的重试，等服务器下发后再执行勾选队列。
        if (string.Equals(markerFinalStatus, "failed", StringComparison.OrdinalIgnoreCase))
        {
            if (status == "retry_wait")
                return Action.None;
            if (status == "dispatched" && recoveryCount >= 1)
                return Action.None;
            return Action.ReportFailed;
        }
        if (markerActive && projectExists)
            return Action.Resume;
        if (status is "accepted" or "running")
            return Action.ReportInterrupted;
        return Action.None;
    }
}
