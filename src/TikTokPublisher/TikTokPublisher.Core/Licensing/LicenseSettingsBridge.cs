using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Licensing;

public static class LicenseSettingsBridge
{
    public static void ApplyAccountLoginCredentials(
        ClientSettings settings,
        LicenseState state,
        string? serverUrl = null,
        string? account = null,
        string? password = null)
    {
        var cleanServerUrl = CleanBaseUrl(FirstNonEmpty(serverUrl, state.ServerUrl, settings.AuthServerUrl));
        var cleanAccount = FirstNonEmpty(
            state.AccountUsername,
            state.Email,
            state.LicenseKey,
            account,
            settings.AuthAccount);

        settings.AuthServerUrl = cleanServerUrl;
        settings.AuthAccount = cleanAccount;
        if (!string.IsNullOrEmpty(password))
            settings.AuthPassword = password;
        settings.AuthLastUsername = cleanAccount;
        settings.AuthLastLoginAt = FirstNonEmpty(state.LastVerifiedAt, state.ActivatedAt, settings.AuthLastLoginAt);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "")
            .FirstOrDefault(value => value.Length > 0) ?? "";

    private static string CleanBaseUrl(string? serverUrl) =>
        (serverUrl ?? "").Trim().TrimEnd('/');
}
