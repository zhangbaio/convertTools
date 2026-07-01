using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using ShortDrama.Desktop.Services;
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
        CreateManualMaterialProjectButton.Click += CreateManualMaterialProjectButton_Click;
        DeleteChannelMaterialsButton.Click += DeleteChannelMaterialsButton_Click;
        OpenClipConfigButton.Click += OpenClipConfigButton_Click;
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
        ViewModel?.ShowMaterialUploadLogs(ViewModel.SelectedProject);
    }

    private async void CreateManualMaterialProjectButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || OwnerWindow is null || Application.Current is not App app)
        {
            return;
        }

        var service = app.Services.GetRequiredService<ManualMaterialProjectService>();
        var window = new ManualMaterialProjectWindow(ViewModel.RootDir, service);
        var accepted = await window.ShowDialog<bool>(OwnerWindow);
        if (!accepted || string.IsNullOrWhiteSpace(window.VideoDirectory))
        {
            return;
        }

        try
        {
            var result = service.CreateProject(new ManualMaterialProjectRequest(
                ViewModel.RootDir,
                window.VideoDirectory!,
                window.NewTitle,
                window.OriginalTitle,
                window.EpisodeCount));

            ViewModel.AppendExternalLog(result.Message, stepKey: "material-upload", stepLabel: "素材上传");
            ViewModel.StatusMessage = result.Message;
            await ViewModel.ScanCommand.ExecuteAsync(null);

            var target = ViewModel.MaterialUploadProjects.FirstOrDefault(project =>
                string.Equals(project.SourceProjectDir, result.SourceProjectDirectory, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(project.WorkflowProjectDir, result.WorkflowProjectDirectory, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                ViewModel.ActivateMaterialUploadProject(target);
            }
        }
        catch (Exception ex)
        {
            ViewModel.AppendExternalLog(
                $"手动创建素材项目失败：{ex.Message}",
                stepKey: "material-upload",
                stepLabel: "素材上传",
                isFailure: true);
            ViewModel.StatusMessage = ex.Message;
        }
    }

    private async void DeleteChannelMaterialsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null || OwnerWindow is null || Application.Current is not App app)
        {
            return;
        }

        var dialog = new MaterialChannelVideoDeleteWindow(ViewModel.SelectedProject.NewTitle);
        var accepted = await dialog.ShowDialog<bool>(OwnerWindow);
        if (!accepted)
        {
            return;
        }

        var service = app.Services.GetRequiredService<WeixinMaterialChannelVideoDeleteService>();
        try
        {
            ViewModel.StatusMessage = $"开始删除视频号素材：关键词“{dialog.Keyword}”，数量 {dialog.DeleteCount}";
            ViewModel.AppendExternalLog(ViewModel.StatusMessage, ViewModel.SelectedProject.ProjectKey, ViewModel.SelectedProject.DisplayName, "material-upload", "素材上传");

            var result = await service.DeleteAsync(
                ViewModel.SelectedProject.SourceProjectDir,
                ViewModel.SelectedProject.MaterialPublishConfigPath,
                dialog.Keyword,
                dialog.DeleteCount,
                new Progress<string>(message =>
                {
                    ViewModel.AppendExternalLog(message, ViewModel.SelectedProject.ProjectKey, ViewModel.SelectedProject.DisplayName, "material-upload", "素材上传");
                    ViewModel.StatusMessage = message;
                }),
                CancellationToken.None);

            var summary = $"视频号素材删除完成，共删除 {result.DeletedCount} 条。";
            ViewModel.AppendExternalLog(summary, ViewModel.SelectedProject.ProjectKey, ViewModel.SelectedProject.DisplayName, "material-upload", "素材上传");
            ViewModel.StatusMessage = summary;
        }
        catch (Exception ex)
        {
            ViewModel.AppendExternalLog(
                $"视频号素材删除失败：{ex.Message}",
                ViewModel.SelectedProject.ProjectKey,
                ViewModel.SelectedProject.DisplayName,
                "material-upload",
                "素材上传",
                isFailure: true);
            ViewModel.StatusMessage = ex.Message;
        }
    }

    private async void OpenClipConfigButton_Click(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow is null || Application.Current is not App app)
        {
            return;
        }

        var settingsService = app.Services.GetRequiredService<GlobalSettingsService>();
        var window = new MaterialClipConfigWindow(settingsService);
        await window.ShowDialog<bool>(OwnerWindow);
        ViewModel?.AppendExternalLog("已打开剪辑配置窗口。", stepKey: "material-upload", stepLabel: "素材上传");
    }
}
