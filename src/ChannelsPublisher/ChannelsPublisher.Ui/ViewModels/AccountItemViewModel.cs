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
        _status = model.Status;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private AccountStatus _status;

    public string Id => Model.Id;

    public string StatusText => Status switch
    {
        AccountStatus.Online => "在线",
        AccountStatus.LoggingIn => "登录中",
        _ => "离线",
    };

    partial void OnNameChanged(string value) => Model.Name = value;

    partial void OnStatusChanged(AccountStatus value)
    {
        Model.Status = value;
        OnPropertyChanged(nameof(StatusText));
    }
}
