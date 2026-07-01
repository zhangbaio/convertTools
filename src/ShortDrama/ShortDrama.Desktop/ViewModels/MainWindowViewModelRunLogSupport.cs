using CommunityToolkit.Mvvm.ComponentModel;
using ShortDrama.Desktop.Models;
using System.ComponentModel;
using System.Text;

namespace ShortDrama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private bool _syncingRunLogProjectSelection;
    private bool _syncingProjectLogFilterSelection;

    private static readonly HashSet<string> MaterialRunLogStepKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "weixin-material-upload",
        "material-upload",
        "material-validate",
        "material-convert",
        "material-auto-repair"
    };

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

    public bool IsMaterialRunLogTab =>
        string.Equals(SelectedRunLogTabOption?.Key ?? RunLogTabVideoChannel, RunLogTabMaterialLog, StringComparison.Ordinal);

    public bool IsRunLogProjectPaneVisible => !IsMaterialRunLogTab;

    public bool IsRunLogFollowControlsVisible => !IsMaterialRunLogTab;

    public bool IsRunLogStopButtonVisible => !IsMaterialRunLogTab;

    public bool IsRunLogStepFilterVisible => !IsMaterialRunLogTab;

    public string RunLogHeaderTitle =>
        IsMaterialRunLogTab ? "素材上传日志" : "运行日志";

    public string RunLogHeaderContextText =>
        IsMaterialRunLogTab
            ? "集中展示素材上传、素材生成、素材转码、素材校验等流程日志。"
            : $"工作目录: {RootDir}";

    public string RunLogHeaderSummaryText =>
        IsMaterialRunLogTab
            ? $"当前范围: {RunLogCurrentScopeLabel}"
            : RunLogSummary;

    public string RunLogFooterHintText =>
        IsMaterialRunLogTab
            ? "从素材上传页点击“查看素材日志”可直接跳转到这里。"
            : "这里集中查看任务队列日志，不会打断任务队列表格中的项目浏览。";

    public bool IsAllProjectsRunLogScope =>
        string.Equals(SelectedProjectLogFilter?.Key ?? AllProjectsFilterKey, AllProjectsFilterKey, StringComparison.Ordinal);

    public string VisibleActivityLogText => BuildVisibleActivityLogText();

    partial void OnSelectedRunLogProjectChanged(ProjectListItemViewModel? value)
    {
        if (_syncingRunLogProjectSelection || IsMaterialRunLogTab)
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

    public void ShowMaterialRunLogTab(ProjectListItemViewModel? project)
    {
        SelectedSidebarTabIndex = SidebarTabRunLogIndex;
        SelectedRunLogTabOption = RunLogTabOptions.FirstOrDefault(item => string.Equals(item.Key, RunLogTabMaterialLog, StringComparison.Ordinal))
            ?? SelectedRunLogTabOption;
        SelectedStepLogFilter = StepLogFilters.FirstOrDefault(item => string.Equals(item.Key, AllStepsFilterKey, StringComparison.Ordinal))
            ?? SelectedStepLogFilter;

        if (project is not null)
        {
            SelectedProject = project;
            SelectProjectActivityLog(project);
            ActivityTitle = $"素材上传日志 · {project.DisplayName}";
        }
        else
        {
            ShowAllProjectsActivityLog();
            ActivityTitle = "素材上传日志";
        }

        RefreshRunLogViewState();
    }

    public string BuildVisibleActivityLogText()
    {
        if (ActivityLog.Count == 0)
        {
            return IsMaterialRunLogTab ? "暂无运行日志" : "当前筛选条件下暂无日志。";
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
        if (!IsMaterialRunLogTab && FollowLatestActiveLogProject && !string.IsNullOrWhiteSpace(projectKey))
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
        OnPropertyChanged(nameof(RunLogProjects));
        OnPropertyChanged(nameof(RunLogSummary));
        OnPropertyChanged(nameof(RunLogCurrentScopeLabel));
        OnPropertyChanged(nameof(IsMaterialRunLogTab));
        OnPropertyChanged(nameof(IsRunLogProjectPaneVisible));
        OnPropertyChanged(nameof(IsRunLogFollowControlsVisible));
        OnPropertyChanged(nameof(IsRunLogStopButtonVisible));
        OnPropertyChanged(nameof(IsRunLogStepFilterVisible));
        OnPropertyChanged(nameof(RunLogHeaderTitle));
        OnPropertyChanged(nameof(RunLogHeaderContextText));
        OnPropertyChanged(nameof(RunLogHeaderSummaryText));
        OnPropertyChanged(nameof(RunLogFooterHintText));
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

    private bool MatchesRunLogTab(ActivityLogEntry item)
    {
        var tabKey = SelectedRunLogTabOption?.Key ?? RunLogTabVideoChannel;
        return tabKey switch
        {
            RunLogTabMaterialLog => IsMaterialRunLogEntry(item),
            RunLogTabMiniprogram => IsMiniprogramRunLogEntry(item),
            RunLogTabKuaishou => IsKuaishouRunLogEntry(item),
            _ => !IsMaterialRunLogEntry(item) && !IsMiniprogramRunLogEntry(item) && !IsKuaishouRunLogEntry(item)
        };
    }

    private static bool IsMaterialRunLogEntry(ActivityLogEntry item)
    {
        if (MaterialRunLogStepKeys.Contains(item.StepKey))
        {
            return true;
        }

        var combined = $"{item.StepLabel} {item.Message}";
        return combined.Contains("素材", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("material_clips", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMiniprogramRunLogEntry(ActivityLogEntry item)
    {
        var combined = $"{item.StepKey} {item.StepLabel} {item.Message}";
        return combined.Contains("miniprogram", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("minidrama", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("小程序", StringComparison.Ordinal);
    }

    private static bool IsKuaishouRunLogEntry(ActivityLogEntry item)
    {
        var combined = $"{item.StepKey} {item.StepLabel} {item.Message}";
        return combined.Contains("kuaishou", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("快手", StringComparison.Ordinal);
    }
}
