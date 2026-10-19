using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Licensing;

namespace TikTokPublisher.Ui.Views;

public sealed record LicenseLoginDialogResult(
    string ServerUrl,
    string Account,
    string Password,
    LicenseState State,
    string LicenseCode = "");

public sealed class LicenseLoginDialog : Window
{
    private readonly TextBox _serverUrlBox;
    private readonly TextBox _accountBox;
    private readonly TextBox _passwordBox;
    private readonly TextBox? _licenseCodeBox;
    private readonly TextBlock _statusText;
    private readonly Button _loginButton;
    private readonly Button _cancelButton;
    private LicenseLoginDialogResult? _result;

    private LicenseLoginDialog(
        string serverUrl,
        string account,
        string password,
        string message,
        bool requireLicenseCode = false,
        string licenseCode = "")
    {
        Title = "软件授权登录";
        Width = 560;
        Height = requireLicenseCode ? 390 : 330;
        MinWidth = 520;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
        };

        root.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(message)
                ? "请输入授权账号，登录成功后会保存到 C# 客户端独立的 license_state.bin。"
                : message,
            TextWrapping = TextWrapping.Wrap,
        });

        var form = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
            RowSpacing = 10,
            ColumnSpacing = 12,
        };

        form.Children.Add(BuildLabel("服务器地址", 0));
        _serverUrlBox = new TextBox
        {
            Text = serverUrl,
            MinWidth = 360,
        };
        Grid.SetRow(_serverUrlBox, 0);
        Grid.SetColumn(_serverUrlBox, 1);
        form.Children.Add(_serverUrlBox);

        form.Children.Add(BuildLabel("账号", 1));
        _accountBox = new TextBox
        {
            Text = account,
            Watermark = "用户名 / 邮箱",
        };
        Grid.SetRow(_accountBox, 1);
        Grid.SetColumn(_accountBox, 1);
        form.Children.Add(_accountBox);

        form.Children.Add(BuildLabel("密码", 2));
        _passwordBox = new TextBox
        {
            Text = password,
            PasswordChar = '*',
            RevealPassword = false,
        };
        Grid.SetRow(_passwordBox, 2);
        Grid.SetColumn(_passwordBox, 1);
        form.Children.Add(_passwordBox);

        if (requireLicenseCode)
        {
            form.Children.Add(BuildLabel("授权码", 3));
            _licenseCodeBox = new TextBox
            {
                Text = licenseCode,
                Watermark = "请输入授权码",
            };
            Grid.SetRow(_licenseCodeBox, 3);
            Grid.SetColumn(_licenseCodeBox, 1);
            form.Children.Add(_licenseCodeBox);
        }
        else
        {
            _licenseCodeBox = null;
        }

        root.Children.Add(form);

        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 38,
        };
        root.Children.Add(_statusText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        _loginButton = new Button { Content = "登录", MinWidth = 96, Classes = { "primaryAction" } };
        _cancelButton = new Button { Content = "取消", MinWidth = 96 };
        _loginButton.Click += async (_, _) => await LoginAsync();
        _cancelButton.Click += (_, _) => Close(null);
        buttons.Children.Add(_loginButton);
        buttons.Children.Add(_cancelButton);
        root.Children.Add(buttons);

        Content = root;

        Opened += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_accountBox.Text))
                _accountBox.Focus();
            else if (string.IsNullOrEmpty(_passwordBox.Text))
                _passwordBox.Focus();
            else if (_licenseCodeBox is not null && string.IsNullOrWhiteSpace(_licenseCodeBox.Text))
                _licenseCodeBox.Focus();
            else
                _loginButton.Focus();
        };
    }

    public static Task<LicenseLoginDialogResult?> ShowAsync(
        Window? owner,
        string serverUrl,
        string account,
        string password,
        string message = "")
    {
        if (owner is null)
            return ShowStandaloneAsync(serverUrl, account, password, message);

        var dialog = new LicenseLoginDialog(serverUrl, account, password, message);
        return dialog.ShowDialog<LicenseLoginDialogResult?>(owner);
    }

    public static Task<LicenseLoginDialogResult?> ShowStandaloneAsync(
        string serverUrl,
        string account,
        string password,
        string message = "",
        bool requireLicenseCode = false,
        string licenseCode = "")
    {
        var dialog = new LicenseLoginDialog(serverUrl, account, password, message, requireLicenseCode, licenseCode)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        var tcs = new TaskCompletionSource<LicenseLoginDialogResult?>();
        dialog.Closed += (_, _) => tcs.TrySetResult(dialog._result);
        dialog.Show();
        return tcs.Task;
    }

    private async Task LoginAsync()
    {
        SetBusy(true);
        _statusText.Text = "正在登录并校验授权...";
        var serverUrl = (_serverUrlBox.Text ?? "").Trim().TrimEnd('/');
        var account = (_accountBox.Text ?? "").Trim();
        var password = _passwordBox.Text ?? "";
        var licenseCode = (_licenseCodeBox?.Text ?? "").Trim();

        try
        {
            var state = _licenseCodeBox is null
                ? await LicenseAuthService.LoginAsync(serverUrl, account, password)
                : await LicenseAuthService.LoginWithLicenseCodeAsync(serverUrl, account, password, licenseCode);
            _result = new LicenseLoginDialogResult(serverUrl, account, password, state, licenseCode);
            Close(_result);
        }
        catch (Exception ex)
        {
            _statusText.Text = ex.Message;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _loginButton.IsEnabled = !busy;
        _cancelButton.IsEnabled = !busy;
        _serverUrlBox.IsEnabled = !busy;
        _accountBox.IsEnabled = !busy;
        _passwordBox.IsEnabled = !busy;
        if (_licenseCodeBox is not null)
            _licenseCodeBox.IsEnabled = !busy;
    }

    private static TextBlock BuildLabel(string text, int row)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        return label;
    }
}
