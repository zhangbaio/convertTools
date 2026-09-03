using ChannelsPublisher.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChannelsPublisher.Desktop.ViewModels;

/// <summary>账号列表项。包裹领域模型 PublishAccount，暴露可观察的名称/状态给左侧列表。</summary>
public sealed partial class AccountItemViewModel : ViewModelBase
{
    public PublishAccount Model { get; }

    public AccountItemViewModel(PublishAccount model)
    {
        Model = model;
        _name = model.Name;
        _nickname = model.Nickname;
        _status = model.Status;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _nickname;
    [ObservableProperty] private AccountStatus _status;

    public string Id => Model.Id;
    public string ProfileDir => Model.ProfileDir;
    public string LastLoginText => Model.LastLoginAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "尚未记录";
    public string LoginStatusText => Model.LastLoginAt is null ? "未保存登录状态" : "已保存登录状态（可能已登录）";
    public string CostReportCompanyName { get => Model.CostReportCompanyName; set { if (Model.CostReportCompanyName == value) return; Model.CostReportCompanyName = value; OnPropertyChanged(); } }
    public string CostReportTemplatePath { get => Model.CostReportTemplatePath; set { if (Model.CostReportTemplatePath == value) return; Model.CostReportTemplatePath = value; OnPropertyChanged(); } }
    public string CostReportSignPath { get => Model.CostReportSignPath; set { if (Model.CostReportSignPath == value) return; Model.CostReportSignPath = value; OnPropertyChanged(); } }
    public string CostReportSealPath { get => Model.CostReportSealPath; set { if (Model.CostReportSealPath == value) return; Model.CostReportSealPath = value; OnPropertyChanged(); } }
    public string CostReportLegalRepresentative { get => Model.CostReportLegalRepresentative; set { if (Model.CostReportLegalRepresentative == value) return; Model.CostReportLegalRepresentative = value; OnPropertyChanged(); } }
    public string CostReportActorPayRatio { get => Model.CostReportActorPayRatio; set { if (Model.CostReportActorPayRatio == value) return; Model.CostReportActorPayRatio = value; OnPropertyChanged(); } }
    public string KuaishouPersonalAccount { get => Model.KuaishouPersonalAccount; set { if (Model.KuaishouPersonalAccount == value) return; Model.KuaishouPersonalAccount = value; OnPropertyChanged(); } }
    public string KuaishouPersonalConfigPath { get => Model.KuaishouPersonalConfigPath; set { if (Model.KuaishouPersonalConfigPath == value) return; Model.KuaishouPersonalConfigPath = value; OnPropertyChanged(); } }
    public string KuaishouEnterpriseAccount { get => Model.KuaishouEnterpriseAccount; set { if (Model.KuaishouEnterpriseAccount == value) return; Model.KuaishouEnterpriseAccount = value; OnPropertyChanged(); } }
    public string KuaishouEnterpriseConfigPath { get => Model.KuaishouEnterpriseConfigPath; set { if (Model.KuaishouEnterpriseConfigPath == value) return; Model.KuaishouEnterpriseConfigPath = value; OnPropertyChanged(); } }
    public string WorkRootDirectory { get => Model.WorkRootDirectory; set { if (Model.WorkRootDirectory == value) return; Model.WorkRootDirectory = value; OnPropertyChanged(); } }
    public string DownloadDirectory { get => Model.DownloadDirectory; set { if (Model.DownloadDirectory == value) return; Model.DownloadDirectory = value; OnPropertyChanged(); } }
    public string ArchiveRootDirectory { get => Model.ArchiveRootDirectory; set { if (Model.ArchiveRootDirectory == value) return; Model.ArchiveRootDirectory = value; OnPropertyChanged(); } }

    public void MarkLoggedIn(DateTimeOffset timestamp)
    {
        Model.LastLoginAt = timestamp;
        Status = AccountStatus.Online;
        OnPropertyChanged(nameof(LastLoginText));
        OnPropertyChanged(nameof(LoginStatusText));
    }

    public string StatusText => Status switch
    {
        AccountStatus.Online => "在线",
        AccountStatus.LoggingIn => "登录中",
        _ => "离线",
    };

    partial void OnNameChanged(string value) => Model.Name = value;
    partial void OnNicknameChanged(string value) => Model.Nickname = value;

    partial void OnStatusChanged(AccountStatus value)
    {
        Model.Status = value;
        OnPropertyChanged(nameof(StatusText));
    }
}
