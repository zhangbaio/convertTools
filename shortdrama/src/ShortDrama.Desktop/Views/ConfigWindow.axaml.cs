using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using ShortDrama.Desktop.ViewModels;
using ShortDrama.Desktop.Views.SettingsTabs;

namespace ShortDrama.Desktop.Views;

public partial class ConfigWindow : Window
{
    private bool _tabsInitialized;

    public ConfigWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => EnsureConfigViewModel();
    }

    public ConfigWindow(ConfigWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    public bool WasSaved => ViewModel?.WasSaved == true;

    private ConfigWindowViewModel? ViewModel => DataContext as ConfigWindowViewModel;

    private async void PickRootDir_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
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

    private void SaveAndClose_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.Save() == true)
        {
            Close(true);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void EnsureConfigViewModel()
    {
        if (DataContext is ConfigWindowViewModel configViewModel)
        {
            InitializeTabs(configViewModel);
            return;
        }

        if (DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }

        if (Application.Current is not App app)
        {
            return;
        }

        var configService = app.Services.GetRequiredService<Services.DesktopConfigService>();
        var shellService = app.Services.GetRequiredService<Services.DesktopShellService>();
        DataContext = new ConfigWindowViewModel(mainWindowViewModel.RootDir, configService, shellService);
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
            new TabItem { Header = "剧目信息配置", Content = new SeriesInfoSettingsTab { DataContext = viewModel } },
            new TabItem { Header = "成本报表", Content = new CostReportSettingsTab { DataContext = viewModel } },
            new TabItem { Header = "AI 文本", Content = new AiTextSettingsTab { DataContext = viewModel } },
            new TabItem { Header = "AI 图片", Content = new AiImageSettingsTab { DataContext = viewModel } },
        };
    }
}
