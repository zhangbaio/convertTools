using TikTokPublisher.Core.Publishing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TikTokPublisher.Ui.ViewModels;

public enum PublishTaskStatus
{
    Pending,
    Running,
    Done,
    Failed,
}

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
    public string AccountName => Account.DisplayName;
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
