using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class SystemServicesView : UserControl
{
    private const string XingeHint =
        "启用后客户端会用账号登录 XINGE，注册当前工作目录和 TikTok 账号列表，并接收 XINGE 下发的 TikTok 上传命令。客户端 ID/Token 仅用于兼容旧配置。";

    private readonly XingeConnectionViewModel _xinge;
    private SystemServicesViewModel? _vm;

    public SystemServicesView()
    {
        InitializeComponent();
        _xinge = new XingeConnectionViewModel(null, "TikTokPublisher", XingeHint)
        {
            ShowSaveButton = false,
            ShowHeading = false,
        };
        XingeConnectionHost.Bind(_xinge);
    }

    public void Bind(SystemServicesViewModel vm)
    {
        if (_vm is not null)
            _vm.StatusRequested -= OnStatusRequested;

        _vm = vm;
        DataContext = vm;
        vm.AttachXinge(_xinge);
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
        var includeData = await ConfigScopeDialog.ShowAsync(
            owner,
            "导出配置",
            "仅配置：账号、系统设置、登录态和印章。工作目录、Excel、归档只记录路径。"
            + Environment.NewLine
            + "配置和数据：在配置之外，再带上 Excel 上传记录、队列库、归档记录和主库里的运行记录。"
            + Environment.NewLine
            + "视频、素材和软件授权不会导出。",
            "仅配置",
            "配置和数据");
        if (includeData is null)
            return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出配置",
            SuggestedFileName = includeData.Value
                ? $"tiktok-publisher-full-{DateTime.Now:yyyyMMdd}.zip"
                : $"tiktok-publisher-config-{DateTime.Now:yyyyMMdd}.zip",
            FileTypeChoices = [new FilePickerFileType("配置包") { Patterns = ["*.zip"] }],
        });
        if (file is null)
            return;
        try
        {
            var path = file.Path.LocalPath;
            await Task.Run(() => PublisherConfigTransfer.ExportTo(path, includeData.Value));
            var title = includeData.Value ? "配置和数据已导出到" : "配置已导出到";
            await InfoDialog.ShowAsync(owner, $"{title}：{path}", "导出配置", 520, 180);
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
            var info = await Task.Run(() => PublisherConfigTransfer.Inspect(path));
            bool includeData;
            if (!info.IncludesRunData)
            {
                var confirmed = await ConfirmDialog.ShowAsync(owner, "导入配置", info.Summary, 560, 320);
                if (!confirmed)
                    return;
                includeData = false;
            }
            else
            {
                var choice = await ConfigScopeDialog.ShowAsync(
                    owner,
                    "导入配置",
                    info.Summary,
                    "只导入配置",
                    "配置和数据都导入");
                if (choice is null)
                    return;
                includeData = choice.Value;
            }

            var result = await Task.Run(() => PublisherConfigTransfer.ImportFrom(path, includeData));
            vm.NotifyConfigImported();
            await InfoDialog.ShowAsync(owner, result, "导入配置", 560, 360);
        }
        catch (Exception ex)
        {
            await InfoDialog.ShowAsync(owner, ex.Message, "导入配置失败", 520, 200);
        }
    }

    private async void OnUploadCloudConfigClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SystemServicesViewModel vm || vm.IsBusy)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var service = new TikTokManagementConfigSyncService();
        vm.IsBusy = true;
        TikTokConfigUploadPackage? package = null;
        try
        {
            package = await Task.Run(service.PrepareUpload);
            if (!package.Ok)
            {
                await InfoDialog.ShowAsync(owner, package.Message, "上传配置失败", 520, 180);
                return;
            }

            var confirmed = await ConfirmDialog.ShowAsync(owner, "上传配置到云端", package.Summary, 560, 340);
            if (!confirmed)
                return;

            var result = await service.UploadAsync(package);
            await InfoDialog.ShowAsync(
                owner,
                result.Message,
                result.Ok ? "上传配置" : "上传配置失败",
                560,
                result.Ok ? 180 : 220);
        }
        catch (Exception ex)
        {
            await InfoDialog.ShowAsync(owner, ex.Message, "上传配置失败", 520, 180);
        }
        finally
        {
            package?.Dispose();
            vm.IsBusy = false;
        }
    }

    private async void OnDownloadCloudConfigClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SystemServicesViewModel vm || vm.IsBusy)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var service = new TikTokManagementConfigSyncService();
        vm.IsBusy = true;
        try
        {
            var meta = await service.FetchMetaAsync();
            if (!meta.Ok || !meta.Exists || meta.Meta is null)
            {
                await InfoDialog.ShowAsync(
                    owner,
                    meta.Message,
                    meta.Ok ? "从云端下载配置" : "下载配置失败",
                    520,
                    180);
                return;
            }

            var confirmed = await ConfirmDialog.ShowAsync(
                owner,
                "从云端下载配置",
                meta.Meta.BuildConfirmationText(),
                560,
                380);
            if (!confirmed)
                return;

            var result = await service.DownloadAndImportAsync(meta.Meta);
            if (result.Ok)
                vm.NotifyConfigImported();
            await InfoDialog.ShowAsync(
                owner,
                result.Message,
                result.Ok ? "下载配置" : "下载配置失败",
                560,
                result.Ok ? 360 : 220);
        }
        catch (Exception ex)
        {
            await InfoDialog.ShowAsync(owner, ex.Message, "下载配置失败", 520, 200);
        }
        finally
        {
            vm.IsBusy = false;
        }
    }
}
