using System.Collections.ObjectModel;
using System.Diagnostics;
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
    [ObservableProperty] private int _rowIndex;

    public string DisplayName => Item.DisplayName;
    public string OriginalTitle => Item.OriginalTitle;
    public string NewTitle => Item.NewTitle;
    public string ArchiveSource => Item.ArchiveSource.ToLowerInvariant() switch
    {
        "" or "tiktok" => "TikTok",
        "kuaishou" => "快手",
        "video_channel" => "视频号",
        "miniprogram" => "小程序",
        _ => Item.ArchiveSource,
    };
    public string ArchivedAt => Item.ArchivedAt;
    public string MetadataPath => Item.MetadataPath;
    public string ArchiveDisplayPath => string.IsNullOrWhiteSpace(Item.MetadataPath)
        ? Item.ArchiveProjectDir
        : Item.MetadataPath;
    public string SourceDir => Item.ArchivedSourceDir;
    public string WorkflowDir => Item.ArchivedWorkflowDir;
}

public sealed partial class ArchivedProjectsViewModel : ViewModelBase
{
    public ObservableCollection<ArchivedProjectRowViewModel> Rows { get; } = new();
    public ObservableCollection<ArchivedProjectRowViewModel> FilteredRows { get; } = new();

    [ObservableProperty] private string _workspacePath = "";
    [ObservableProperty] private string _archiveRootDir = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _rootSummary = "归档根目录: 未选择工作目录";
    [ObservableProperty] private ArchivedProjectRowViewModel? _selectedRow;

    public event Action<string>? StatusRequested;
    public event Action? Restored;
    public Func<TikTokAccountProfile?>? AccountProvider { get; set; }

    public void SetWorkspace(string? workspacePath)
    {
        WorkspacePath = workspacePath?.Trim() ?? "";
        SyncArchiveRootFromSettings();
        Refresh();
    }

    public void SetArchiveRootDir(string? archiveRootDir)
    {
        ArchiveRootDir = Path.GetFullPath((archiveRootDir ?? "").Trim());
        var settings = ClientSettingsStore.Load();
        settings.ArchiveRootDir = ArchiveRootDir;
        ClientSettingsStore.Save(settings);
        Refresh();
    }

    partial void OnSearchTextChanged(string value) => ApplySearchFilter();

    [RelayCommand]
    private void Refresh()
    {
        Rows.Clear();
        FilteredRows.Clear();
        RootSummary = string.IsNullOrWhiteSpace(ArchiveRootDir)
            ? "归档根目录: 未选择工作目录"
            : $"归档根目录: {ArchiveRootDir}";
        var workspace = WorkspaceForAction();
        if (workspace is null || !Directory.Exists(workspace))
        {
            StatusMessage = "请先绑定工作目录";
            return;
        }

        foreach (var item in TikTokArchivedProjectService.List(workspace, ArchiveRootDir))
            Rows.Add(new ArchivedProjectRowViewModel(item));
        ApplySearchFilter();
    }

    public void SelectAll()
    {
        foreach (var row in FilteredRows)
            row.Selected = true;
    }

    public void ClearSelection()
    {
        foreach (var row in FilteredRows)
            row.Selected = false;
    }

    public void SelectToCurrentProject()
    {
        if (SelectedRow is null)
        {
            StatusMessage = "请先点击选中一个目标项目";
            return;
        }

        var current = FilteredRows.IndexOf(SelectedRow);
        if (current < 0)
        {
            StatusMessage = "当前项目不在筛选结果中";
            return;
        }

        var anchor = FilteredRows
            .Select((row, index) => (row, index))
            .FirstOrDefault(pair => pair.row.Selected)
            .index;
        var start = Math.Min(anchor, current);
        var end = Math.Max(anchor, current);
        for (var i = start; i <= end; i++)
            FilteredRows[i].Selected = true;
        StatusMessage = $"已勾选 {end - start + 1} 个归档项目";
    }

    public void OpenArchiveRoot() => OpenPath(ArchiveRootDir);
    public void OpenSelectedArchiveDir() => OpenPath(SelectedRow?.ArchiveDisplayPath);
    public void OpenSelectedSourceDir() => OpenPath(SelectedRow?.SourceDir);
    public void OpenSelectedWorkflowDir() => OpenPath(SelectedRow?.WorkflowDir);
    public void OpenRowSource(ArchivedProjectRowViewModel? row) => OpenPath(row?.SourceDir);
    public void OpenRowWorkflow(ArchivedProjectRowViewModel? row) => OpenPath(row?.WorkflowDir);

    public async Task RestoreSelectedAsync()
    {
        var targets = TargetRowsForAction();
        if (targets.Length == 0)
        {
            StatusMessage = "请先勾选要回退的归档项目（或点击选中一行）";
            return;
        }

        var workspace = WorkspaceForAction();
        if (workspace is null)
        {
            StatusMessage = "未找到可恢复的工作目录";
            return;
        }

        var (success, failures) = await Task.Run(() =>
        {
            var ok = 0;
            var errors = new List<string>();
            foreach (var row in targets)
            {
                try
                {
                    TikTokArchivedProjectService.Restore(workspace, row.Item.ArchiveProjectDir, ArchiveRootDir);
                    ok++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{row.DisplayName}: {ex.Message}");
                }
            }

            return (ok, errors);
        });

        Refresh();
        if (success > 0)
            Restored?.Invoke();
        StatusMessage = failures.Count == 0
            ? $"已回退 {success} 个归档项目"
            : $"回退完成：成功 {success} 个，失败 {failures.Count} 个";
        StatusRequested?.Invoke(StatusMessage);
        foreach (var failure in failures.Take(5))
            StatusRequested?.Invoke($"回退失败：{failure}");
    }

    public async Task DeleteSelectedAsync()
    {
        var targets = TargetRowsForAction();
        if (targets.Length == 0)
        {
            StatusMessage = "请先勾选要删除的归档项目（或点击选中一行）";
            return;
        }

        var workspace = WorkspaceForAction();
        if (workspace is null)
        {
            StatusMessage = "未找到可删除的工作目录";
            return;
        }

        var deleted = 0;
        foreach (var row in targets)
        {
            await TikTokArchivedProjectService.DeleteAsync(workspace, row.Item.ArchiveProjectDir, ArchiveRootDir);
            deleted++;
        }

        Refresh();
        StatusMessage = deleted == 1
            ? $"已删除归档项目：{targets[0].DisplayName}"
            : $"已删除归档项目 {deleted} 个";
        StatusRequested?.Invoke(StatusMessage);
    }

    public int GetActionTargetCount() => TargetRowsForAction().Length;

    public async Task SyncCheckedToManagementAsync()
    {
        var targets = Rows.Where(row => row.Selected).ToArray();
        if (targets.Length == 0)
        {
            StatusMessage = "请先勾选要同步到管理系统的已归档项目";
            return;
        }

        var ok = 0;
        var failed = 0;
        foreach (var row in targets)
        {
            var item = TikTokArchivedProjectService.ToQueueItemForSync(row.Item);
            var result = await TikTokManagementUploadRecordSyncService
                .SyncUploadRecordAsync(item, AccountProvider?.Invoke(), CancellationToken.None);
            if (result.Ok) ok++; else failed++;
            StatusRequested?.Invoke($"同步归档项目：{row.DisplayName} - {result.Message}");
        }

        StatusMessage = $"同步完成：成功 {ok}，失败 {failed}";
        StatusRequested?.Invoke(StatusMessage);
    }

    private void SyncArchiveRootFromSettings()
    {
        var settings = ClientSettingsStore.Load();
        ArchiveRootDir = string.IsNullOrWhiteSpace(settings.ArchiveRootDir)
            ? string.IsNullOrWhiteSpace(WorkspacePath) ? "" : Path.Combine(Path.GetFullPath(WorkspacePath), "archive")
            : Path.GetFullPath(settings.ArchiveRootDir);
        RootSummary = string.IsNullOrWhiteSpace(ArchiveRootDir)
            ? "归档根目录: 未选择工作目录"
            : $"归档根目录: {ArchiveRootDir}";
    }

    private void ApplySearchFilter()
    {
        FilteredRows.Clear();
        var keyword = (SearchText ?? "").Trim().ToLowerInvariant();
        var index = 1;
        foreach (var row in Rows)
        {
            if (keyword.Length > 0 &&
                !Contains(row.DisplayName, keyword) &&
                !Contains(row.OriginalTitle, keyword) &&
                !Contains(row.NewTitle, keyword) &&
                !Contains(row.ArchiveSource, keyword))
            {
                continue;
            }

            row.RowIndex = index++;
            FilteredRows.Add(row);
        }

        StatusMessage = FilteredRows.Count == Rows.Count
            ? $"已归档: {Rows.Count}"
            : $"已归档: {FilteredRows.Count} / {Rows.Count}";
    }

    private ArchivedProjectRowViewModel[] TargetRowsForAction()
    {
        var checkedRows = Rows.Where(r => r.Selected).ToArray();
        if (checkedRows.Length > 0)
            return checkedRows;
        return SelectedRow is null ? Array.Empty<ArchivedProjectRowViewModel>() : new[] { SelectedRow };
    }

    private string? WorkspaceForAction()
    {
        if (!string.IsNullOrWhiteSpace(WorkspacePath) && Directory.Exists(WorkspacePath))
            return Path.GetFullPath(WorkspacePath);
        if (!string.IsNullOrWhiteSpace(ArchiveRootDir))
        {
            var parent = Directory.GetParent(Path.GetFullPath(ArchiveRootDir));
            if (parent is not null)
                return parent.FullName;
        }

        return null;
    }

    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "未找到目录";
            return;
        }

        var target = Path.GetFullPath(path);
        if (File.Exists(target))
            target = Path.GetDirectoryName(target) ?? target;
        if (!Directory.Exists(target))
        {
            StatusMessage = $"目录不存在：{target}";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        });
        StatusMessage = $"已打开：{target}";
    }

    private static bool Contains(string? value, string keyword) =>
        (value ?? "").ToLowerInvariant().Contains(keyword);
}
