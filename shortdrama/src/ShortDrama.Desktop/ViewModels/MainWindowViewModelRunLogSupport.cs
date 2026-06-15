using CommunityToolkit.Mvvm.ComponentModel;
using ShortDrama.Desktop.Models;
using System.ComponentModel;
using System.Text;

namespace ShortDrama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private bool _syncingRunLogProjectSelection;
    private bool _syncingProjectLogFilterSelection;

    [ObservableProperty]
    private ProjectListItemViewModel? selectedRunLogProject;

    [ObservableProperty]
    private bool followLatestActiveLogProject = true;

    public IReadOnlyList<ProjectListItemViewModel> RunLogProjects =>
        Projects.Where(item => item.IsChecked).ToArray();

    public string RunLogSummary =>
        $"项目数: {RunLogProjects.Count} | 运行中: {CountProjectsByStatus(RunLogProjects, "运行中")} | 排队中: {CountProjectsByStatus(RunLogProjects, "排队中")} | 待续跑: {CountProjectsByStatus(RunLogProjects, "待续跑", "已停止")} | 失败: {CountProjectsByStatus(RunLogProjects, "失败")} | 已完成: {CountProjectsByStatus(RunLogProjects, "已完成")}";

    public string RunLogCurrentScopeLabel =>
        string.Equals(SelectedProjectLogFilter?.Key ?? AllProjectsFilterKey, AllProjectsFilterKey, StringComparison.Ordinal)
            ? "已勾选项目"
            : SelectedProjectLogFilter?.Label ?? "已勾选项目";

    public bool IsAllProjectsRunLogScope =>
        string.Equals(SelectedProjectLogFilter?.Key ?? AllProjectsFilterKey, AllProjectsFilterKey, StringComparison.Ordinal);

    public string VisibleActivityLogText => BuildVisibleActivityLogText();

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
        _syncingProjectLogFilterSelection = true;
        try
        {
            SelectedRunLogProject = null;
            SelectedProjectLogFilter = ProjectLogFilters.FirstOrDefault(item => string.Equals(item.Key, AllProjectsFilterKey, StringComparison.Ordinal))
                ?? SelectedProjectLogFilter;
        }
        finally
        {
            _syncingRunLogProjectSelection = false;
            _syncingProjectLogFilterSelection = false;
        }

        ApplyActivityLogFilter();
        RefreshRunLogViewState();
    }

    public void SelectProjectActivityLog(ProjectListItemViewModel? project)
    {
        if (project is null)
        {
            ShowAllProjectsActivityLog();
            return;
        }

        _syncingProjectLogFilterSelection = true;
        try
        {
            SelectedProjectLogFilter = ProjectLogFilters.FirstOrDefault(item => string.Equals(item.Key, project.ProjectKey, StringComparison.Ordinal))
                ?? SelectedProjectLogFilter;
        }
        finally
        {
            _syncingProjectLogFilterSelection = false;
        }

        ApplyActivityLogFilter();
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
            builder.AppendLine(FormatRunLogEntry(item));
        }

        return builder.ToString().TrimEnd();
    }

    private void HandleRunLogActivityAppended(string projectKey)
    {
        if (FollowLatestActiveLogProject && !string.IsNullOrWhiteSpace(projectKey))
        {
            var project = Projects.FirstOrDefault(item => string.Equals(item.ProjectKey, projectKey, StringComparison.Ordinal));
            if (project is not null && project.IsChecked)
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

                _syncingProjectLogFilterSelection = true;
                try
                {
                    SelectedProjectLogFilter = ProjectLogFilters.FirstOrDefault(item => string.Equals(item.Key, project.ProjectKey, StringComparison.Ordinal))
                        ?? SelectedProjectLogFilter;
                }
                finally
                {
                    _syncingProjectLogFilterSelection = false;
                }

                ApplyActivityLogFilter();
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
                SelectedRunLogProject = RunLogProjects.FirstOrDefault(item =>
                    string.Equals(item.ProjectKey, SelectedProjectLogFilter?.Key, StringComparison.Ordinal));
            }
        }
        finally
        {
            _syncingRunLogProjectSelection = false;
        }
    }

    private void RefreshRunLogViewState()
    {
        OnPropertyChanged(nameof(RunLogSummary));
        OnPropertyChanged(nameof(RunLogCurrentScopeLabel));
        OnPropertyChanged(nameof(IsAllProjectsRunLogScope));
        OnPropertyChanged(nameof(VisibleActivityLogText));
    }

    private static int CountProjectsByStatus(IEnumerable<ProjectListItemViewModel> projects, params string[] statuses)
    {
        return projects.Count(project => statuses.Any(status => string.Equals(project.SchedulingStatus, status, StringComparison.Ordinal)));
    }

    private void OnProjectRowStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectListItemViewModel.SchedulingStatus) or
            nameof(ProjectListItemViewModel.IsChecked) or
            nameof(ProjectListItemViewModel.CurrentStepLabel) or
            nameof(ProjectListItemViewModel.CurrentStepProgressText))
        {
            if (e.PropertyName == nameof(ProjectListItemViewModel.IsChecked))
            {
                OnPropertyChanged(nameof(RunLogProjects));
                SyncRunLogSelectionToCurrentFilter();
                ApplyActivityLogFilter();
            }

            RefreshRunLogViewState();
        }
    }

    private static string FormatRunLogEntry(ActivityLogEntry item)
    {
        var parts = new List<string>
        {
            $"[{item.TimestampText}]",
            item.IsFailure ? "ERROR" : "INFO"
        };

        if (!string.IsNullOrWhiteSpace(item.ProjectLabel))
        {
            parts.Add($"[{item.ProjectLabel}]");
        }

        if (!string.IsNullOrWhiteSpace(item.StepLabel))
        {
            parts.Add(item.StepLabel);
        }

        if (!string.IsNullOrWhiteSpace(item.StepKey))
        {
            parts.Add(item.StepKey);
        }

        parts.Add(item.Message);
        return string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}
