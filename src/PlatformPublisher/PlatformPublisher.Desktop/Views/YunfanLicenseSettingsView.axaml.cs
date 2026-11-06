using Avalonia.Controls;
using Avalonia.Interactivity;
using PlatformPublisher.Common.Services;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Views;

namespace PlatformPublisher.Desktop.Views;

public partial class YunfanLicenseSettingsView : UserControl
{
    public YunfanLicenseSettingsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var defaults = YunfanLicenseGate.GetLoginDefaults();
        ServerUrlBox.Text = defaults.ServerUrl;
        VerifyIntervalText.Text = $"启动时 + 每 {LicenseAuthService.VerifyIntervalHours} 小时（系统固定）";
        var state = LicenseStore.Load();
        if (!state.IsActivated())
        {
            LicenseSummaryText.Text = "未登录";
            return;
        }

        var lines = new List<string> { "已登录" };
        if (!string.IsNullOrWhiteSpace(state.AccountUsername))
            lines.Add($"账号：{state.AccountUsername}");
        if (!string.IsNullOrWhiteSpace(state.ExpiresAt))
            lines.Add($"到期时间：{state.ExpiresAt}");
        if (!string.IsNullOrWhiteSpace(state.LastVerifiedAt))
            lines.Add($"最近校验：{state.LastVerifiedAt}");
        LicenseSummaryText.Text = string.Join(Environment.NewLine, lines);
    }

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var defaults = YunfanLicenseGate.GetLoginDefaults();
        var result = await LicenseLoginDialog.ShowAsync(
            owner,
            string.IsNullOrWhiteSpace(ServerUrlBox.Text) ? defaults.ServerUrl : ServerUrlBox.Text.Trim(),
            defaults.Account,
            defaults.Password,
            "请输入云帆授权账号。登录成功后保存在本机，并用于启动和定时联网校验。");
        if (result is null)
            return;

        YunfanLicenseGate.SaveLoginResult(result);
        LicenseStatusText.Text = "登录成功";
        Refresh();
    }

    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await ConfirmDialog.ShowAsync(owner, "确认清除", "确认清除本机云帆软件授权登录？");
        if (!confirmed)
            return;

        LicenseStatusText.Text = "正在退出登录...";
        try
        {
            await LicenseAuthService.LogoutAsync();
            LicenseStatusText.Text = "已退出登录。下次启动需要重新登录。";
        }
        catch (Exception ex)
        {
            LicenseStore.Clear();
            LicenseStatusText.Text = ex.Message;
        }

        Refresh();
    }

    private void OnSaveServerClick(object? sender, RoutedEventArgs e)
    {
        var settings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
        settings.AuthServerUrl = (ServerUrlBox.Text ?? "").Trim().TrimEnd('/');
        ClientSettingsStore.Save(settings, PlatformPublisherPaths.SettingsDatabasePath);
        LicenseStatusText.Text = "服务器地址已保存";
        Refresh();
    }
}
