using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TikTokPublisher.Core.Services;
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
            "请输入软件授权账号。登录成功后会保存到 C# 客户端独立的 license_state.bin，并用于启动和定时联网校验。");
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

        await vm.ClearLicenseLoginAsync();
    }

    private async void OnExportConfigClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (storage is null)
            return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出配置",
            SuggestedFileName = $"tiktok-publisher-config-{DateTime.Now:yyyyMMdd}.zip",
            FileTypeChoices = [new FilePickerFileType("配置包") { Patterns = ["*.zip"] }],
        });
        if (file is null)
            return;
        try
        {
            var path = file.Path.LocalPath;
            await Task.Run(() => PublisherConfigTransfer.ExportTo(path));
            await InfoDialog.ShowAsync(owner, $"配置已导出到：{path}", "导出配置", 520, 180);
        }
        catch (Exception ex)
        {
            await InfoDialog.ShowAsync(owner, ex.Message, "导出配置失败", 520, 180);
        }
    }

    private async void OnImportConfigClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SystemServicesViewModel vm)
            return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (storage is null)
            return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入配置",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("配置包") { Patterns = ["*.zip"] }],
        });
        var file = files.FirstOrDefault();
        if (file is null)
            return;
        try
        {
            var path = file.Path.LocalPath;
            var summary = await Task.Run(() => PublisherConfigTransfer.Describe(path));
            var confirmed = await ConfirmDialog.ShowAsync(owner, "导入配置", summary, 560, 280);
            if (!confirmed)
                return;
            var result = await Task.Run(() => PublisherConfigTransfer.ImportFrom(path));
            vm.NotifyConfigImported();
            await InfoDialog.ShowAsync(owner, result, "导入配置", 560, 320);
        }
        catch (Exception ex)
        {
            await InfoDialog.ShowAsync(owner, ex.Message, "导入配置失败", 520, 200);
        }
    }
}
