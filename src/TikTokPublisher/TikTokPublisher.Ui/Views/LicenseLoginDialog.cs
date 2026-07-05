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
    LicenseState State);

public sealed class LicenseLoginDialog : Window
{
    private readonly TextBox _serverUrlBox;
    private readonly TextBox _accountBox;
    private readonly TextBox _passwordBox;
    private readonly TextBlock _statusText;
    private readonly Button _loginButton;
    private readonly Button _cancelButton;

    private LicenseLoginDialog(
        string serverUrl,
        string account,
        string password,
        string message)
    {
        Title = "软件授权登录";
        Width = 560;
        Height = 330;
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
        var dialog = new LicenseLoginDialog(serverUrl, account, password, message);
        return owner is null
            ? dialog.ShowDialog<LicenseLoginDialogResult?>(new Window())
            : dialog.ShowDialog<LicenseLoginDialogResult?>(owner);
    }

    private async Task LoginAsync()
    {
        SetBusy(true);
        _statusText.Text = "正在登录并校验授权...";
        var serverUrl = (_serverUrlBox.Text ?? "").Trim().TrimEnd('/');
        var account = (_accountBox.Text ?? "").Trim();
        var password = _passwordBox.Text ?? "";

        try
        {
            var state = await LicenseAuthService.LoginAsync(serverUrl, account, password);
            Close(new LicenseLoginDialogResult(serverUrl, account, password, state));
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
