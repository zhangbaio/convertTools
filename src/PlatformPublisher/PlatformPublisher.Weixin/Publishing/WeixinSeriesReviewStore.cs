using System.Security.Cryptography;
using System.Text;
using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Weixin.Publishing;

public sealed class WeixinSeriesReviewStore(string root)
{
    private const string Confirmed = "已核对为已提审";

    public bool HasReachedReview(string accountId, string projectDirectory) =>
        File.Exists(GetPath(accountId, projectDirectory));

    public bool HasConfirmedSubmission(string accountId, string projectDirectory)
    {
        var path = GetPath(accountId, projectDirectory);
        return File.Exists(path) && File.ReadAllText(path) == Confirmed;
    }

    internal bool ResolveReview(string accountId, string projectDirectory, bool submitted)
    {
        var path = GetPath(accountId, projectDirectory);
        if (!File.Exists(path) || File.ReadAllText(path) == Confirmed)
            return false;
        if (submitted)
            Write(path, Confirmed);
        else
            File.Delete(path);
        return true;
    }

    public void RecordReachedReview(string accountId, string projectDirectory)
    {
        Directory.CreateDirectory(root);
        Write(GetPath(accountId, projectDirectory), DateTimeOffset.UtcNow.ToString("O"));
    }

    private static void Write(string path, string value)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes(value));
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
