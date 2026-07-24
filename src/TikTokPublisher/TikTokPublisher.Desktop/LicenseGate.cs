using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Views;

namespace TikTokPublisher.Desktop;

internal sealed record LicenseLoginDefaults(string ServerUrl, string Account, string Password);

internal static class LicenseGate
{
    public static LicenseStartupAction GetStartupAction() =>
        LicenseStartupPolicy.Decide(LicenseStore.Load());

    public static Task<LicenseState?> VerifyAsync(bool forceVerify)
    {
        var settings = ClientSettingsStore.Load();
        return Task.Run(() => LicenseAuthService.LoadUsableState(
            settings.AuthServerUrl,
            verifyIfDue: true,
            forceVerify: forceVerify,
            account: settings.AuthAccount,
            password: settings.AuthPassword));
    }

    public static LicenseLoginDefaults GetLoginDefaults()
    {
        var settings = ClientSettingsStore.Load();
        var state = LicenseStore.Load();
        var serverUrl = FirstNonEmpty(settings.AuthServerUrl, state.ServerUrl);
        var account = FirstNonEmpty(state.AccountUsername, state.Email, state.LicenseKey, settings.AuthAccount);
        return new LicenseLoginDefaults(serverUrl, account, settings.AuthPassword);
    }

    public static void SaveLoginResult(LicenseLoginDialogResult result)
    {
        var settings = ClientSettingsStore.Load();
        LicenseSettingsBridge.ApplyAccountLoginCredentials(
            settings,
            result.State,
            result.ServerUrl,
            result.Account,
            result.Password);
        ClientSettingsStore.Save(settings);
    }

    public static void SaveVerifiedState(LicenseState state)
    {
        var settings = ClientSettingsStore.Load();
        LicenseSettingsBridge.ApplyAccountLoginCredentials(settings, state);
        ClientSettingsStore.Save(settings);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "")
            .FirstOrDefault(value => value.Length > 0) ?? "";
}
