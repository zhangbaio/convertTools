using PlatformPublisher.Common.Services;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Views;

namespace PlatformPublisher.Desktop;

internal static class YunfanLicenseGate
{
    public static void Configure()
    {
        LicenseAuthService.UseProduct("云帆多平台发布助手", "云帆多平台发布助手");
        LicenseAuthService.UseAccountApi("ks");
        LicenseStore.UseStateDirectory(PlatformPublisherPaths.DataRoot);
    }

    public static LicenseStartupAction GetStartupAction() =>
        LicenseStartupPolicy.Decide(LicenseStore.Load());

    public static Task<LicenseState?> VerifyAsync(bool forceVerify)
    {
        var settings = LoadSettings();
        return Task.Run(() => LicenseAuthService.LoadUsableState(
            settings.AuthServerUrl,
            verifyIfDue: true,
            forceVerify: forceVerify,
            account: settings.AuthAccount,
            password: settings.AuthPassword));
    }

    public static async Task<bool> EnsureBeforeMainWindowAsync()
    {
        if (GetStartupAction() == LicenseStartupAction.PromptLogin)
            return await PromptLoginAsync("首次使用或本机尚未登录，请联网登录并获取授权后继续使用。");

        var state = await VerifyAsync(forceVerify: true);
        if (state is not null)
        {
            SaveVerifiedState(state);
            return true;
        }

        return await PromptLoginAsync("软件授权联网校验失败，请重新登录后继续使用。");
    }

    public static async Task<bool> PromptLoginAsync(string message)
    {
        var login = GetLoginDefaults();
        var result = await LicenseLoginDialog.ShowStandaloneAsync(
            login.ServerUrl,
            login.Account,
            login.Password,
            message);
        if (result is null)
            return false;

        SaveLoginResult(result);
        return true;
    }

    public static void SaveLoginResult(LicenseLoginDialogResult result)
    {
        var settings = LoadSettings();
        LicenseSettingsBridge.ApplyAccountLoginCredentials(
            settings,
            result.State,
            result.ServerUrl,
            result.Account,
            result.Password);
        SaveSettings(settings);
    }

    public static void SaveVerifiedState(LicenseState state)
    {
        var settings = LoadSettings();
        LicenseSettingsBridge.ApplyAccountLoginCredentials(settings, state);
        SaveSettings(settings);
    }

    public static (string ServerUrl, string Account, string Password) GetLoginDefaults()
    {
        var settings = LoadSettings();
        var state = LicenseStore.Load();
        var serverUrl = FirstNonEmpty(settings.AuthServerUrl, state.ServerUrl);
        var account = FirstNonEmpty(state.AccountUsername, state.Email, state.LicenseKey, settings.AuthAccount);
        return (serverUrl, account, settings.AuthPassword);
    }

    private static ClientSettings LoadSettings() =>
        ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);

    private static void SaveSettings(ClientSettings settings) =>
        ClientSettingsStore.Save(settings, PlatformPublisherPaths.SettingsDatabasePath);

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "")
            .FirstOrDefault(value => value.Length > 0) ?? "";
}
