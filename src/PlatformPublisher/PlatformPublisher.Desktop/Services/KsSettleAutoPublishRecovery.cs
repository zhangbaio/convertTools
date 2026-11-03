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
    }

    public static Action Decide(string? serverStatus, bool markerActive, string? markerFinalStatus, bool projectExists)
    {
        var status = (serverStatus ?? "").Trim().ToLowerInvariant();
        if (status is "success" or "failed" or "released")
            return Action.None;
        if (string.Equals(markerFinalStatus, "success", StringComparison.OrdinalIgnoreCase))
            return Action.ReportSuccess;
        if (markerActive && projectExists)
            return Action.Resume;
        if (status is "accepted" or "running")
            return Action.ReportInterrupted;
        return Action.None;
    }
}
