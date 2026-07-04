using CommunityToolkit.Mvvm.ComponentModel;
using ShortDrama.Desktop.Services;

namespace ShortDrama.Desktop.ViewModels;

public sealed partial class MaterialUploadAccountItemViewModel : ViewModelBase
{
    public MaterialUploadAccountItemViewModel(
        string id,
        string name,
        string authFile,
        string browserProfileDir,
        bool isActive = false)
    {
        Id = id;
        this.name = string.IsNullOrWhiteSpace(name) ? id : name.Trim();
        this.authFile = authFile;
        this.browserProfileDir = browserProfileDir;
        this.isActive = isActive;
    }

    public string Id { get; }

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string authFile;

    [ObservableProperty]
    private string browserProfileDir;

    [ObservableProperty]
    private bool isActive;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    public string LoginStatusText => File.Exists(ExpandPath(AuthFile))
        ? "授权文件存在（可能已登录）"
        : "未找到授权文件";

    public string ActiveBadge => IsActive ? "当前账号" : string.Empty;

    public string SecondaryText => string.IsNullOrWhiteSpace(Id) ? LoginStatusText : $"{Id} · {LoginStatusText}";

    public DesktopStateService.MaterialUploadAccountState ToState() =>
        new(Id, DisplayName, AuthFile, BrowserProfileDir);

    public void RefreshFileState()
    {
        OnPropertyChanged(nameof(LoginStatusText));
        OnPropertyChanged(nameof(SecondaryText));
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnAuthFileChanged(string value)
    {
        OnPropertyChanged(nameof(LoginStatusText));
        OnPropertyChanged(nameof(SecondaryText));
    }

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ActiveBadge));
    }

    private static string ExpandPath(string? path)
    {
        var text = (path ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (text.StartsWith("~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            text = Path.Combine(home, text.TrimStart('~', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        try
        {
            return Path.GetFullPath(text);
        }
        catch
        {
            return text;
        }
    }
}
