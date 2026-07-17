namespace ShortDrama.Core.Models;

public sealed record UploadRemuxResult(
    bool Ok,
    int TotalFiles,
    int RemuxedFiles,
    int SkippedFiles,
    IReadOnlyList<string> Failures)
{
    public string Message =>
        Ok
            ? $"无损重封装完成：成功 {RemuxedFiles}，跳过 {SkippedFiles}"
            : Failures.Count > 0
                ? Failures[0]
                : "无损重封装失败";
}
