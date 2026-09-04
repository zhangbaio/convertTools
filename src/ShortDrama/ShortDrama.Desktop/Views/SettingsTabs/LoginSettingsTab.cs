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
    private readonly RadioButton _hghighButton;
    private readonly RadioButton _mapleleafButton;
    private readonly RadioButton _hglocalButton;
    private readonly RadioButton _pikachuButton;

    public LoginSettingsTab()
    {
        _hgnewButton = BuildSourceButton("hgnew", "hgnew");
        _hghighButton = BuildSourceButton("hghigh", "hghigh");
        _mapleleafButton = BuildSourceButton("mapleleaf", "Mapleleaf");
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

        panel.Children.Add(Hint("登录设置支持 hgnew / hghigh / Mapleleaf / hglocal / pikachu；短剧搜索、下载和上新只使用当前选择的数据源，不再自动降级。"));
        panel.Children.Add(BuildSourceRow());

        panel.Children.Add(SectionTitle("hgnew"));
        panel.Children.Add(Hint("仅支持 1.4.x AES 协议；设备唯一标识必须是大写 GUID。"));
        panel.Children.Add(Row("账号", BindText(nameof(ConfigWindowViewModel.HgnewAccount))));
        panel.Children.Add(Row("密码", BindPassword(nameof(ConfigWindowViewModel.HgnewPassword))));
        panel.Children.Add(Row("UDID", BuildHgnewUdidRow()));
        panel.Children.Add(Row("下载超时(秒)", BindText(nameof(ConfigWindowViewModel.HongguoDownloadTimeoutSeconds))));
        panel.Children.Add(Row("单集重试次数", BindText(nameof(ConfigWindowViewModel.HongguoEpisodeDownloadAttempts))));
        panel.Children.Add(Row("客户端版本", BindText(nameof(ConfigWindowViewModel.HgnewClientVersion))));
        panel.Children.Add(Row("测试结果", ReadOnlyText(nameof(ConfigWindowViewModel.HgnewProbeStatus))));

        panel.Children.Add(SectionTitle("hghigh"));
        panel.Children.Add(Hint("高码率版与标准版共用账号、密码和 Enc/Sign Master；DeviceId、客户端与登录会话按版本隔离。"));
        panel.Children.Add(Row("客户端版本", BuildHghighEditionCombo()));
        panel.Children.Add(Row("账号", BindText(nameof(ConfigWindowViewModel.HghighAccount))));
        panel.Children.Add(Row("密码", BindPassword(nameof(ConfigWindowViewModel.HghighPassword))));
        panel.Children.Add(Row("高码率 DeviceId", BindText(nameof(ConfigWindowViewModel.HghighDeviceId))));
        panel.Children.Add(Row("高码率客户端 exe", BindText(nameof(ConfigWindowViewModel.HghighClientExe))));
        panel.Children.Add(Row("标准版 DeviceId", BindText(nameof(ConfigWindowViewModel.HghighStandardDeviceId))));
        panel.Children.Add(Row("标准版客户端 exe", BindText(nameof(ConfigWindowViewModel.HghighStandardClientExe))));
        panel.Children.Add(Row("密钥状态", ReadOnlyText(nameof(ConfigWindowViewModel.HghighMastersStatus))));
        panel.Children.Add(Row("测试结果", BuildHghighProbeRow()));

        panel.Children.Add(SectionTitle("Mapleleaf 1.6.5"));
        panel.Children.Add(Hint("独立账号和设备号；搜索、上新与剧集列表走 Mapleleaf，单集播放地址通常需要同时配置 hglocal。"));
        panel.Children.Add(Row("账号", BindText(nameof(ConfigWindowViewModel.MapleleafAccount))));
        panel.Children.Add(Row("密码", BindPassword(nameof(ConfigWindowViewModel.MapleleafPassword))));
        panel.Children.Add(Row("DeviceUDID", BuildMapleleafUdidRow()));
        panel.Children.Add(Row("测试结果", ReadOnlyText(nameof(ConfigWindowViewModel.MapleleafProbeStatus))));

        panel.Children.Add(SectionTitle("hglocal"));
        panel.Children.Add(Row("本地链路地址", BindText(nameof(ConfigWindowViewModel.HongguoLocalBaseUrl))));
        panel.Children.Add(Row("本地链路密钥", BindText(nameof(ConfigWindowViewModel.HongguoLocalApiKey))));

        panel.Children.Add(Row("测试结果", BuildHongguoLocalProbeRow()));

        panel.Children.Add(SectionTitle("pikachu"));
        panel.Children.Add(Row("内容类型", BuildPikachuTypeCombo()));
        panel.Children.Add(Row("代理服务地址", BindText(nameof(ConfigWindowViewModel.PikachuServerUrl))));
        panel.Children.Add(Row("DeviceId", BindText(nameof(ConfigWindowViewModel.PikachuDeviceId))));
        panel.Children.Add(Row("客户端版本", BindText(nameof(ConfigWindowViewModel.PikachuClientVersion))));
        panel.Children.Add(Row("测试结果", BuildPikachuProbeRow()));

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
        row.Children.Add(_hghighButton);
        row.Children.Add(_mapleleafButton);
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
        _hghighButton.IsChecked = string.Equals(viewModel.DramaSourceChain, "hghigh", StringComparison.OrdinalIgnoreCase);
        _mapleleafButton.IsChecked = string.Equals(viewModel.DramaSourceChain, "mapleleaf", StringComparison.OrdinalIgnoreCase);
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

    private static Control BuildHghighProbeRow()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            ColumnSpacing = 8
        };

        var status = ReadOnlyText(nameof(ConfigWindowViewModel.HghighProbeStatus));
        grid.Children.Add(status);
        Grid.SetColumn(status, 0);

        var readButton = new Button { Content = "读取 DeviceId", MinWidth = 110 };
        readButton.Click += ReadHghighDeviceId_Click;
        grid.Children.Add(readButton);
        Grid.SetColumn(readButton, 1);

        var provisionButton = new Button { Content = "提取启动密钥", MinWidth = 120 };
        provisionButton.Click += ProvisionHghigh_Click;
        grid.Children.Add(provisionButton);
        Grid.SetColumn(provisionButton, 2);

        var probeButton = new Button { Content = "测试登录", MinWidth = 96 };
        probeButton.Click += ProbeHghighLogin_Click;
        grid.Children.Add(probeButton);
        Grid.SetColumn(probeButton, 3);

        return grid;
    }

    private static Control BuildMapleleafUdidRow()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            ColumnSpacing = 8
        };
        var text = BindText(nameof(ConfigWindowViewModel.MapleleafUdid));
        grid.Children.Add(text);
        var readButton = new Button { Content = "读取 DeviceUDID", MinWidth = 120 };
        readButton.Click += ReadMapleleafUdid_Click;
        grid.Children.Add(readButton);
        Grid.SetColumn(readButton, 1);
        var generateButton = new Button { Content = "生成", MinWidth = 72 };
        generateButton.Click += GenerateMapleleafUdid_Click;
        grid.Children.Add(generateButton);
        Grid.SetColumn(generateButton, 2);
        var probeButton = new Button { Content = "测试登录", MinWidth = 96 };
        probeButton.Click += ProbeMapleleafLogin_Click;
        grid.Children.Add(probeButton);
        Grid.SetColumn(probeButton, 3);
        return grid;
    }

    private static Control BuildHongguoLocalProbeRow()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8
        };

        var status = ReadOnlyText(nameof(ConfigWindowViewModel.HongguoLocalProbeStatus));
        grid.Children.Add(status);
        Grid.SetColumn(status, 0);

        var probeButton = new Button
        {
            Content = "测试 hglocal",
            MinWidth = 110
        };
        probeButton.Click += ProbeHongguoLocal_Click;
        grid.Children.Add(probeButton);
        Grid.SetColumn(probeButton, 1);

        return grid;
    }

    private static Control BuildPikachuProbeRow()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 8
        };

        var status = ReadOnlyText(nameof(ConfigWindowViewModel.PikachuProbeStatus));
        grid.Children.Add(status);
        Grid.SetColumn(status, 0);

        var readButton = new Button
        {
            Content = "读取 DeviceId",
            MinWidth = 110
        };
        readButton.Click += ReadPikachuRuntime_Click;
        grid.Children.Add(readButton);
        Grid.SetColumn(readButton, 1);

        var probeButton = new Button
        {
            Content = "测试 pikachu",
            MinWidth = 110
        };
        probeButton.Click += ProbePikachu_Click;
        grid.Children.Add(probeButton);
        Grid.SetColumn(probeButton, 2);

        return grid;
    }

    private static Control BuildPikachuTypeCombo()
    {
        var combo = new ComboBox
        {
            ItemsSource = new[] { "manga" }
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

    private static void ReadHghighDeviceId_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConfigWindowViewModel viewModel })
        {
            viewModel.ReadHghighDeviceId();
        }
    }

    private static async void ProvisionHghigh_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConfigWindowViewModel viewModel })
        {
            await viewModel.ProvisionHghighMastersAsync();
        }
    }

    private static async void ProbeHghighLogin_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConfigWindowViewModel viewModel })
        {
            await viewModel.ProbeHghighLoginAsync();
        }
    }

    private static Control BuildHghighEditionCombo()
    {
        var combo = new ComboBox
        {
            ItemsSource = new[] { "high", "standard" }
        };
        combo[!SelectingItemsControl.SelectedItemProperty] = new Binding(nameof(ConfigWindowViewModel.HghighEdition))
        {
            Mode = BindingMode.TwoWay
        };
        return combo;
    }

    private static void ReadMapleleafUdid_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConfigWindowViewModel viewModel })
            viewModel.ReadMapleleafUdid();
    }

    private static void GenerateMapleleafUdid_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConfigWindowViewModel viewModel })
            viewModel.GenerateMapleleafUdid();
    }

    private static async void ProbeMapleleafLogin_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConfigWindowViewModel viewModel })
            await viewModel.ProbeMapleleafLoginAsync();
    }

    private static async void ProbeHongguoLocal_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConfigWindowViewModel viewModel })
        {
            await viewModel.ProbeHongguoLocalAsync();
        }
    }

    private static async void ReadPikachuRuntime_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConfigWindowViewModel viewModel })
        {
            await viewModel.ReadPikachuRuntimeAsync();
        }
    }

    private static async void ProbePikachu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConfigWindowViewModel viewModel })
        {
            await viewModel.ProbePikachuAsync();
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
        // 输入即写回 ViewModel，避免焦点仍在框内点「保存」时仍用旧值
        textBox[!TextBox.TextProperty] = new Binding(propertyName)
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        };
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
