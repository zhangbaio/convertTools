using System.Collections.ObjectModel;
using ChannelsPublisher.Core.Models;
using ChannelsPublisher.Core.Publishing;
using ChannelsPublisher.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChannelsPublisher.Desktop.ViewModels;

/// <summary>结束动作选项（供 UI 下拉）。</summary>
public sealed record FinalActionChoice(string Label, FinalAction Value);

/// <summary>主界面 VM：左侧账号列表 + 增删/登录命令 + 状态栏。对应参考图的多账号发布壳。</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    public const string ChannelsLoginUrl = "https://channels.weixin.qq.com/platform/login";

    private readonly AccountStore _store;
    private readonly bool _excludeNewAccountsFromKuaishou;
    private readonly Func<string, bool>? _hasWeixinJobs;

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = new();
    public IReadOnlyList<AccountItemViewModel> FilteredAccounts { get; private set; } = [];
    public ObservableCollection<string> Logs { get; } = new();

    [ObservableProperty] private AccountItemViewModel? _selectedAccount;
    [ObservableProperty] private string _accountSearchText = "";
    [ObservableProperty] private string _statusMessage = "就绪";
    public string CurrentAccountTitle => SelectedAccount?.DisplayName ?? "请先在左侧选择账号";
    public string CurrentLoginStatusText => SelectedAccount?.LoginStatusText ?? "尚未选择账号";
    public string CurrentLastLoginText => SelectedAccount?.LastLoginText ?? "-";
    public string CurrentRuntimeStatusText => SelectedAccount?.StatusText ?? "未选择";

    public AccountItemViewModel? SidebarSelectedAccount
    {
        get => SelectedAccount is { } account && FilteredAccounts.Contains(account) ? account : null;
        set
        {
            if (value is not null && FilteredAccounts.Contains(value))
                SelectedAccount = value;
        }
    }

    partial void OnSelectedAccountChanged(AccountItemViewModel? value)
    {
        OnPropertyChanged(nameof(SidebarSelectedAccount));
        OnPropertyChanged(nameof(CurrentAccountTitle));
        OnPropertyChanged(nameof(CurrentLoginStatusText));
        OnPropertyChanged(nameof(CurrentLastLoginText));
        OnPropertyChanged(nameof(CurrentRuntimeStatusText));
    }

    partial void OnAccountSearchTextChanged(string value) => RefreshAccountFilter();

    private void RefreshAccountFilter()
    {
        var keyword = (AccountSearchText ?? "").Trim();
        FilteredAccounts = Accounts.Where(IsInWorkspace).Where(account => keyword.Length == 0 ||
            account.Name?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true ||
            account.Nickname?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true ||
            account.Id?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        OnPropertyChanged(nameof(FilteredAccounts));
        OnPropertyChanged(nameof(SidebarSelectedAccount));
    }

    internal bool IsInWorkspace(AccountItemViewModel account) =>
        _hasWeixinJobs is null || WeixinWorkspaceAccounts.IsVisible(account.Model, _hasWeixinJobs(account.Id));

    internal IEnumerable<PublishAccount> UnassociatedAccounts =>
        Accounts.Where(account => !IsInWorkspace(account)).Select(account => account.Model);

    internal void AssociateWeixinAccount(string id)
    {
        if (_hasWeixinJobs is null || FindAccount(id) is not { } account) return;
        var previous = account.Model.IsWeixinWorkspaceAccount;
        account.Model.IsWeixinWorkspaceAccount = true;
        try { _store.Update(account.Model); }
        catch
        {
            account.Model.IsWeixinWorkspaceAccount = previous;
            throw;
        }
        RefreshAccountFilter();
        SelectedAccount = account;
        StatusMessage = $"已关联视频号账号「{account.DisplayName}」。";
    }

    private AccountItemViewModel CreateAccountItem(PublishAccount account) =>
        new(account, _hasWeixinJobs is null ? null : item => WeixinWorkspaceAccounts.WorkDirectory(item, _store.Accounts));

    partial void OnStatusMessageChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var entry = $"[{DateTime.Now:HH:mm:ss}] {value.Trim()}";
        if (Logs.FirstOrDefault() == entry) return;
        Logs.Insert(0, entry);
        while (Logs.Count > 500)
            Logs.RemoveAt(Logs.Count - 1);
    }

    // ── 发布任务队列（P3）──
    public ObservableCollection<PublishTaskItemViewModel> Tasks { get; } = new();

    public IReadOnlyList<FinalActionChoice> FinalActionChoices { get; } = new[]
    {
        new FinalActionChoice("只填不发（安全）", FinalAction.None),
        new FinalActionChoice("保存草稿", FinalAction.Draft),
        new FinalActionChoice("直接发表", FinalAction.Publish),
    };

    [ObservableProperty] private FinalActionChoice _selectedFinalAction;
    [ObservableProperty] private int _maxParallel = 2;

    public AccountItemViewModel? FindAccount(string nameOrId)
    {
        var key = (nameOrId ?? "").Trim();
        return Accounts.FirstOrDefault(a => a.Id == key)
               ?? Accounts.FirstOrDefault(a => a.Name == key);
    }

    public void ReloadAccounts(string? selectedAccountId = null)
    {
        var selectedId = string.IsNullOrWhiteSpace(selectedAccountId) ? SelectedAccount?.Id : selectedAccountId;
        _store.Load();
        Accounts.Clear();
        foreach (var account in _store.Accounts) Accounts.Add(CreateAccountItem(account));
        RefreshAccountFilter();
        SelectedAccount = Accounts.FirstOrDefault(account => account.Id == selectedId && IsInWorkspace(account))
            ?? Accounts.FirstOrDefault(IsInWorkspace);
        StatusMessage = $"账号列表已刷新，共 {Accounts.Count} 个账号。";
    }

    public void RefreshAccountList()
    {
        var current = _store.Accounts.ToDictionary(account => account.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var item in Accounts.Where(item => !current.ContainsKey(item.Id)).ToArray())
            Accounts.Remove(item);
        foreach (var account in current.Values)
        {
            if (Accounts.All(item => item.Id != account.Id))
                Accounts.Add(CreateAccountItem(account));
        }
        RefreshAccountFilter();
        if (SelectedAccount is null || !Accounts.Contains(SelectedAccount) || !IsInWorkspace(SelectedAccount))
            SelectedAccount = Accounts.FirstOrDefault(IsInWorkspace);
    }

    public void RecordAccountLogin(AccountItemViewModel account)
    {
        account.MarkLoggedIn(DateTimeOffset.Now);
        _store.Update(account.Model);
        StatusMessage = $"[{account.Name}] 登录会话已就绪";
    }

    /// <summary>请求视图把某账号的内嵌浏览器导航到 url（View 侧持有 WebView2Host）。</summary>
    public event Action<AccountItemViewModel, string>? NavigateRequested;
    public event Action<PublishAccount>? AccountSettingsSaved;

    public MainViewModel(AccountStore store, bool loadAccounts = true, bool excludeNewAccountsFromKuaishou = false,
        Func<string, bool>? hasWeixinJobs = null)
    {
        _store = store;
        _excludeNewAccountsFromKuaishou = excludeNewAccountsFromKuaishou;
        _hasWeixinJobs = hasWeixinJobs;
        if (loadAccounts)
            _store.Load();
        foreach (var account in _store.Accounts)
            Accounts.Add(CreateAccountItem(account));
        RefreshAccountFilter();
        SelectedAccount = Accounts.FirstOrDefault(IsInWorkspace);
        _selectedFinalAction = FinalActionChoices[0];
    }

    // 设计时无参构造，供 XAML 预览。
    public MainViewModel() : this(new AccountStore())
    {
    }

    [RelayCommand]
    private void AddAccount()
    {
        var account = _store.Add($"账号{Accounts.Count(IsInWorkspace) + 1}", _excludeNewAccountsFromKuaishou,
            weixinWorkspaceAccount: _hasWeixinJobs is not null);
        var vm = CreateAccountItem(account);
        Accounts.Add(vm);
        if (string.IsNullOrEmpty(AccountSearchText))
            RefreshAccountFilter();
        else
            AccountSearchText = "";
        SelectedAccount = vm;
        StatusMessage = $"已添加「{account.Name}」，点击「登录」扫码（每账号独立会话，登录一次长期保持）";
    }

    [RelayCommand]
    private void RemoveAccount()
    {
        if (SelectedAccount is null) return;
        var vm = SelectedAccount;
        _store.Remove(vm.Model);
        Accounts.Remove(vm);
        RefreshAccountFilter();
        SelectedAccount = Accounts.FirstOrDefault(IsInWorkspace);
        StatusMessage = $"已删除「{vm.Name}」";
    }

    [RelayCommand]
    private void Login()
    {
        if (SelectedAccount is null)
        {
            StatusMessage = "请先在左侧选择一个账号";
            return;
        }

        SelectedAccount.Status = AccountStatus.LoggingIn;
        StatusMessage = $"[{SelectedAccount.Name}] 打开视频号登录页扫码…";
        NavigateRequested?.Invoke(SelectedAccount, ChannelsLoginUrl);
    }

    [RelayCommand]
    private void SaveAccountConfig()
    {
        if (SelectedAccount is null)
        {
            StatusMessage = "请先在左侧选择账号";
            return;
        }
        SelectedAccount.Name = string.IsNullOrWhiteSpace(SelectedAccount.Name)
            ? SelectedAccount.Id
            : SelectedAccount.Name.Trim();
        SelectedAccount.Nickname = SelectedAccount.Nickname?.Trim() ?? string.Empty;
        SelectedAccount.CostReportCompanyName = SelectedAccount.CostReportCompanyName?.Trim() ?? string.Empty;
        SelectedAccount.CostReportTemplatePath = SelectedAccount.CostReportTemplatePath?.Trim() ?? string.Empty;
        SelectedAccount.CostReportSignPath = SelectedAccount.CostReportSignPath?.Trim() ?? string.Empty;
        SelectedAccount.CostReportSealPath = SelectedAccount.CostReportSealPath?.Trim() ?? string.Empty;
        SelectedAccount.CostReportLegalRepresentative = SelectedAccount.CostReportLegalRepresentative?.Trim() ?? string.Empty;
        SelectedAccount.CostReportActorPayRatio = SelectedAccount.CostReportActorPayRatio?.Trim() ?? string.Empty;
        if (_hasWeixinJobs is null)
        {
            SelectedAccount.KuaishouPersonalAccount = SelectedAccount.KuaishouPersonalAccount?.Trim() ?? string.Empty;
            SelectedAccount.KuaishouPersonalConfigPath = SelectedAccount.KuaishouPersonalConfigPath?.Trim() ?? string.Empty;
            SelectedAccount.KuaishouEnterpriseAccount = SelectedAccount.KuaishouEnterpriseAccount?.Trim() ?? string.Empty;
            SelectedAccount.KuaishouEnterpriseConfigPath = SelectedAccount.KuaishouEnterpriseConfigPath?.Trim() ?? string.Empty;
        }
        SelectedAccount.WorkRootDirectory = SelectedAccount.WorkRootDirectory?.Trim() ?? string.Empty;
        SelectedAccount.DownloadDirectory = SelectedAccount.DownloadDirectory?.Trim() ?? string.Empty;
        SelectedAccount.ArchiveRootDirectory = SelectedAccount.ArchiveRootDirectory?.Trim() ?? string.Empty;
        _store.Update(SelectedAccount.Model);
        RefreshAccountFilter();
        StatusMessage = $"[{SelectedAccount.Name}] 账号配置已保存";
        OnPropertyChanged(nameof(CurrentAccountTitle));
        AccountSettingsSaved?.Invoke(SelectedAccount.Model);
    }

    [RelayCommand]
    private void ClearLogs()
    {
        Logs.Clear();
        StatusMessage = "运行日志已清空";
    }
}
