using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views;

public partial class MaterialUploadView : UserControl
{
    public MaterialUploadView()
    {
        InitializeComponent();

        RunMaterialUploadQueueButton.Click += RunMaterialUploadQueueButton_Click;
        CheckAllVisibleButton.Click += (_, _) => ViewModel?.SetAllMaterialUploadProjectsChecked(true);
        UncheckAllVisibleButton.Click += (_, _) => ViewModel?.SetAllMaterialUploadProjectsChecked(false);
        OpenPublishConfigButton.Click += (_, _) => ViewModel?.OpenMaterialPublishConfig(null);
        ShowMaterialLogsButton.Click += ShowMaterialLogsButton_Click;
        MaterialUploadProjectsListBox.SelectionChanged += MaterialUploadProjectsListBox_SelectionChanged;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private async void PickRootDir_Click(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow?.StorageProvider is null)
        {
            return;
        }

        var folders = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
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

    private async void RunMaterialUploadQueueButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RunCheckedMaterialUploadQueueFromPageAsync();
    }

    private async void RunSingleMaterialUpload_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var project = (sender as Control)?.DataContext as ProjectListItemViewModel;
        await ViewModel.RunMaterialUploadProjectFromPageAsync(project);
    }

    private void MaterialUploadProjectsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MaterialUploadProjectsListBox.SelectedItem is ProjectListItemViewModel project)
        {
            ViewModel?.ActivateMaterialUploadProject(project);
        }
    }

    private void ShowMaterialLogsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null || OwnerWindow is null)
        {
            return;
        }

        ViewModel.ShowMaterialUploadLogs(ViewModel.SelectedProject);
        SelectSidebarTab("运行日志");
    }

    private void SelectSidebarTab(string headerText)
    {
        if (OwnerWindow?.FindControl<TabControl>("SidebarTabs") is not TabControl tabs ||
            tabs.Items is not IEnumerable<object> items)
        {
            return;
        }

        foreach (var item in items.OfType<TabItem>())
        {
            if (string.Equals(item.Header?.ToString(), headerText, StringComparison.Ordinal))
            {
                tabs.SelectedItem = item;
                return;
            }
        }
    }
}
