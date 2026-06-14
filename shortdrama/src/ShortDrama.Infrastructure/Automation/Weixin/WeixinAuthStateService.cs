using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Automation.Weixin;

public sealed class WeixinAuthStateService : IWeixinAuthStateService
{
    public async Task<WeixinAuthStateInfo> ResolveAsync(
        WeixinAutomationConfig config,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var authFilePath = Path.GetFullPath(config.AuthFilePath);
        if (!File.Exists(authFilePath))
        {
            return new WeixinAuthStateInfo(
                AuthFilePath: authFilePath,
                Exists: false,
                IsValidJson: false,
                CookiesCount: 0,
                OriginsCount: 0,
                Message: "未找到微信登录态文件。");
        }

        try
        {
            await using var stream = File.OpenRead(authFilePath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var cookiesCount = root.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array
                ? cookies.GetArrayLength()
                : 0;
            var originsCount = root.TryGetProperty("origins", out var origins) && origins.ValueKind == JsonValueKind.Array
                ? origins.GetArrayLength()
                : 0;

            return new WeixinAuthStateInfo(
                AuthFilePath: authFilePath,
                Exists: true,
                IsValidJson: true,
                CookiesCount: cookiesCount,
                OriginsCount: originsCount,
                Message: cookiesCount > 0 || originsCount > 0
                    ? "已检测到可复用微信登录态。"
                    : "登录态文件存在，但未发现 cookies/origins 数据。");
        }
        catch (Exception ex)
        {
            return new WeixinAuthStateInfo(
                AuthFilePath: authFilePath,
                Exists: true,
                IsValidJson: false,
                CookiesCount: 0,
                OriginsCount: 0,
                Message: $"登录态文件无效: {ex.Message}");
        }
    }
}
