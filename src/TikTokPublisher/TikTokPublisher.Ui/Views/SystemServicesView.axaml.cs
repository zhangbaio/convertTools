using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class SystemServicesView : UserControl
{
    private SystemServicesViewModel? _vm;

    public SystemServicesView() => InitializeComponent();

    public void Bind(SystemServicesViewModel vm)
    {
        if (_vm is not null)
            _vm.StatusRequested -= OnStatusRequested;

        _vm = vm;
        DataContext = vm;
        vm.StatusRequested += OnStatusRequested;
        vm.RefreshLicenseSummaryDisplay();
    }

    private async void OnStatusRequested(string message)
    {
        if (!message.Contains("系统服务配置已保存", StringComparison.Ordinal))
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        await InfoDialog.ShowSaveSuccessAsync(owner, "系统服务配置已保存成功。");
    }

    private async void OnLicenseLoginClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SystemServicesViewModel vm)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var result = await LicenseLoginDialog.ShowAsync(
            owner,
            vm.AuthServerUrl,
            vm.LoginAccount,
            vm.LoginPassword,
            "请输入软件授权账号。登录成功后会保存到 Python 兼容的 account_state.bin，并用于启动和定时联网校验。");
        if (result is null)
            return;

        vm.ApplyLicenseLoginResult(result.ServerUrl, result.Account, result.Password, result.State);
    }

    private async void OnLicenseLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SystemServicesViewModel vm)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await ConfirmDialog.ShowAsync(owner, "确认清除", "确认清除本机软件授权登录？");
        if (!confirmed)
            return;

        vm.ClearLicenseLogin();
    }
}
