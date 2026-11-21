using System.Security.Cryptography;
using System.Text;
using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Weixin.Publishing;

public sealed class WeixinSeriesReviewStore(string root)
{
    public bool HasReachedReview(string accountId, string projectDirectory) =>
        File.Exists(GetPath(accountId, projectDirectory));

    public void RecordReachedReview(string accountId, string projectDirectory)
    {
        Directory.CreateDirectory(root);
        var path = GetPath(accountId, projectDirectory);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")));
        stream.Flush(flushToDisk: true);
    }

    public static WeixinSeriesReviewStore CreateDefault() =>
        new(Path.Combine(PlatformPublisherPaths.DataRoot, "weixin", "series-review"));

    private string GetPath(string accountId, string projectDirectory)
    {
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        var identity = $"{accountId}\n{project}".ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(root, key + ".review");
    }
}
