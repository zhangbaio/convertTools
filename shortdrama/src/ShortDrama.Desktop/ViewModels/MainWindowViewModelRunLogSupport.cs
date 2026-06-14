using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using System.Text;

namespace ShortDrama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private bool _syncingRunLogProjectSelection;

    [ObservableProperty]
    private ProjectListItemViewModel? selectedRunLogProject;

    [ObservableProperty]
    private bool followLatestActiveLogProject = true;

    public string RunLogSummary =>
        $"项目数: {Projects.Count} | 运行中: {CountProjectsByStatus("运行中")} | 排队中: {CountProjectsByStatus("排队中")} | 待继续: {CountProjectsByStatus("待继续", "已停止")} | 失败: {CountProjectsByStatus("失败")} | 已完成: {CountProjectsByStatus("已完成")}";

    public string RunLogCurrentScopeLabel => SelectedProjectLogFilter?.Label ?? "全部项目";

    public bool IsAllProjectsRunLogScope =>
        string.Equals(SelectedProjectLogFilter?.Key ?? AllProjectsFilterKey, AllProjectsFilterKey, StringComparison.Ordinal);

    partial void OnSelectedRunLogProjectChanged(ProjectListItemViewModel? value)
    {
        if (_syncingRunLogProjectSelection)
        {
            return;
        }

        if (value is null)
        {
            if (!IsAllProjectsRunLogScope)
            {
                ShowAllProjectsActivityLog();
            }

            return;
        }

        SelectProjectActivityLog(value);
    }

    public void ShowAllProjectsActivityLog()
    {
        _syncingRunLogProjectSelection = true;
        try
        {
            SelectedRunLogProject = null;
            SelectedProjectLogFilter = ProjectLogFilters.FirstOrDefault(item => string.Equals(item.Key, AllProjectsFilterKey, StringComparison.Ordinal))
                ?? SelectedProjectLogFilter;
        }
        finally
        {
            _syncingRunLogProjectSelection = false;
        }

        RefreshRunLogViewState();
    }

    public void SelectProjectActivityLog(ProjectListItemViewModel? project)
    {
        if (project is null)
        {
            ShowAllProjectsActivityLog();
            return;
        }

        SelectedProjectLogFilter = ProjectLogFilters.FirstOrDefault(item => string.Equals(item.Key, project.ProjectKey, StringComparison.Ordinal))
            ?? SelectedProjectLogFilter;
        RefreshRunLogViewState();
    }

    public string BuildVisibleActivityLogText()
    {
        if (ActivityLog.Count == 0)
        {
            return "当前筛选条件下暂无日志。";
        }

        var builder = new StringBuilder();
        foreach (var item in ActivityLog.Reverse())
        {
            builder.AppendLine(item.DisplayText);
        }

        return builder.ToString().TrimEnd();
    }

    private void HandleRunLogActivityAppended(string projectKey)
    {
        if (FollowLatestActiveLogProject && !string.IsNullOrWhiteSpace(projectKey))
        {
            var project = Projects.FirstOrDefault(item => string.Equals(item.ProjectKey, projectKey, StringComparison.Ordinal));
            if (project is not null)
            {
                _syncingRunLogProjectSelection = true;
                try
                {
                    SelectedRunLogProject = project;
                }
                finally
                {
                    _syncingRunLogProjectSelection = false;
                }

                SelectedProjectLogFilter = ProjectLogFilters.FirstOrDefault(item => string.Equals(item.Key, project.ProjectKey, StringComparison.Ordinal))
                    ?? SelectedProjectLogFilter;
            }
        }

        RefreshRunLogViewState();
    }

    private void SyncRunLogSelectionToCurrentFilter()
    {
        _syncingRunLogProjectSelection = true;
        try
        {
            if (string.Equals(SelectedProjectLogFilter?.Key ?? AllProjectsFilterKey, AllProjectsFilterKey, StringComparison.Ordinal))
            {
                SelectedRunLogProject = null;
            }
            else
            {
                SelectedRunLogProject = Projects.FirstOrDefault(item =>
                    string.Equals(item.ProjectKey, SelectedProjectLogFilter?.Key, StringComparison.Ordinal));
            }
        }
        finally
        {
            _syncingRunLogProjectSelection = false;
        }

        RefreshRunLogViewState();
    }

    private void RefreshRunLogViewState()
    {
        OnPropertyChanged(nameof(RunLogSummary));
        OnPropertyChanged(nameof(RunLogCurrentScopeLabel));
        OnPropertyChanged(nameof(IsAllProjectsRunLogScope));
    }

    private int CountProjectsByStatus(params string[] statuses)
    {
        return Projects.Count(project => statuses.Any(status => string.Equals(project.SchedulingStatus, status, StringComparison.Ordinal)));
    }

    private void OnProjectRowStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectListItemViewModel.SchedulingStatus) or
            nameof(ProjectListItemViewModel.CurrentStepLabel) or
            nameof(ProjectListItemViewModel.CurrentStepProgressText))
        {
            RefreshRunLogViewState();
        }
    }
}
