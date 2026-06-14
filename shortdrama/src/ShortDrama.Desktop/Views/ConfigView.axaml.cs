using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
        var configVm = new ConfigWindowViewModel(mainWindowViewModel.RootDir, configService, shellService);
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
            new TabItem { Header = "剧目信息配置", Content = new SeriesInfoSettingsTab { DataContext = viewModel } },
            new TabItem { Header = "成本报表", Content = new CostReportSettingsTab { DataContext = viewModel } },
            new TabItem { Header = "AI 文本", Content = new AiTextSettingsTab { DataContext = viewModel } },
            new TabItem { Header = "AI 图片", Content = new AiImageSettingsTab { DataContext = viewModel } },
        };
    }
}
