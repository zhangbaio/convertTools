using CommunityToolkit.Mvvm.ComponentModel;
using PlatformPublisher.Core.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class PublishJobRowViewModel : ObservableObject
{
    public PublishJobRowViewModel(PublishJob model) => Model = model;

    public PublishJob Model { get; }
    public string Id => Model.Id;
    public PublishPlatform Platform => Model.Platform;
    public string PlatformName => Model.Platform.DisplayName();
    public string KindName => Model.Kind.DisplayName();
    public string ProjectName => Model.ProjectName;
    public string ProjectDirectory => Model.ProjectDirectory;
    public string AccountName => string.IsNullOrWhiteSpace(Model.AccountName) ? "默认账号" : Model.AccountName;
    public string StatusText => Model.Status switch
    {
        PublishJobStatus.Pending => "等待执行",
        PublishJobStatus.Running => "执行中",
        PublishJobStatus.Succeeded => "已完成",
        PublishJobStatus.Failed => "失败",
        PublishJobStatus.Blocked => "待接入",
        _ => Model.Status.ToString(),
    };
    public string StatusMessage => Model.StatusMessage;

    public void Refresh()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusMessage));
    }
}
