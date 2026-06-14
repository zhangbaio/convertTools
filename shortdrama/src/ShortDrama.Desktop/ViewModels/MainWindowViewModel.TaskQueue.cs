using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace ShortDrama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<ProjectListItemViewModel> FilteredProjects { get; } = [];

    [ObservableProperty]
    private string taskQueueFilterText = string.Empty;

    public string TaskQueueSummary =>
        $"项目数: {Projects.Count} | 当前筛选: {FilteredProjects.Count} | 已勾选: {Projects.Count(item => item.IsChecked)} | 启用步骤: {GetQueueSelectedSteps().Length}";

    partial void OnTaskQueueFilterTextChanged(string value)
    {
        ApplyTaskQueueFilter();
        RefreshCommandStates();
    }

    private void ApplyTaskQueueFilter()
    {
        var selectedProjectKey = SelectedProject?.ProjectKey;
        var filter = (TaskQueueFilterText ?? string.Empty).Trim();
        var matches = string.IsNullOrWhiteSpace(filter)
            ? Projects
            : Projects.Where(project => MatchesTaskQueueFilter(project, filter));

        FilteredProjects.Clear();
        foreach (var project in matches)
        {
            FilteredProjects.Add(project);
        }

        OnPropertyChanged(nameof(TaskQueueSummary));

        if (!HasTaskQueueDetail)
        {
            SelectedProject = selectedProjectKey is null
                ? FilteredProjects.FirstOrDefault()
                : FilteredProjects.FirstOrDefault(item => string.Equals(item.ProjectKey, selectedProjectKey, StringComparison.Ordinal))
                    ?? FilteredProjects.FirstOrDefault();
        }
    }

    private static bool MatchesTaskQueueFilter(ProjectListItemViewModel project, string filter)
    {
        var token = filter.Trim();
        if (token.Length == 0)
        {
            return true;
        }

        return Contains(project.OriginalTitle, token)
               || Contains(project.NewTitle, token)
               || Contains(project.SchedulingStatus, token)
               || Contains(project.DownloadNodeStatus, token)
               || Contains(project.ProjectMaterialNodeStatus, token)
               || Contains(project.EpisodeUploadNodeStatus, token)
               || Contains(project.MaterialUploadNodeStatus, token)
               || Contains(project.SourceProjectDir, token)
               || Contains(project.WorkflowProjectDir, token)
               || Contains(project.DramaInfoSummary, token)
               || Contains(project.MaterialSummary, token);
    }

    private static bool Contains(string? source, string token)
    {
        return !string.IsNullOrWhiteSpace(source) &&
               source.Contains(token, StringComparison.OrdinalIgnoreCase);
    }
}
