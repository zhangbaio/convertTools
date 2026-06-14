using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views.SettingsTabs;

public sealed class LoginSettingsTab : UserControl
{
    private readonly RadioButton _hgnewButton;
    private readonly RadioButton _hglocalButton;
    private readonly RadioButton _pikachuButton;

    public LoginSettingsTab()
    {
        _hgnewButton = BuildSourceButton("hgnew", "hgnew");
        _hglocalButton = BuildSourceButton("hglocal", "hglocal");
        _pikachuButton = BuildSourceButton("pikachu", "pikachu");
        DataContextChanged += (_, _) => SyncSourceButtons();

        Content = new ScrollViewer
        {
            Content = BuildContent()
        };
    }

    private Control BuildContent()
    {
        var panel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16)
        };

        panel.Children.Add(Hint("登录设置只保留 hgnew / hglocal / pikachu 三条链路，不再接入 52API。"));
        panel.Children.Add(BuildSourceRow());
        panel.Children.Add(Row("搜索服务顺序", BindText(nameof(ConfigWindowViewModel.DramaServiceOrderSearch))));
        panel.Children.Add(Row("下载服务顺序", BindText(nameof(ConfigWindowViewModel.DramaServiceOrderDownload))));
        panel.Children.Add(Row("上新服务顺序", BindText(nameof(ConfigWindowViewModel.DramaServiceOrderNewRelease))));
        panel.Children.Add(Row("排名服务顺序", BindText(nameof(ConfigWindowViewModel.DramaServiceOrderRanking))));

        panel.Children.Add(SectionTitle("hgnew"));
        panel.Children.Add(Row("账号", BindText(nameof(ConfigWindowViewModel.HgnewAccount))));
        panel.Children.Add(Row("密码", BindPassword(nameof(ConfigWindowViewModel.HgnewPassword))));
        panel.Children.Add(Row("UDID", BuildHgnewUdidRow()));
        panel.Children.Add(Row("客户端版本", BindText(nameof(ConfigWindowViewModel.HgnewClientVersion))));
        panel.Children.Add(Row("测试结果", ReadOnlyText(nameof(ConfigWindowViewModel.HgnewProbeStatus))));

        panel.Children.Add(SectionTitle("hglocal"));
        panel.Children.Add(Row("本地链路地址", BindText(nameof(ConfigWindowViewModel.HongguoLocalBaseUrl))));
        panel.Children.Add(Row("本地链路密钥", BindText(nameof(ConfigWindowViewModel.HongguoLocalApiKey))));

        panel.Children.Add(SectionTitle("pikachu"));
        panel.Children.Add(Row("内容类型", BuildPikachuTypeCombo()));
        panel.Children.Add(Row("代理服务地址", BindText(nameof(ConfigWindowViewModel.PikachuServerUrl))));
        panel.Children.Add(Row("番茄 Cookie", MultiLineText(nameof(ConfigWindowViewModel.PikachuFanqieCookie), 110)));

        return panel;
    }

    private Control BuildSourceRow()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        row.Children.Add(_hgnewButton);
        row.Children.Add(_hglocalButton);
        row.Children.Add(_pikachuButton);
        return row;
    }

    private RadioButton BuildSourceButton(string value, string label)
    {
        var button = new RadioButton
        {
            Content = label,
            GroupName = "DramaSourceChain"
        };
        button.Checked += (_, _) =>
        {
            if (DataContext is ConfigWindowViewModel viewModel)
            {
                viewModel.DramaSourceChain = value;
            }
        };
        return button;
    }

    private void SyncSourceButtons()
    {
        if (DataContext is not ConfigWindowViewModel viewModel)
        {
            return;
        }

        _hgnewButton.IsChecked = string.Equals(viewModel.DramaSourceChain, "hgnew", StringComparison.OrdinalIgnoreCase);
        _hglocalButton.IsChecked = string.Equals(viewModel.DramaSourceChain, "hglocal", StringComparison.OrdinalIgnoreCase);
        _pikachuButton.IsChecked = string.Equals(viewModel.DramaSourceChain, "pikachu", StringComparison.OrdinalIgnoreCase);
    }

    private static Control BuildHgnewUdidRow()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            ColumnSpacing = 8
        };
        var textBox = BindText(nameof(ConfigWindowViewModel.HgnewUdid));
        grid.Children.Add(textBox);
        Grid.SetColumn(textBox, 0);

        var readButton = new Button
        {
            Content = "读取 DeviceUDID",
            MinWidth = 120
        };
        readButton.Click += ReadHgnewUdid_Click;
        grid.Children.Add(readButton);
        Grid.SetColumn(readButton, 1);

        var generateButton = new Button
        {
            Content = "生成 UUID",
            MinWidth = 96
        };
        generateButton.Click += GenerateHgnewUdid_Click;
        grid.Children.Add(generateButton);
        Grid.SetColumn(generateButton, 2);

        var probeButton = new Button
        {
            Content = "测试登录",
            MinWidth = 96
        };
        probeButton.Click += ProbeHgnewLogin_Click;
        grid.Children.Add(probeButton);
        Grid.SetColumn(probeButton, 3);

        return grid;
    }

    private static Control BuildPikachuTypeCombo()
    {
        var combo = new ComboBox
        {
            ItemsSource = new[] { "short", "manga" }
        };
        combo[!SelectingItemsControl.SelectedItemProperty] = new Binding(nameof(ConfigWindowViewModel.PikachuDramaType));
        return combo;
    }

    private static async void GenerateHgnewUdid_Click(object? sender, RoutedEventArgs e)
    {
        await Task.Yield();
        if (sender is Control { DataContext: ConfigWindowViewModel viewModel })
        {
            viewModel.HgnewUdid = Guid.NewGuid().ToString().ToUpperInvariant();
        }
    }

    private static void ReadHgnewUdid_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConfigWindowViewModel viewModel })
        {
            viewModel.ReadHgnewDeviceUdid();
        }
    }

    private static async void ProbeHgnewLogin_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConfigWindowViewModel viewModel })
        {
            await viewModel.ProbeHgnewLoginAsync();
        }
    }

    private static TextBlock SectionTitle(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 6, 0, 0)
        };
    }

    private static TextBox BindText(string propertyName)
    {
        var textBox = new TextBox();
        textBox[!TextBox.TextProperty] = new Binding(propertyName);
        return textBox;
    }

    private static TextBox BindPassword(string propertyName)
    {
        var textBox = BindText(propertyName);
        textBox.PasswordChar = '*';
        return textBox;
    }

    private static TextBox MultiLineText(string propertyName, double minHeight)
    {
        var textBox = BindText(propertyName);
        textBox.AcceptsReturn = true;
        textBox.TextWrapping = TextWrapping.Wrap;
        textBox.MinHeight = minHeight;
        return textBox;
    }

    private static TextBox ReadOnlyText(string propertyName)
    {
        var textBox = BindText(propertyName);
        textBox.IsReadOnly = true;
        return textBox;
    }

    private static Control Row(string label, Control editor)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("220,*"),
            ColumnSpacing = 12
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center
        });
        grid.Children.Add(editor);
        Grid.SetColumn(editor, 1);
        return grid;
    }

    private static TextBlock Hint(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };
    }
}
