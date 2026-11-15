using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Core.Services;

public enum TikTokLicenseContractLookupStatus
{
    AlreadyConfigured,
    NotLoggedIn,
    Single,
    Multiple,
    None,
    Failed,
}

public readonly record struct TikTokLicenseContractLookupResult(
    TikTokLicenseContractLookupStatus Status,
    string LicenseId,
    IReadOnlyList<string> LicenseIds,
    string Message)
{
    public static TikTokLicenseContractLookupResult AlreadyConfigured() =>
        new(TikTokLicenseContractLookupStatus.AlreadyConfigured, "", Array.Empty<string>(), "");
}

/// <summary>合同 ID 为空时，用已保存的登录状态读取授权合同。</summary>
public static class TikTokLicenseContractLookup
{
    public const string FetchingLog = "合同 ID 为空，正在读取授权合同。";

    public static string SavedLog(string licenseId) => $"已保存授权合同 {licenseId}。";

    public static TikTokLicenseContractLookupResult FromLicenseIds(IReadOnlyList<string> licenseIds)
    {
        var ids = licenseIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 1)
            return new(TikTokLicenseContractLookupStatus.Single, ids[0], ids, "");
        if (ids.Length == 0)
        {
            return new(
                TikTokLicenseContractLookupStatus.None,
                "",
                ids,
                "该账号没有授权合同，请在「账号管理 → 发布配置」填写合同 ID。");
        }

        return new(
            TikTokLicenseContractLookupStatus.Multiple,
            "",
            ids,
            "授权合同有多份，请在「账号管理 → 发布配置」选择一份：" + string.Join("、", ids));
    }

    public static bool TryApply(TikTokAccountProfile account, string licenseId)
    {
        if (!string.IsNullOrWhiteSpace(account.TiktokContractId))
            return false;
        var id = licenseId.Trim();
        if (id.Length == 0)
            return false;
        account.TiktokContractId = id;
        account.TiktokContractIdMode = TikTokPublishConstants.ContractIdModeManual;
        return true;
    }

    public static async Task<TikTokLicenseContractLookupResult> TryReadAsync(
        TikTokAccountProfile account,
        Action<string>? log,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(account.TiktokContractId))
            return TikTokLicenseContractLookupResult.AlreadyConfigured();

        var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
        if (!File.Exists(authPath))
        {
            return new(
                TikTokLicenseContractLookupStatus.NotLoggedIn,
                "",
                Array.Empty<string>(),
                "请先在浏览器页登录该账号，再自动获取授权合同。");
        }

        string cookie;
        try
        {
            cookie = TikTokAccountAnalyticsClient.ReadCookieHeader(authPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Failed(ex.Message);
        }

        if (string.IsNullOrWhiteSpace(cookie))
        {
            return new(
                TikTokLicenseContractLookupStatus.NotLoggedIn,
                "",
                Array.Empty<string>(),
                "请先在浏览器页登录该账号，再自动获取授权合同。");
        }

        log?.Invoke(FetchingLog);
        try
        {
            var json = await new TikTokAccountAnalyticsClient()
                .PostLicenseContractsAsync(cookie, TikTokProxyHelper.BuildFromAccount(account), ct)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("code", out var codeElement) &&
                codeElement.TryGetInt64(out var code) &&
                code != 0)
            {
                var message = ReadMessage(root);
                return Failed(string.IsNullOrWhiteSpace(message) ? $"授权合同接口返回 code {code}。" : message);
            }

            return FromLicenseIds(TikTokApiSeriesDocument.ListLicenseIds(root));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(ex.Message);
        }
    }

    private static TikTokLicenseContractLookupResult Failed(string message)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? "授权合同接口没有返回数据。" : message.Trim();
        return new(TikTokLicenseContractLookupStatus.Failed, "", Array.Empty<string>(), "读取授权合同失败：" + detail);
    }

    private static string ReadMessage(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString()?.Trim() ?? "";
        if (root.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String)
            return msg.GetString()?.Trim() ?? "";
        return "";
    }
}
