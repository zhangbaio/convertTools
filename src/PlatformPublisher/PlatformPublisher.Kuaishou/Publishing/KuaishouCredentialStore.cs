using System.Text;
using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouCredentials(
    string AppSecret,
    string AuthCode,
    string AccessToken,
    string RefreshToken);

public sealed class KuaishouCredentialStore
{
    private readonly ISecureBlobStore _blobStore;
    private readonly IDataProtector _protector;

    public KuaishouCredentialStore(ISecureBlobStore blobStore, IDataProtector protector)
    {
        _blobStore = blobStore;
        _protector = protector;
    }

    public KuaishouCredentials Load(string accountId, PublishPlatform platform)
    {
        var key = Key(accountId, platform);
        try
        {
            var encrypted = _blobStore.Load(key, accountId);
            if (encrypted is null) return new("", "", "", "");
            var json = Encoding.UTF8.GetString(_protector.Unprotect(encrypted));
            return JsonSerializer.Deserialize<KuaishouCredentials>(json) ?? new("", "", "", "");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("快手凭据解密失败，请重新填写并保存。", ex);
        }
    }

    public void Save(string accountId, PublishPlatform platform, KuaishouCredentials credentials)
    {
        var key = Key(accountId, platform);
        if (string.IsNullOrWhiteSpace(credentials.AppSecret) &&
            string.IsNullOrWhiteSpace(credentials.AuthCode) &&
            string.IsNullOrWhiteSpace(credentials.AccessToken) &&
            string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            _blobStore.Delete(key);
            return;
        }

        var json = JsonSerializer.Serialize(credentials);
        var encrypted = _protector.Protect(Encoding.UTF8.GetBytes(json));
        _blobStore.Save(key, accountId, encrypted);
    }

    private static string Key(string accountId, PublishPlatform platform) =>
        $"kuaishou.credentials.{(int)platform}.{accountId}";
}
