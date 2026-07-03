using TikTokPublisher.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class AccountItemViewModel : ViewModelBase
{
    public TikTokAccountProfile Model { get; }

    public AccountItemViewModel(TikTokAccountProfile model)
    {
        Model = model;
        _name = model.Name;
        _status = model.Status;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private AccountStatus _status;

    public string Id => Model.Id;
    public string DisplayName => Model.DisplayName;

    public string LoginEmail =>
        (Model.TiktokLastLoginEmail ?? Model.TiktokLoginEmail ?? "").Trim();

    public string Subtitle =>
        !string.IsNullOrWhiteSpace(LoginEmail) ? LoginEmail : Id;

    public string StatusText => Status switch
    {
        AccountStatus.Online => "在线",
        AccountStatus.LoggingIn => "登录中",
        _ => "离线",
    };

    partial void OnNameChanged(string value)
    {
        Model.Name = value;
    }

    partial void OnStatusChanged(AccountStatus value)
    {
        Model.Status = value;
        OnPropertyChanged(nameof(StatusText));
    }

    public void RefreshFromModel()
    {
        Name = Model.Name;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(LoginEmail));
        OnPropertyChanged(nameof(StatusText));
    }
}
