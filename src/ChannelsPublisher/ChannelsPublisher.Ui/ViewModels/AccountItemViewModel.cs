using ChannelsPublisher.Core.Models;
using ChannelsPublisher.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChannelsPublisher.Desktop.ViewModels;

/// <summary>账号列表项。包裹领域模型 PublishAccount，暴露可观察的名称/状态给左侧列表。</summary>
public sealed partial class AccountItemViewModel : ViewModelBase
{
    public PublishAccount Model { get; }
    private readonly Func<PublishAccount, string>? _weixinDirectory;

    public AccountItemViewModel(PublishAccount model, Func<PublishAccount, string>? weixinDirectory = null)
    {
        Model = model;
        _weixinDirectory = weixinDirectory;
        _name = weixinDirectory is null ? model.Name : WeixinWorkspaceAccounts.AccountName(model);
        _nickname = weixinDirectory is null ? model.Nickname : WeixinWorkspaceAccounts.Nickname(model);
        _status = weixinDirectory is null ? model.Status : AccountStatus.Offline;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _nickname;
    [ObservableProperty] private AccountStatus _status;

    public string Id => Model.Id;
    public string ProfileDir => Model.ProfileDir;
    private DateTimeOffset? LastLogin => _weixinDirectory is null ? Model.LastLoginAt : WeixinWorkspaceAccounts.LastLogin(Model);
    private bool HasSavedLogin => LastLogin is not null || (_weixinDirectory is not null && WeixinWorkspaceAccounts.HasSavedSession(Model));
    public string DisplayName => _weixinDirectory is not null && !string.IsNullOrWhiteSpace(Nickname) ? Nickname : Name;
    public string LastLoginText => LastLogin?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "尚未记录";
    public string LoginStatusText => HasSavedLogin ? "已保存登录状态（可能已登录）" : "未保存登录状态";
    public string CostReportCompanyName { get => Model.CostReportCompanyName; set { if (Model.CostReportCompanyName == value) return; Model.CostReportCompanyName = value; OnPropertyChanged(); } }
    public string CostReportTemplatePath { get => Model.CostReportTemplatePath; set { if (Model.CostReportTemplatePath == value) return; Model.CostReportTemplatePath = value; OnPropertyChanged(); } }
    public string CostReportSignPath { get => Model.CostReportSignPath; set { if (Model.CostReportSignPath == value) return; Model.CostReportSignPath = value; OnPropertyChanged(); } }
    public string CostReportSealPath { get => Model.CostReportSealPath; set { if (Model.CostReportSealPath == value) return; Model.CostReportSealPath = value; OnPropertyChanged(); } }
    public string CostReportLegalRepresentative { get => Model.CostReportLegalRepresentative; set { if (Model.CostReportLegalRepresentative == value) return; Model.CostReportLegalRepresentative = value; OnPropertyChanged(); } }
    public string CostReportActorPayRatio { get => Model.CostReportActorPayRatio; set { if (Model.CostReportActorPayRatio == value) return; Model.CostReportActorPayRatio = value; OnPropertyChanged(); } }
    public string KuaishouPersonalAccount { get => Model.KuaishouPersonalAccount; set { if (Model.KuaishouPersonalAccount == value) return; Model.KuaishouPersonalAccount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlatformSummary)); } }
    public string KuaishouPersonalConfigPath { get => Model.KuaishouPersonalConfigPath; set { if (Model.KuaishouPersonalConfigPath == value) return; Model.KuaishouPersonalConfigPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlatformSummary)); } }
    public string KuaishouEnterpriseAccount { get => Model.KuaishouEnterpriseAccount; set { if (Model.KuaishouEnterpriseAccount == value) return; Model.KuaishouEnterpriseAccount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlatformSummary)); } }
    public string KuaishouEnterpriseConfigPath { get => Model.KuaishouEnterpriseConfigPath; set { if (Model.KuaishouEnterpriseConfigPath == value) return; Model.KuaishouEnterpriseConfigPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlatformSummary)); } }
    public string WorkRootDirectory
    {
        get => _weixinDirectory?.Invoke(Model) ?? Model.WorkRootDirectory;
        set
        {
            if (_weixinDirectory is not null) Model.WeixinWorkRootDirectory = value;
            else Model.WorkRootDirectory = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NeedsWeixinWorkDirectory));
        }
    }
    public bool NeedsWeixinWorkDirectory => _weixinDirectory is not null &&
        Model.WeixinWorkRootDirectory is null && !string.IsNullOrWhiteSpace(Model.WorkRootDirectory) &&
        string.IsNullOrWhiteSpace(WorkRootDirectory);
    public string DownloadDirectory { get => Model.DownloadDirectory; set { if (Model.DownloadDirectory == value) return; Model.DownloadDirectory = value; OnPropertyChanged(); } }
    internal void RefreshWorkDirectory()
    {
        OnPropertyChanged(nameof(WorkRootDirectory));
        OnPropertyChanged(nameof(NeedsWeixinWorkDirectory));
    }
    public string ArchiveRootDirectory { get => Model.ArchiveRootDirectory; set { if (Model.ArchiveRootDirectory == value) return; Model.ArchiveRootDirectory = value; OnPropertyChanged(); } }
    public string LegacySessionSummary => string.IsNullOrWhiteSpace(Model.LegacyProfileId)
        ? "尚未导入旧版登录状态"
        : $"旧账号 {Model.LegacyProfileId} · 最近导入 {Model.LegacySessionImportedAt?.LocalDateTime:yyyy-MM-dd HH:mm}";

    public void MarkLoggedIn(DateTimeOffset timestamp)
    {
        if (_weixinDirectory is not null)
        {
            Model.IsWeixinWorkspaceAccount = true;
            Model.WeixinLastLoginAt = timestamp;
        }
        else Model.LastLoginAt = timestamp;
        Status = AccountStatus.Online;
        OnPropertyChanged(nameof(LastLoginText));
        OnPropertyChanged(nameof(LoginStatusText));
    }

    public void RefreshLegacySessionState()
    {
        OnPropertyChanged(nameof(LegacySessionSummary));
        OnPropertyChanged(nameof(PlatformSummary));
        OnPropertyChanged(nameof(SidebarSubtitle));
    }

    public string StatusText => Status switch
    {
        AccountStatus.Online => "在线",
        AccountStatus.LoggingIn => "登录中",
        _ => HasSavedLogin ? "已保存登录" : _weixinDirectory is null ? "离线" : "待登录",
    };

    public string PlatformSummary => _weixinDirectory is not null ? $"视频号：{StatusText}" :
        $"视频号：{StatusText} · 快手分账：{KuaishouEditionLabel(Model.KuaishouAccountEdition)}";

    public string SidebarSubtitle => $"{StatusText} · {Id}";

    public void NotifyKuaishouEditionChanged() => OnPropertyChanged(nameof(PlatformSummary));

    private static string KuaishouEditionLabel(string? edition) =>
        string.Equals(edition, "enterprise", StringComparison.OrdinalIgnoreCase) ? "企业版"
        : string.Equals(edition, "personal", StringComparison.OrdinalIgnoreCase) ? "个人版"
        : "未设置";

    partial void OnNameChanged(string value)
    {
        if (_weixinDirectory is not null) Model.WeixinAccountName = value;
        else Model.Name = value;
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnNicknameChanged(string value)
    {
        if (_weixinDirectory is not null) Model.WeixinNickname = value;
        else Model.Nickname = value;
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnStatusChanged(AccountStatus value)
    {
        if (_weixinDirectory is null) Model.Status = value;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PlatformSummary));
        OnPropertyChanged(nameof(SidebarSubtitle));
    }
}
