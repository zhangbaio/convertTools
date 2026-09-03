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

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    [ObservableProperty] private AccountItemViewModel? _selectedAccount;
    [ObservableProperty] private string _statusMessage = "就绪";
    public string CurrentAccountTitle => SelectedAccount?.Name ?? "请先在左侧选择账号";
    public string CurrentLoginStatusText => SelectedAccount?.LoginStatusText ?? "尚未选择账号";
    public string CurrentLastLoginText => SelectedAccount?.LastLoginText ?? "-";
    public string CurrentRuntimeStatusText => SelectedAccount?.StatusText ?? "未选择";

    partial void OnSelectedAccountChanged(AccountItemViewModel? value)
    {
        OnPropertyChanged(nameof(CurrentAccountTitle));
        OnPropertyChanged(nameof(CurrentLoginStatusText));
        OnPropertyChanged(nameof(CurrentLastLoginText));
        OnPropertyChanged(nameof(CurrentRuntimeStatusText));
    }

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

    public void RecordAccountLogin(AccountItemViewModel account)
    {
        account.MarkLoggedIn(DateTimeOffset.Now);
        _store.Update(account.Model);
        StatusMessage = $"[{account.Name}] 登录会话已就绪";
    }

    /// <summary>请求视图把某账号的内嵌浏览器导航到 url（View 侧持有 WebView2Host）。</summary>
    public event Action<AccountItemViewModel, string>? NavigateRequested;

    public MainViewModel(AccountStore store)
    {
        _store = store;
        _store.Load();
        foreach (var account in _store.Accounts)
            Accounts.Add(new AccountItemViewModel(account));
        SelectedAccount = Accounts.Count > 0 ? Accounts[0] : null;
        _selectedFinalAction = FinalActionChoices[0];
    }

    // 设计时无参构造，供 XAML 预览。
    public MainViewModel() : this(new AccountStore())
    {
    }

    [RelayCommand]
    private void AddAccount()
    {
        var account = _store.Add($"账号{Accounts.Count + 1}");
        var vm = new AccountItemViewModel(account);
        Accounts.Add(vm);
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
        SelectedAccount = Accounts.Count > 0 ? Accounts[0] : null;
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
        SelectedAccount.KuaishouPersonalAccount = SelectedAccount.KuaishouPersonalAccount?.Trim() ?? string.Empty;
        SelectedAccount.KuaishouPersonalConfigPath = SelectedAccount.KuaishouPersonalConfigPath?.Trim() ?? string.Empty;
        SelectedAccount.KuaishouEnterpriseAccount = SelectedAccount.KuaishouEnterpriseAccount?.Trim() ?? string.Empty;
        SelectedAccount.KuaishouEnterpriseConfigPath = SelectedAccount.KuaishouEnterpriseConfigPath?.Trim() ?? string.Empty;
        SelectedAccount.WorkRootDirectory = SelectedAccount.WorkRootDirectory?.Trim() ?? string.Empty;
        SelectedAccount.DownloadDirectory = SelectedAccount.DownloadDirectory?.Trim() ?? string.Empty;
        SelectedAccount.ArchiveRootDirectory = SelectedAccount.ArchiveRootDirectory?.Trim() ?? string.Empty;
        _store.Update(SelectedAccount.Model);
        StatusMessage = $"[{SelectedAccount.Name}] 账号配置已保存";
    }

    [RelayCommand]
    private void ClearLogs()
    {
        Logs.Clear();
        StatusMessage = "运行日志已清空";
    }
}
