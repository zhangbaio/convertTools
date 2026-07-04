using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Services;

public sealed class LogEntry
{
    public string Text { get; init; } = "";
    public string Level { get; init; } = "info";
    public string ProjectName { get; init; } = "";
    public string ProjectPath { get; init; } = "";
}

public sealed class LogProjectItem
{
    public string Title { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public string StatusTone { get; init; } = "none";
}

public sealed class LogService
{
    private const int MaxEntries = 5000;
    private const int MaxRendered = 1200;
    private static readonly Regex HeaderRegex = new(
        @"^\[(?<time>[^\]]+)\]\s*(?<level>\w+)\s*(?:\[(?<project>[^\]]+)\])?\s*(?<rest>.*)$",
        RegexOptions.Compiled);

    private readonly List<LogEntry> _entries = new();
    private Dictionary<string, string> _nameIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _changedScheduled;
    private string _selectedProjectPath = "";
    private bool _problemsOnly;

    public ObservableCollection<LogProjectItem> Projects { get; } = new();
    public ObservableCollection<LogEntry> RenderedEntries { get; } = new();

    public string WorkspaceLabel { get; private set; } = "TikTok队列工作目录：未选择";
    public string SummaryText { get; private set; } =
        "项目数：0 | 运行中：0 | 下载中：0 | 上传已完成：0 | 待后续：0 | 失败：0 | 已完成：0";

    public bool IsRunning { get; private set; }
    public string SelectedProjectPath
    {
        get => _selectedProjectPath;
        set
        {
            var normalized = value ?? "";
            if (string.Equals(_selectedProjectPath, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedProjectPath = normalized;
            RefreshRendered();
            ScheduleChanged();
        }
    }

    public bool AutoFollowActiveProject { get; set; } = true;
    public bool ProblemsOnly
    {
        get => _problemsOnly;
        set
        {
            if (_problemsOnly == value)
                return;

            _problemsOnly = value;
            RefreshRendered();
            ScheduleChanged();
        }
    }

    public event Action? Changed;

    public void Append(string text)
    {
        var line = (text ?? "").TrimEnd();
        if (string.IsNullOrWhiteSpace(line)) return;

        var (level, project) = ParseHeader(line);
        var entry = new LogEntry
        {
            Text = line,
            Level = level,
            ProjectName = project,
            ProjectPath = ResolveProjectPath(project),
        };
        _entries.Add(entry);
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(0, _entries.Count - MaxEntries);

        if (EntryMatchesFilter(entry))
            AppendRendered(entry);

        ScheduleChanged();
    }

    public void UpdateSnapshot(IEnumerable<QueueProjectRowViewModel> rows, string? workspacePath, bool queueRunning)
    {
        IsRunning = queueRunning;
        WorkspaceLabel = string.IsNullOrWhiteSpace(workspacePath)
            ? "TikTok队列工作目录：未选择"
            : $"TikTok队列工作目录：{workspacePath}";

        var list = rows.ToList();
        var running = list.Count(r => r.StatusText is QueueStepStatus.Running);
        var uploadDone = list.Count(r => r.UploadStatus == QueueStepStatus.Completed);
        var failed = list.Count(r => r.StatusText == QueueStepStatus.Failed || r.LastError.Length > 0);
        var completed = list.Count(r => r.StatusText == QueueStepStatus.Completed);
        var pending = list.Count(r => r.IsPendingUpload);
        SummaryText =
            $"项目数：{list.Count} | 运行中：{running} | 下载中：0 | 上传已完成：{uploadDone} | 待后续：{pending} | 失败：{failed} | 已完成：{completed}";

        _nameIndex = list
            .Where(r => !string.IsNullOrWhiteSpace(r.Title))
            .GroupBy(r => r.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Item.ProjectDir, StringComparer.OrdinalIgnoreCase);

        Projects.Clear();
        Projects.Add(new LogProjectItem { Title = "全部项目", ProjectPath = "", StatusTone = "none" });
        foreach (var row in list.Where(r => r.IsEnabled && !string.IsNullOrWhiteSpace(r.Title)))
        {
            Projects.Add(new LogProjectItem
            {
                Title = row.Title,
                ProjectPath = row.Item.ProjectDir,
                StatusTone = ToneForRow(row),
            });
        }

        if (AutoFollowActiveProject)
        {
            var active = list.FirstOrDefault(r => r.StatusText == QueueStepStatus.Running)
                         ?? list.FirstOrDefault(r => r.IsPendingUpload);
            if (active is not null)
                SelectedProjectPath = active.Item.ProjectDir;
        }

        RefreshRendered();
        ScheduleChanged();
    }

    public string BuildCopyText()
    {
        var filtered = FilterEntries().TakeLast(MaxRendered);
        return string.Join(Environment.NewLine, filtered.Select(e => e.Text));
    }

    private void AppendRendered(LogEntry entry)
    {
        while (RenderedEntries.Count >= MaxRendered)
            RenderedEntries.RemoveAt(0);
        RenderedEntries.Add(entry);
    }

    private void RefreshRendered()
    {
        RenderedEntries.Clear();
        foreach (var entry in FilterEntries().TakeLast(MaxRendered))
            RenderedEntries.Add(entry);
    }

    private bool EntryMatchesFilter(LogEntry entry)
    {
        if (ProblemsOnly)
        {
            if (!IsProblemLevel(entry.Level)
                && !entry.Text.Contains("失败", StringComparison.Ordinal)
                && !entry.Text.Contains("错误", StringComparison.Ordinal)
                && !entry.Text.Contains("异常", StringComparison.Ordinal)
                && !entry.Text.Contains("超时", StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(SelectedProjectPath))
            return true;

        return string.Equals(entry.ProjectPath, SelectedProjectPath, StringComparison.OrdinalIgnoreCase)
               || (string.IsNullOrWhiteSpace(entry.ProjectPath)
                   && !string.IsNullOrWhiteSpace(entry.ProjectName)
                   && _nameIndex.TryGetValue(entry.ProjectName, out var path)
                   && string.Equals(path, SelectedProjectPath, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<LogEntry> FilterEntries()
    {
        IEnumerable<LogEntry> query = _entries;
        if (!string.IsNullOrWhiteSpace(SelectedProjectPath))
        {
            query = query.Where(e =>
                string.Equals(e.ProjectPath, SelectedProjectPath, StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrWhiteSpace(e.ProjectPath)
                    && !string.IsNullOrWhiteSpace(e.ProjectName)
                    && _nameIndex.TryGetValue(e.ProjectName, out var path)
                    && string.Equals(path, SelectedProjectPath, StringComparison.OrdinalIgnoreCase)));
        }

        if (ProblemsOnly)
        {
            query = query.Where(e =>
                IsProblemLevel(e.Level)
                || e.Text.Contains("失败", StringComparison.Ordinal)
                || e.Text.Contains("错误", StringComparison.Ordinal)
                || e.Text.Contains("异常", StringComparison.Ordinal)
                || e.Text.Contains("超时", StringComparison.Ordinal));
        }

        return query;
    }

    private void ScheduleChanged()
    {
        if (_changedScheduled) return;
        _changedScheduled = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _changedScheduled = false;
            Changed?.Invoke();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private string ResolveProjectPath(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return "";
        return _nameIndex.TryGetValue(projectName.Trim(), out var path) ? path : "";
    }

    private static (string Level, string Project) ParseHeader(string line)
    {
        var match = HeaderRegex.Match(line);
        if (!match.Success) return ("info", "");
        return (match.Groups["level"].Value.Trim().ToLowerInvariant(), match.Groups["project"].Value.Trim());
    }

    private static bool IsProblemLevel(string level) =>
        level is "error" or "failed" or "fail" or "warn" or "warning" or "e" or "w";

    private static string ToneForRow(QueueProjectRowViewModel row) => row.StatusText switch
    {
        QueueStepStatus.Completed => "ok",
        QueueStepStatus.Running => "running",
        QueueStepStatus.Failed => "failed",
        QueueStepStatus.WaitingUploadSlot => "waiting",
        _ when row.IsPendingUpload => "waiting",
        _ => "none",
    };
}
