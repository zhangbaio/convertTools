namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 上传后自动归档：这次执行可以不勾上传剧集。只要上传已经完成，就归档。
/// </summary>
public static class KuaishouAutoArchivePolicy
{
    public static bool ShouldArchive(bool autoArchiveEnabled, bool uploadCompleted) =>
        autoArchiveEnabled && uploadCompleted;

    public static bool UploadCompleted(bool stepSucceeded, string? artifactStatus) =>
        stepSucceeded || (artifactStatus?.StartsWith("已完成", StringComparison.Ordinal) ?? false);
}
