using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public static class AccountLoginStatusHelper
{
    public static string Describe(TikTokAccountProfile profile)
    {
        var authPath = Expand(profile.TiktokStorageStatePath);
        var authExists = !string.IsNullOrWhiteSpace(authPath) && File.Exists(authPath);
        var text = authExists ? "授权文件存在（可能已登录）" : "尚未登录";

        var lastEmail = (profile.TiktokLastLoginEmail ?? profile.TiktokLoginEmail ?? "").Trim();
        if (!string.IsNullOrEmpty(lastEmail))
            text += $" | {lastEmail}";

        var lastLoginAt = (profile.TiktokLastLoginAt ?? "").Trim();
        if (!string.IsNullOrEmpty(lastLoginAt))
            text += $" | {lastLoginAt}";

        return text;
    }

    public static void DeleteAuthState(TikTokAccountProfile profile)
    {
        var authPath = Expand(profile.TiktokStorageStatePath);
        if (string.IsNullOrWhiteSpace(authPath)) return;
        try
        {
            if (File.Exists(authPath))
                File.Delete(authPath);
        }
        catch
        {
            // 文件占用时不阻断重新登录
        }
    }

    private static string Expand(string? path)
    {
        var text = (path ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return "";
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(text)); }
        catch { return text; }
    }
}
