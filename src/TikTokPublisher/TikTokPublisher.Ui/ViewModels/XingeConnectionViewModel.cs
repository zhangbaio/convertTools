using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class XingeConnectionViewModel : ViewModelBase
{
    private readonly string? _databasePath;
    private readonly string _defaultClientName;

    public XingeConnectionViewModel(string? databasePath, string defaultClientName, string hint, bool supportsLoginFollow = false)
    {
        _databasePath = databasePath;
        _defaultClientName = string.IsNullOrWhiteSpace(defaultClientName) ? "TikTokPublisher" : defaultClientName.Trim();
        Hint = hint ?? "";
        SupportsLoginFollow = supportsLoginFollow;
        ClientName = _defaultClientName;
    }

    public string Hint { get; }
    public bool SupportsLoginFollow { get; }
    public bool ShowLegacyToggle => !SupportsLoginFollow;
    public bool ShowManual => !SupportsLoginFollow || ModeIndex == 1;
    public bool ShowAutomatic => SupportsLoginFollow && ModeIndex == 0;
    public string SelectedMode => ModeIndex switch { 1 => XingeRuntimeConnection.Manual, 2 => XingeRuntimeConnection.Off, _ => XingeRuntimeConnection.FollowLogin };
    public string DeviceName => Environment.MachineName;

    public event Action<string>? StatusRequested;
    public event Action? SettingsSaved;

    [ObservableProperty] private bool _showHeading;
    [ObservableProperty] private bool _showSaveButton = true;
    [ObservableProperty] private string _heading = "系统服务";
    [ObservableProperty] private string _status = "未启用";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private string _loginServer = "";
    [ObservableProperty] private string _serverUrl = "";
    [ObservableProperty] private string _account = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _clientName = "";
    [ObservableProperty] private int _pollIntervalSeconds = ClientSettingsDefaults.XingePollIntervalSeconds;
    [ObservableProperty] private string _clientId = "";
    [ObservableProperty] private string _clientToken = "";
    [ObservableProperty] private bool _isBusy;

    public void Load(string? fallbackServerUrl = null, string? fallbackAccount = null, string? fallbackPassword = null)
    {
        var settings = ClientSettingsStore.Load(_databasePath);
        CopyFrom(
            settings.XingeRemoteEnabled,
            FirstNonEmpty(settings.XingeServerUrl, fallbackServerUrl, settings.AuthServerUrl),
            FirstNonEmpty(settings.XingeAccount, fallbackAccount, settings.AuthAccount),
            !string.IsNullOrEmpty(settings.XingePassword)
                ? settings.XingePassword
                : FirstNonEmpty(fallbackPassword, settings.AuthPassword),
            settings.XingeClientId ?? "",
            settings.XingeClientToken ?? "",
            ResolveClientName(settings.XingeClientName),
            ClientSettingsDefaults.NormalizeXingePollIntervalSeconds(settings.XingePollIntervalSeconds),
            settings.XingeRemoteEnabled ? "等待连接" : "未启用",
            SupportsLoginFollow ? XingeRuntimeConnection.Mode(settings) : null);
    }

    public void CopyFrom(
        bool enabled,
        string? serverUrl,
        string? account,
        string? password,
        string? clientId,
        string? clientToken,
        string? clientName,
        int pollIntervalSeconds,
        string? status,
        string? mode = null)
    {
        Enabled = enabled;
        ServerUrl = serverUrl ?? "";
        Account = account ?? "";
        Password = password ?? "";
        ClientId = clientId ?? "";
        ClientToken = clientToken ?? "";
        ClientName = string.IsNullOrWhiteSpace(clientName) ? _defaultClientName : clientName.Trim();
        PollIntervalSeconds = ClientSettingsDefaults.NormalizeXingePollIntervalSeconds(pollIntervalSeconds);
        Status = string.IsNullOrWhiteSpace(status) ? (enabled ? "等待连接" : "未启用") : status;
        if (SupportsLoginFollow)
        {
            ModeIndex = mode switch { XingeRuntimeConnection.Manual => 1, XingeRuntimeConnection.Off => 2, _ => 0 };
            Enabled = ModeIndex != 2;
            LoginServer = XingeRuntimeConnection.ReadSoftwareSession()?.BaseUrl ?? "软件尚未登录";
        }
    }

    public void RefreshCredentials()
    {
        var settings = ClientSettingsStore.Load(_databasePath);
        ClientId = settings.XingeClientId ?? "";
        ClientToken = settings.XingeClientToken ?? "";
        if (!string.IsNullOrWhiteSpace(settings.XingeClientName))
            ClientName = settings.XingeClientName.Trim();
    }

    public void ApplyTo(ClientSettings settings)
    {
        if (SupportsLoginFollow)
            settings.XingeConnectionMode = SelectedMode;
        settings.XingeRemoteEnabled = Enabled;
        if (SupportsLoginFollow && !ShowManual)
            return;
        settings.XingeServerUrl = (ServerUrl ?? "").Trim();
        settings.XingeAccount = (Account ?? "").Trim();
        settings.XingePassword = Password ?? "";
        settings.XingeClientId = (ClientId ?? "").Trim();
        settings.XingeClientToken = (ClientToken ?? "").Trim();
        settings.XingeClientName = string.IsNullOrWhiteSpace(ClientName) ? _defaultClientName : ClientName.Trim();
        settings.XingePollIntervalSeconds = ClientSettingsDefaults.NormalizeXingePollIntervalSeconds(PollIntervalSeconds);
    }

    [RelayCommand]
    private void Save()
    {
        var settings = ClientSettingsStore.Load(_databasePath);
        ApplyTo(settings);
        ClientSettingsStore.Save(settings, _databasePath);
        Status = Enabled ? "等待连接" : "未启用";
        StatusRequested?.Invoke("XINGE 配置已保存");
        SettingsSaved?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanTestLogin))]
    public async Task TestLoginAsync()
    {
        if (IsBusy || !ShowManual)
            return;
        IsBusy = true;
        Status = "正在使用用户名和密码登录 XINGE...";
        try
        {
            var settings = ClientSettingsStore.Load(_databasePath);
            ApplyTo(settings);
            var credentials = await XingeRemoteAccountService.ProvisionAsync(settings);
            ClientId = credentials.ClientId;
            ClientToken = credentials.ClientToken;
            settings.XingeClientId = credentials.ClientId;
            settings.XingeClientToken = credentials.ClientToken;
            settings.XingeCredentialFingerprint = credentials.CredentialFingerprint;
            ClientSettingsStore.Save(settings, _databasePath);
            Status = $"账号登录成功：{credentials.Username}，远程客户端凭证已生成";
            StatusRequested?.Invoke(Status);
            SettingsSaved?.Invoke();
        }
        catch (Exception ex)
        {
            Status = $"XINGE 登录失败：{ex.Message}";
            StatusRequested?.Invoke(Status);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanTestLogin() => !IsBusy && ShowManual;

    partial void OnIsBusyChanged(bool value) => TestLoginCommand.NotifyCanExecuteChanged();

    partial void OnModeIndexChanged(int value)
    {
        if (SupportsLoginFollow)
            Enabled = value != 2;
        OnPropertyChanged(nameof(ShowManual));
        OnPropertyChanged(nameof(ShowAutomatic));
        TestLoginCommand.NotifyCanExecuteChanged();
    }

    private string ResolveClientName(string? configured)
    {
        var name = (configured ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return _defaultClientName;
        if (name.Contains("tiktok", StringComparison.OrdinalIgnoreCase)
            && !_defaultClientName.Contains("tiktok", StringComparison.OrdinalIgnoreCase))
            return _defaultClientName;
        return name;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }
}
