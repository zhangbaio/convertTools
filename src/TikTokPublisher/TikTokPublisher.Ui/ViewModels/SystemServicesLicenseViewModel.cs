using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class SystemServicesViewModel
{
    public int LicenseVerifyIntervalHours => LicenseAuthService.VerifyIntervalHours;

    public string ServiceSummaryText
    {
        get
        {
            var state = LicenseStore.Load();
            var licenseStatus = state.IsActivated() ? "已登录" : "未登录";
            var feishuStatus = FeishuCommandEnabled ? "已启用" : "未启用";
            var xingeStatus = XingeRemoteEnabled ? "已启用" : "未启用";
            return $"授权：{licenseStatus} | 飞书命令：{feishuStatus} | XINGE：{xingeStatus} | 联网校验：启动时 + 每 {LicenseAuthService.VerifyIntervalHours} 小时";
        }
    }

    partial void OnFeishuCommandEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ServiceSummaryText));
    }

    partial void OnXingeRemoteEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ServiceSummaryText));
    }

    public void RefreshLicenseSummaryDisplay()
    {
        var state = LicenseStore.Load();
        if (!state.IsActivated())
        {
            LicenseSummary = "未登录";
        }
        else
        {
            var parts = new List<string> { "已登录" };
            if (!string.IsNullOrWhiteSpace(state.ExpiresAt))
                parts.Add($"到期时间：{state.ExpiresAt}");
            if (!string.IsNullOrWhiteSpace(state.LastVerifiedAt))
                parts.Add($"最近校验：{state.LastVerifiedAt}");
            LicenseSummary = string.Join(Environment.NewLine, parts);
        }

        OnPropertyChanged(nameof(ServiceSummaryText));
    }

    public void ApplyLicenseLoginResult(string serverUrl, string account, string password, LicenseState state)
    {
        AuthServerUrl = serverUrl.Trim().TrimEnd('/');
        LoginAccount = FirstNonEmpty(state.AccountUsername, state.Email, state.LicenseKey, account);
        LoginPassword = password;

        var settings = ClientSettingsStore.Load();
        ApplyUiToSettings(settings);
        LicenseSettingsBridge.ApplyAccountLoginCredentials(settings, state, AuthServerUrl, LoginAccount, LoginPassword);
        ClientSettingsStore.Save(settings);

        LoginStatus = "登录成功";
        RefreshLicenseSummaryDisplay();
        StatusRequested?.Invoke("授权登录成功");
    }

    public async Task ClearLicenseLoginAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LoginStatus = "正在退出登录...";
        try
        {
            await LicenseAuthService.LogoutAsync();
            LoginStatus = "已退出登录";
            RefreshLicenseSummaryDisplay();
            StatusRequested?.Invoke("已退出授权登录");
        }
        catch (Exception ex)
        {
            LoginStatus = ex.Message;
            StatusRequested?.Invoke($"退出授权登录失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? "")
            .FirstOrDefault(value => value.Length > 0) ?? "";
}
