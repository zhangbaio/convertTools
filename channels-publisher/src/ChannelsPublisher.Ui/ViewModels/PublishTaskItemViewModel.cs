using ChannelsPublisher.Core.Publishing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChannelsPublisher.Desktop.ViewModels;

public enum PublishTaskStatus
{
    Pending,
    Running,
    Done,
    Failed,
}

/// <summary>发布任务列表项：一条素材 + 目标账号 + 可观察状态。</summary>
public sealed partial class PublishTaskItemViewModel : ViewModelBase
{
    public PublishItem Item { get; }
    public AccountItemViewModel Account { get; }

    public PublishTaskItemViewModel(PublishItem item, AccountItemViewModel account)
    {
        Item = item;
        Account = account;
    }

    public string VideoName => Item.DisplayName;
    public string AccountName => Account.Name;
    public string DramaName => Item.DramaName ?? "";

    [ObservableProperty] private PublishTaskStatus _status = PublishTaskStatus.Pending;
    [ObservableProperty] private string _message = "待发布";

    public string StatusText => Status switch
    {
        PublishTaskStatus.Running => "发布中",
        PublishTaskStatus.Done => "完成",
        PublishTaskStatus.Failed => "失败",
        _ => "待发布",
    };

    partial void OnStatusChanged(PublishTaskStatus value) => OnPropertyChanged(nameof(StatusText));
}
