using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using PlatformPublisher.Desktop.ViewModels;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.Views;

public partial class KuaishouQueueLogView : UserControl
{
    private readonly ObservableCollection<LogProjectRow> _projects = [];
    private MainWindowViewModel? _vm;
    private bool _syncingSelection;
    private string _selectedProject = "";
    private string _logText = "";

    public event EventHandler? ReturnRequested;

    public KuaishouQueueLogView()
    {
        InitializeComponent();
        ProjectList.ItemsSource = _projects;
        AttachedToVisualTree += (_, _) => AttachViewModel();
        DataContextChanged += (_, _) => AttachViewModel();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && IsVisible)
                Refresh();
        };
        AutoFollowBox.IsCheckedChanged += (_, _) => Refresh();
        ProblemsOnlyBox.IsCheckedChanged += (_, _) => Refresh();
        ProjectList.SelectionChanged += (_, _) =>
        {
            if (_syncingSelection) return;
            if (ProjectList.SelectedItem is not LogProjectRow row) return;
            if (AutoFollowBox.IsChecked == true)
                AutoFollowBox.IsChecked = false;
            _selectedProject = row.ProjectName;
            RefreshLog();
        };
    }

    private void AttachViewModel()
    {
        if (DataContext is not MainWindowViewModel vm || ReferenceEquals(vm, _vm))
            return;
        if (_vm is not null)
        {
            _vm.ActivityLogRecords.CollectionChanged -= OnLogsChanged;
            _vm.KuaishouLogProjectsChanged -= OnKuaishouLogProjectsChanged;
        }
        _vm = vm;
        _vm.ActivityLogRecords.CollectionChanged += OnLogsChanged;
        _vm.KuaishouLogProjectsChanged += OnKuaishouLogProjectsChanged;
        Refresh();
    }

    private void OnKuaishouLogProjectsChanged(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        Refresh();
    }

    private bool _refreshPosted;

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsVisible) return;
        if (AutoFollowBox.IsChecked == true &&
            e.NewItems?.OfType<ActivityLogRecord>().LastOrDefault() is { ProjectName: { Length: > 0 } project })
        {
            _selectedProject = project;
        }

        if (_refreshPosted) return;
        _refreshPosted = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _refreshPosted = false;
            if (IsVisible) Refresh();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public void Refresh()
    {
        if (_vm is null) return;
        TitleLabel.Text = _vm.KuaishouEditionHeading;
        ReturnButton.Content = "返回快手分账";
        WorkspaceLabel.Text = string.IsNullOrWhiteSpace(_vm.DraftProjectDirectory)
            ? "工作目录：未选择"
            : $"工作目录：{_vm.DraftProjectDirectory}";
        StopButton.IsEnabled = _vm.IsCurrentWorkStoppable;
        RebuildProjects();
        RefreshLog();
    }

    private void RebuildProjects()
    {
        if (_vm is null) return;
        var rows = new List<LogProjectRow> { new("全部项目", "", "none") };
        var listed = new HashSet<string>(StringComparer.Ordinal);
        void Add(string title, string name, string tone)
        {
            if (string.IsNullOrWhiteSpace(name) || !listed.Add(name))
                return;
            rows.Add(new LogProjectRow(title, name, tone));
        }

        if (_vm.KuaishouLogProjects.Count > 0)
        {
            foreach (var project in _vm.KuaishouLogProjects)
            {
                var job = FindJob(project.ProjectName, project.ProjectDirectory);
                Add(
                    ProjectListTitle(job?.NewTitle, project.ProjectName),
                    project.ProjectName,
                    RowTone(project.Tone, job is null ? null : ToneFromJob(job)));
            }
        }
        else
        {
            foreach (var job in _vm.VisibleJobs.Where(job => job.IsChecked))
                Add(ProjectListTitle(job.NewTitle, job.ProjectName), job.ProjectName, ToneFromJob(job));
        }

        ApplyProjectRows(rows);
    }

    private void ApplyProjectRows(IReadOnlyList<LogProjectRow> next)
    {
        var same = _projects.Count == next.Count;
        if (same)
        {
            for (var index = 0; index < next.Count; index++)
            {
                if (!string.Equals(_projects[index].ProjectName, next[index].ProjectName, StringComparison.Ordinal) ||
                    !string.Equals(_projects[index].Title, next[index].Title, StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }
        }

        if (!same)
        {
            _syncingSelection = true;
            _projects.Clear();
            foreach (var row in next)
                _projects.Add(row);
            _syncingSelection = false;
        }
        else
        {
            for (var index = 0; index < next.Count; index++)
                _projects[index].Tone = next[index].Tone;
        }

        SyncSelection();
    }

    private void SyncSelection()
    {
        var selected = _projects.FirstOrDefault(row => row.ProjectName == _selectedProject) ?? _projects.FirstOrDefault();
        if (selected is null)
            return;
        if (ReferenceEquals(ProjectList.SelectedItem, selected))
        {
            _selectedProject = selected.ProjectName;
            return;
        }

        _syncingSelection = true;
        ProjectList.SelectedItem = selected;
        _selectedProject = selected.ProjectName;
        _syncingSelection = false;
    }

    private PublishJobRowViewModel? FindJob(string projectName, string projectDirectory)
    {
        if (_vm is null)
            return null;
        return _vm.VisibleJobs.FirstOrDefault(job =>
            string.Equals(job.ProjectName, projectName, StringComparison.Ordinal) ||
            SameDirectory(job.ProjectDirectory, projectDirectory));
    }

    private static bool SameDirectory(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    public static string ProjectListTitle(string? newTitle, string projectName)
    {
        var title = newTitle?.Trim();
        return string.IsNullOrWhiteSpace(title) ? projectName : title;
    }

    public static string RowTone(string runTone, string? jobTone) =>
        runTone is "running" or "ok" or "failed" or "none"
            ? runTone
            : string.IsNullOrEmpty(jobTone) ? "waiting" : jobTone;

    private static string ToneFromJob(PublishJobRowViewModel job)
    {
        var statuses = new[]
        {
            job.StatusText,
            job.DownloadStepStatus,
            job.RewriteStepStatus,
            job.PosterStepStatus,
            job.ProjectImageStepStatus,
            job.AutoFillStepStatus,
            job.UploadStepStatus,
        };
        return ToneFromStatuses(job.StatusText, job.UploadStepStatus, statuses);
    }

    public static string ToneFromStatuses(string statusText, string uploadStatus, IReadOnlyList<string> statuses)
    {
        if (statuses.Any(IsRunningStatus))
            return "running";
        if (statuses.Any(IsFailedStatus))
            return "failed";
        if (IsSuccessStatus(statusText) || IsSuccessStatus(uploadStatus))
            return "ok";
        return "waiting";
    }

    private static bool IsFailedStatus(string status) =>
        status.Contains("失败", StringComparison.Ordinal) || status is "异常" or "错误";

    private static bool IsRunningStatus(string status) =>
        status.StartsWith("进行中", StringComparison.Ordinal) ||
        status is "执行中" or "上传中" or "发布中" or "等待槽位";

    private static bool IsSuccessStatus(string status) =>
        status.StartsWith("已完成", StringComparison.Ordinal) || status is "完成" or "成功";

    private IEnumerable<string> ReadLogLines()
    {
        if (_vm is null)
            return [];
        var projects = _vm.KuaishouLogProjects;
        if (!string.IsNullOrEmpty(_selectedProject))
        {
            var match = projects.FirstOrDefault(project => project.ProjectName == _selectedProject);
            if (match is not null && match.CopyLines().Count > 0)
                return match.CopyLines();
            return ActivityLines(_selectedProject);
        }

        if (projects.Count > 0 && projects.Any(project => project.CopyLines().Count > 0))
        {
            return projects.SelectMany(project => project.CopyLines());
        }

        return ActivityLines("");
    }

    private IEnumerable<string> ActivityLines(string projectName)
    {
        if (_vm is null)
            return [];
        var platform = _vm.SelectedPlatform.Value;
        return _vm.ActivityLogRecords
            .Where(record => record.Platform == platform)
            .Where(record => string.IsNullOrEmpty(projectName) ||
                             string.Equals(record.ProjectName, projectName, StringComparison.Ordinal) ||
                             record.Text.Contains(projectName, StringComparison.Ordinal))
            .Select(record => record.Text);
    }

    private void RefreshLog()
    {
        if (_vm is null) return;
        var problemsOnly = ProblemsOnlyBox.IsChecked == true;
        var lines = ReadLogLines()
            .Where(line => !problemsOnly || IsProblem(line))
            .ToList();
        var running = _projects.Count(row => row.Tone == "running");
        var succeeded = _projects.Count(row => row.Tone == "ok");
        var failed = _projects.Count(row => row.Tone == "failed");
        var checkedCount = Math.Max(0, _projects.Count - 1);
        var counts = $"勾选 {checkedCount} | 运行中 {running} | 成功 {succeeded} | 失败 {failed}";
        var selectedTitle = _projects.FirstOrDefault(row => row.ProjectName == _selectedProject)?.Title;
        SummaryLabel.Text = string.IsNullOrEmpty(_selectedProject)
            ? $"{counts} · 日志 {lines.Count} 条"
            : $"{counts} · {ProjectListTitle(selectedTitle, _selectedProject)} · 日志 {lines.Count} 条";
        var atEnd = LogScrollViewer.Offset.Y >=
                    Math.Max(0, LogScrollViewer.Extent.Height - LogScrollViewer.Viewport.Height - 8);
        _logText = string.Join(Environment.NewLine, lines);
        RenderLogLines(lines);
        if (atEnd || AutoFollowBox.IsChecked == true)
            LogScrollViewer.ScrollToEnd();
    }

    private void RenderLogLines(IReadOnlyList<string> lines)
    {
        LogTextBlock.Inlines?.Clear();
        for (var index = 0; index < lines.Count; index++)
        {
            if (index > 0)
                LogTextBlock.Inlines?.Add(new LineBreak());
            var line = lines[index];
            LogTextBlock.Inlines?.Add(new Run(line) { Foreground = BrushForLogLine(line) });
        }
    }

    private static IBrush BrushForLogLine(string line) => LogMessageLevelClassifier.InferLevel(line) switch
    {
        "error" => FailedForeground,
        "warn" => WaitingForeground,
        "success" => SuccessForeground,
        _ => DefaultForeground,
    };

    private static bool IsProblem(string text) =>
        text.Contains("失败", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("错误", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("WARN", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

    private void OnStopClick(object? sender, RoutedEventArgs e) => _vm?.StopCommand.Execute(null);

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not IClipboard clipboard) return;
        await clipboard.SetTextAsync(_logText);
        if (_vm is not null) _vm.StatusMessage = "已复制日志";
    }

    private async void OnCopySelectionClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not IClipboard clipboard) return;
        var selected = LogTextBlock.SelectedText;
        await clipboard.SetTextAsync(string.IsNullOrEmpty(selected) ? _logText : selected);
        if (_vm is not null) _vm.StatusMessage = "已复制选中日志";
    }

    private void OnSelectAllLogClick(object? sender, RoutedEventArgs e)
    {
        LogTextBlock.Focus();
        LogTextBlock.SelectAll();
    }

    private void OnReturnClick(object? sender, RoutedEventArgs e) => ReturnRequested?.Invoke(this, EventArgs.Empty);

    private static readonly IBrush SuccessForeground = new SolidColorBrush(Color.Parse("#047857"));
    private static readonly IBrush SuccessBackground = new SolidColorBrush(Color.Parse("#DDFBEA"));
    private static readonly IBrush FailedForeground = new SolidColorBrush(Color.Parse("#B42318"));
    private static readonly IBrush FailedBackground = new SolidColorBrush(Color.Parse("#FFE3E3"));
    private static readonly IBrush RunningForeground = new SolidColorBrush(Color.Parse("#075BC7"));
    private static readonly IBrush RunningBackground = new SolidColorBrush(Color.Parse("#DDEBFF"));
    private static readonly IBrush WaitingForeground = new SolidColorBrush(Color.Parse("#B45309"));
    private static readonly IBrush WaitingBackground = new SolidColorBrush(Color.Parse("#FFF2CC"));
    private static readonly IBrush DefaultForeground = new SolidColorBrush(Color.Parse("#334155"));

    private sealed class LogProjectRow : INotifyPropertyChanged
    {
        private string _tone;

        public LogProjectRow(string title, string projectName, string tone)
        {
            Title = title;
            ProjectName = projectName;
            _tone = tone;
        }

        public string Title { get; }
        public string ProjectName { get; }

        public string Tone
        {
            get => _tone;
            set
            {
                if (_tone == value) return;
                _tone = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tone)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Foreground)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Background)));
            }
        }

        public IBrush Foreground => _tone switch
        {
            "ok" => SuccessForeground,
            "failed" => FailedForeground,
            "running" => RunningForeground,
            "waiting" => WaitingForeground,
            _ => Brushes.Black,
        };

        public IBrush Background => _tone switch
        {
            "ok" => SuccessBackground,
            "failed" => FailedBackground,
            "running" => RunningBackground,
            "waiting" => WaitingBackground,
            _ => Brushes.Transparent,
        };

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
