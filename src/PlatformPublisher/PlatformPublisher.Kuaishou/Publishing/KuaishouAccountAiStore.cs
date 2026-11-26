using System.ComponentModel;
using System.Text.Json;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouTextPreset(string Id, string Name, string Endpoint, string Model, int Timeout);

public sealed record KuaishouImagePreset(
    string Id, string Name, string Endpoint, string Model, string Provider, string Size, string Quality);

public sealed record KuaishouAccountAiOptions
{
    public string TextPresetId { get; init; } = "";
    public string ImagePresetId { get; init; } = "";
    public KuaishouTextPreset[] TextPresets { get; init; } = [];
    public KuaishouImagePreset[] ImagePresets { get; init; } = [];
    public Dictionary<string, string> TextKeys { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ImageKeys { get; init; } = new(StringComparer.Ordinal);
    public string PublishPrompt { get; init; } = "";
    public string PosterMode { get; init; } = "";
    public string VerticalDirectPrompt { get; init; } = "";
    public string HorizontalDirectPrompt { get; init; } = "";
    public string VerticalErasePrompt { get; init; } = "";
    public string HorizontalErasePrompt { get; init; } = "";

    public static string EndpointKey(string endpoint) =>
        Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + uri.Query
            : "";
}

public sealed class KuaishouAccountAiStore(ISecureBlobStore blobs, IDataProtector protection)
{
    public KuaishouAccountAiOptions Load(string accountId)
    {
        try
        {
            var value = blobs.Load(Key(accountId), accountId);
            return value is null ? new() :
                JsonSerializer.Deserialize<KuaishouAccountAiOptions>(protection.Unprotect(value)) ?? new();
        }
        catch (Exception ex) when (ex is Win32Exception or JsonException or System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException("快手账号的 AI 配置无法解密，请重新填写并保存。", ex);
        }
    }

    public void Save(string accountId, KuaishouAccountAiOptions options)
    {
        var protectedData = protection.Protect(JsonSerializer.SerializeToUtf8Bytes(options));
        blobs.Save(Key(accountId), accountId, protectedData);
    }

    private static string Key(string accountId) => $"kuaishou.ai.account.{accountId}";
}
