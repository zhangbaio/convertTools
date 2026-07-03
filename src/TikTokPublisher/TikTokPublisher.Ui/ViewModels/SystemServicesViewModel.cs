using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class SystemServicesViewModel : ViewModelBase
{
    public event Action<string>? StatusRequested;

    [ObservableProperty] private string _authServerUrl = "";
    [ObservableProperty] private string _licenseSummary = "未登录";
    [ObservableProperty] private string _loginAccount = "";
    [ObservableProperty] private string _loginPassword = "";
    [ObservableProperty] private string _loginStatus = "";
    [ObservableProperty] private bool _isBusy;

    public void Load()
    {
        var settings = ClientSettingsStore.Load();
        AuthServerUrl = settings.AuthServerUrl ?? "";
        LoginAccount = settings.AuthAccount ?? "";
        LoginPassword = settings.AuthPassword ?? "";
        RefreshLicenseSummary();
    }

    [RelayCommand]
    private void RefreshLicenseSummary()
    {
        var state = LicenseStore.Load();
        if (!state.IsActivated())
        {
            LicenseSummary = "未登录";
            return;
        }

        var account = string.IsNullOrWhiteSpace(state.AccountUsername) ? state.LicenseKey : state.AccountUsername;
        var masked = string.IsNullOrWhiteSpace(state.LicenseKeyMasked)
            ? LicenseStore.MaskLicenseKey(account)
            : state.LicenseKeyMasked;
        LicenseSummary = $"已登录：{masked} | 设备：{state.MachineId[..Math.Min(8, state.MachineId.Length)]}…";
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LoginStatus = "登录中...";
        try
        {
            var settings = ClientSettingsStore.Load();
            settings.AuthServerUrl = AuthServerUrl.Trim();
            settings.AuthAccount = LoginAccount.Trim();
            settings.AuthPassword = LoginPassword;
            ClientSettingsStore.Save(settings);

            await LicenseAuthService.LoginAsync(settings.AuthServerUrl, LoginAccount, LoginPassword);
            LoginStatus = "登录成功";
            RefreshLicenseSummary();
            StatusRequested?.Invoke("授权登录成功");
        }
        catch (Exception ex)
        {
            LoginStatus = ex.Message;
            StatusRequested?.Invoke(LoginStatus);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Logout()
    {
        LicenseAuthService.Logout();
        LoginStatus = "已退出登录";
        RefreshLicenseSummary();
        StatusRequested?.Invoke("已退出授权登录");
    }

    [RelayCommand]
    private void SaveAuthServerUrl()
    {
        var settings = ClientSettingsStore.Load();
        settings.AuthServerUrl = AuthServerUrl.Trim();
        settings.AuthAccount = LoginAccount.Trim();
        settings.AuthPassword = LoginPassword;
        ClientSettingsStore.Save(settings);
        StatusRequested?.Invoke("授权服务地址已保存");
    }
}

public sealed partial class ArchivedProjectRowViewModel : ViewModelBase
{
    public ArchivedProjectItem Item { get; }

    public ArchivedProjectRowViewModel(ArchivedProjectItem item) => Item = item;

    [ObservableProperty] private bool _selected;

    public string DisplayName => Item.DisplayName;
    public string OriginalTitle => Item.OriginalTitle;
    public string NewTitle => Item.NewTitle;
    public string ArchivedAt => Item.ArchivedAt;
}

public sealed partial class ArchivedProjectsViewModel : ViewModelBase
{
    public ObservableCollection<ArchivedProjectRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _workspacePath = "";
    [ObservableProperty] private string _statusMessage = "";

    public event Action<string>? StatusRequested;

    public void SetWorkspace(string? workspacePath)
    {
        WorkspacePath = workspacePath?.Trim() ?? "";
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        Rows.Clear();
        if (string.IsNullOrWhiteSpace(WorkspacePath) || !Directory.Exists(WorkspacePath))
        {
            StatusMessage = "请先绑定工作目录";
            return;
        }

        foreach (var item in TikTokArchivedProjectService.List(WorkspacePath))
            Rows.Add(new ArchivedProjectRowViewModel(item));
        StatusMessage = $"已归档 {Rows.Count} 个项目";
    }

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        var targets = Rows.Where(r => r.Selected).ToArray();
        if (targets.Length == 0)
        {
            StatusMessage = "请先勾选要恢复的项目";
            return;
        }

        foreach (var row in targets)
        {
            try
            {
                TikTokArchivedProjectService.Restore(WorkspacePath, row.Item.ArchiveProjectDir);
            }
            catch (Exception ex)
            {
                StatusMessage = $"恢复失败：{ex.Message}";
                StatusRequested?.Invoke(StatusMessage);
                return;
            }
        }

        Refresh();
        StatusMessage = $"已恢复 {targets.Length} 个项目";
        StatusRequested?.Invoke(StatusMessage);
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var targets = Rows.Where(r => r.Selected).ToArray();
        if (targets.Length == 0)
        {
            StatusMessage = "请先勾选要删除的项目";
            return;
        }

        foreach (var row in targets)
        {
            try
            {
                await TikTokArchivedProjectService.DeleteAsync(WorkspacePath, row.Item.ArchiveProjectDir);
            }
            catch (Exception ex)
            {
                StatusMessage = $"删除失败：{ex.Message}";
                StatusRequested?.Invoke(StatusMessage);
                return;
            }
        }

        Refresh();
        StatusMessage = $"已删除 {targets.Length} 个归档项目";
        StatusRequested?.Invoke(StatusMessage);
    }
}
