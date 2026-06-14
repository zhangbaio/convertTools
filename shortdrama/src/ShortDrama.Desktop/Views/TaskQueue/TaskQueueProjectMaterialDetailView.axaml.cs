using Avalonia.Controls;
using Avalonia.Interactivity;
using ShortDrama.Desktop.Models;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views.TaskQueue;

public partial class TaskQueueProjectMaterialDetailView : UserControl
{
    public TaskQueueProjectMaterialDetailView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private async void RunSelectedRewriteDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "rewrite", "改写信息");
    }

    private async void RunSelectedPosterDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "poster-rename", "生成海报图片");
    }

    private async void RunSelectedProjectImageDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "project-image", "生成工程图");
    }

    private async void RunSelectedCostReportDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "cost-report", "生成成本报表");
    }

    private async void RunSelectedBatchRenameDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "batch-file-rename", "重命名视频文件");
    }

    private async void RunSelectedMaterialConvertDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null)
        {
            return;
        }

        await ViewModel.RunProjectStepFromQueueAsync(ViewModel.SelectedProject, "material-convert", "转换素材视频");
    }

    private async void RenameSelectedProjectTitle_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedProject is null || OwnerWindow is null)
        {
            return;
        }

        var window = new RenameProjectTitleWindow
        {
            OriginalTitle = ViewModel.SelectedProject.OriginalTitle,
            CurrentTitle = ViewModel.SelectedProject.DisplayName,
            NewTitle = ViewModel.SelectedProject.DisplayName
        };

        var result = await window.ShowDialog<string?>(OwnerWindow);
        if (!string.IsNullOrWhiteSpace(result))
        {
            await ViewModel.UpdateSelectedProjectTitleAsync(result);
        }
    }

    private async void FixMaterialValidationIssue_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.FixMaterialValidationIssueAsync((sender as Control)?.DataContext as MaterialValidationIssueItem);
    }

    private async void FixAllMaterialValidationIssues_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.FixAllMaterialValidationIssuesForSelectedProjectAsync();
    }
}
