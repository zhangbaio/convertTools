using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PlatformPublisher.Adx.Automation;
using PlatformPublisher.Adx.Models;

namespace PlatformPublisher.Desktop.Views;

public partial class AdxSettingsView : UserControl
{
    private AdxAutomationService? _service;
    private CancellationTokenSource? _cancellation;
    private bool _busy;

    public AdxSettingsView() => InitializeComponent();

    public void Bind(AdxAutomationService service)
    {
        if (_service is not null) _service.LoginStatusChanged -= OnLoginStatusChanged;
        _service = service;
        _service.LoginStatusChanged += OnLoginStatusChanged;
        LoadSettings();
    }

    private void LoadSettings()
    {
        if (_service is null) return;
        var settings = _service.LoadSettings();
        BaseUrlBox.Text = settings.BaseUrl;
        UsernameBox.Text = settings.Username;
        PasswordBox.Text = string.Empty;
        DefaultTopBox.Value = settings.DefaultTopCount;
        QueryLimitBox.Value = settings.QueryLimit;
        ConcurrencyBox.Value = settings.DownloadConcurrency;
        HeadlessBox.IsChecked = settings.Headless;
        ApplyStatus(_service.GetLoginStatus());
    }

    private void OnLoginStatusChanged(object? sender, AdxLoginStatus status) =>
        Dispatcher.UIThread.Post(() => ApplyStatus(status));

    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveAsync(showConfirmation: true);

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        if (_service is null || !TryBegin()) return;
        try
        {
            await SaveAsync(showConfirmation: false);
            StatusText.Text = "正在打开 ADX 并验证登录状态…";
            var status = await _service.LoginAsync(_cancellation!.Token);
            ApplyStatus(status);
        }
        catch (OperationCanceledException) { StatusText.Text = "ADX 登录已取消。"; }
        catch (Exception ex) { StatusText.Text = "ADX 登录失败：" + ex.Message; }
        finally { End(); }
    }

    private void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        try
        {
            _service.Logout();
            ApplyStatus(_service.GetLoginStatus());
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private Task SaveAsync(bool showConfirmation)
    {
        if (_service is null) return Task.CompletedTask;
        _service.SaveSettings(new AdxSettings
        {
            BaseUrl = BaseUrlBox.Text ?? string.Empty,
            Username = UsernameBox.Text ?? string.Empty,
            DefaultTopCount = (int)(DefaultTopBox.Value ?? 5),
            QueryLimit = (int)(QueryLimitBox.Value ?? 50),
            DownloadConcurrency = (int)(ConcurrencyBox.Value ?? 3),
            Headless = HeadlessBox.IsChecked == true,
        });
        if (!string.IsNullOrEmpty(PasswordBox.Text))
        {
            _service.SavePassword(PasswordBox.Text);
            PasswordBox.Text = string.Empty;
        }
        ApplyStatus(_service.GetLoginStatus());
        if (showConfirmation) StatusText.Text = "ADX 配置已保存。";
        return Task.CompletedTask;
    }

    private bool TryBegin()
    {
        if (_busy) return false;
        _busy = true;
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        SetBusy(true);
        return true;
    }

    private void End()
    {
        _busy = false;
        SetBusy(false);
    }

    private void SetBusy(bool value)
    {
        BusyBar.IsVisible = value;
        LoginButton.IsEnabled = !value;
        LogoutButton.IsEnabled = !value;
        SaveButton.IsEnabled = !value;
    }

    private void ApplyStatus(AdxLoginStatus status)
    {
        StatusBadgeText.Text = LoginText(status.State);
        StatusText.Text = status.LastVerifiedAt is { } verified
            ? $"{status.Message} · 最后验证 {verified.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : status.Message ?? string.Empty;
        var (background, foreground) = status.State switch
        {
            AdxLoginState.LoggedIn => ("#E9F8EF", "#087A42"),
            AdxLoginState.Checking => ("#EAF2FF", "#245BD6"),
            AdxLoginState.Failed or AdxLoginState.Expired => ("#FEF0F0", "#D92D20"),
            _ => ("#F2F4F7", "#475467"),
        };
        StatusBadge.Background = Brush.Parse(background);
        StatusBadgeText.Foreground = Brush.Parse(foreground);
        LogoutButton.IsEnabled = !_busy && status.State == AdxLoginState.LoggedIn;
    }

    private static string LoginText(AdxLoginState state) => state switch
    {
        AdxLoginState.LoggedIn => "已登录",
        AdxLoginState.Checking => "验证中",
        AdxLoginState.Failed => "登录失败",
        AdxLoginState.Expired => "已失效",
        AdxLoginState.NotConfigured => "未配置",
        _ => "未登录",
    };
}
