using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using ShortDrama.Desktop.ViewModels;
using ShortDrama.Desktop.Views.SettingsTabs;
using System.ComponentModel;

namespace ShortDrama.Desktop.Views;

public partial class ConfigView : UserControl
{
    private bool _tabsInitialized;
    private MainWindowViewModel? _mainWindowViewModel;

    public ConfigView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public bool WasSaved => ViewModel?.WasSaved == true;

    private ConfigWindowViewModel? ViewModel => RootGrid.DataContext as ConfigWindowViewModel;

    private async void PickRootDir_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not TopLevel topLevel)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择工作目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            ViewModel?.SetRootDir(folder.Path.LocalPath);
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.Save();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ConfigWindowViewModel configViewModel)
        {
            RootGrid.DataContext = configViewModel;
            InitializeTabs(configViewModel);
            return;
        }

        if (DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }

        if (!ReferenceEquals(_mainWindowViewModel, mainWindowViewModel))
        {
            if (_mainWindowViewModel is not null)
            {
                _mainWindowViewModel.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
            }

            _mainWindowViewModel = mainWindowViewModel;
            _mainWindowViewModel.PropertyChanged += OnMainWindowViewModelPropertyChanged;
        }

        if (Application.Current is not App app)
        {
            return;
        }

        var configService = app.Services.GetRequiredService<Services.DesktopConfigService>();
        var shellService = app.Services.GetRequiredService<Services.DesktopShellService>();
        var xingeRemoteControlService = app.Services.GetRequiredService<Services.XingeRemoteControlService>();
        var configVm = new ConfigWindowViewModel(mainWindowViewModel.RootDir, configService, shellService, xingeRemoteControlService);
        RootGrid.DataContext = configVm;
        InitializeTabs(configVm);
    }

    private void OnMainWindowViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(MainWindowViewModel.RootDir), StringComparison.Ordinal))
        {
            return;
        }

        if (_mainWindowViewModel is null)
        {
            return;
        }

        ViewModel?.SetRootDir(_mainWindowViewModel.RootDir);
    }

    private void InitializeTabs(ConfigWindowViewModel viewModel)
    {
        if (_tabsInitialized)
        {
            return;
        }

        _tabsInitialized = true;
        SettingsTabControl.ItemsSource = new object[]
        {
            new TabItem { Header = "基础设置", Content = new BasicSettingsTab { DataContext = viewModel } },
            new TabItem { Header = "登录设置", Content = new LoginSettingsTab { DataContext = viewModel } },
            new TabItem { Header = "Xinge 远程", Content = new XingeSettingsTabControl { DataContext = viewModel } },
            new TabItem { Header = "剧目信息配置", Content = new SeriesInfoSettingsTab { DataContext = viewModel } },
            new TabItem { Header = "成本报表", Content = new CostReportSettingsTab { DataContext = viewModel } },
            new TabItem { Header = "AI 文本", Content = new AiTextSettingsTab { DataContext = viewModel } },
            new TabItem { Header = "AI 图片", Content = new AiImageSettingsTab { DataContext = viewModel } },
            new TabItem { Header = "飞书通知", Content = new FeishuSettingsTab { DataContext = viewModel } },
        };
    }
}

internal sealed class XingeSettingsTabControl : UserControl
{
    public XingeSettingsTabControl()
    {
        Content = new ScrollViewer
        {
            Content = BuildContent()
        };
    }

    private static Control BuildContent()
    {
        var panel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16)
        };

        panel.Children.Add(Hint("Xinge 远程控制独立于普通数据源登录，用于同步任务队列快照和后续远程联动。"));
        panel.Children.Add(BindCheck("启用 Xinge 远程控制", nameof(ConfigWindowViewModel.XingeEnabled)));
        panel.Children.Add(Row("服务地址", BindText(nameof(ConfigWindowViewModel.XingeServerUrl))));
        panel.Children.Add(Row("用户名", BindText(nameof(ConfigWindowViewModel.XingeUsername))));
        panel.Children.Add(Row("密码", BindText(nameof(ConfigWindowViewModel.XingePassword), isPassword: true)));
        panel.Children.Add(Row("客户端 ID", BindText(nameof(ConfigWindowViewModel.XingeClientId))));
        panel.Children.Add(Row("客户端 Token", BindText(nameof(ConfigWindowViewModel.XingeClientToken), isPassword: true)));
        panel.Children.Add(Row("设备名称", BindText(nameof(ConfigWindowViewModel.XingeClientName))));
        panel.Children.Add(BindCheck("优先使用 WebSocket 长连接，失败后自动轮询", nameof(ConfigWindowViewModel.XingeWsEnabled)));
        panel.Children.Add(Row("轮询间隔（秒）", BindText(nameof(ConfigWindowViewModel.XingePollIntervalSeconds))));
        panel.Children.Add(BindCheck("上传登录二维码截图到 Xinge 服务", nameof(ConfigWindowViewModel.XingeUploadLoginQr)));
        panel.Children.Add(Row("用户角色", ReadOnlyText(nameof(ConfigWindowViewModel.XingeUserRole))));
        panel.Children.Add(BuildActionRow());
        panel.Children.Add(Row("最近结果", ReadOnlyText(nameof(ConfigWindowViewModel.XingeOperationStatus))));

        return panel;
    }

    private static Control BuildActionRow()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("220,*"),
            ColumnSpacing = 12
        };

        grid.Children.Add(new TextBlock
        {
            Text = "Xinge 操作",
            VerticalAlignment = VerticalAlignment.Center
        });

        var button = new Button
        {
            Content = "获取凭证并测试连接",
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        button[!Button.CommandProperty] = new Binding(nameof(ConfigWindowViewModel.RefreshXingeCredentialsCommand));
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    private static CheckBox BindCheck(string content, string propertyName)
    {
        var checkBox = new CheckBox { Content = content };
        checkBox[!ToggleButton.IsCheckedProperty] = new Binding(propertyName);
        return checkBox;
    }

    private static TextBox BindText(string propertyName, bool isPassword = false)
    {
        var textBox = new TextBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        textBox[!TextBox.TextProperty] = new Binding(propertyName);
        if (isPassword)
        {
            textBox.PasswordChar = '*';
        }

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
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private static TextBlock Hint(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
    }
}
