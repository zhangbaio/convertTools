using System.Security.Cryptography;
using System.Text;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Common.Services;

public static class PublishAccountStorageKey
{
    public static string ForJob(PublishJob job)
    {
        var source = !string.IsNullOrWhiteSpace(job.AccountId)
            ? job.AccountId.Trim()
            : !string.IsNullOrWhiteSpace(job.AccountName) ? job.AccountName.Trim() : "default";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12].ToLowerInvariant();
    }
}
