using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Services;

public sealed class LogEntry
{
    public string Text { get; init; } = "";
    public string Level { get; init; } = "info";
    public string ProjectName { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public IBrush Foreground => LogService.BrushForLevel(Level);
}

public sealed class LogProjectItem
{
    private static readonly IBrush SuccessForeground = new SolidColorBrush(Color.Parse("#047857"));
    private static readonly IBrush SuccessBackground = new SolidColorBrush(Color.Parse("#E8F7ED"));
    private static readonly IBrush FailedForeground = new SolidColorBrush(Color.Parse("#B42318"));
    private static readonly IBrush FailedBackground = new SolidColorBrush(Color.Parse("#FFE3E3"));
    private static readonly IBrush RunningForeground = new SolidColorBrush(Color.Parse("#075BC7"));
    private static readonly IBrush RunningBackground = new SolidColorBrush(Color.Parse("#DDEBFF"));
    private static readonly IBrush WaitingForeground = new SolidColorBrush(Color.Parse("#8A4B00"));
    private static readonly IBrush WaitingBackground = new SolidColorBrush(Color.Parse("#FFF2CC"));

    public string Title { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public string StatusTone { get; init; } = "none";
    public IBrush Foreground => StatusTone switch
    {
        "ok" => SuccessForeground,
        "failed" => FailedForeground,
        "running" => RunningForeground,
        "waiting" => WaitingForeground,
        _ => Brushes.Black,
    };
    public IBrush Background => StatusTone switch
    {
        "ok" => SuccessBackground,
        "failed" => FailedBackground,
        "running" => RunningBackground,
        "waiting" => WaitingBackground,
        _ => Brushes.Transparent,
    };
}

public sealed class LogService
{
    private const int MaxEntries = 5000;
    private const int MaxRendered = 1200;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(TikTokExecutionHistoryService.DefaultRetentionDays);
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

        var now = DateTime.Now;
        var (level, project, normalizedLine) = ParseHeader(line);
        var entry = new LogEntry
        {
            Text = normalizedLine,
            Level = level,
            ProjectName = project,
            ProjectPath = ResolveProjectPath(project),
            CreatedAt = now,
        };
        _entries.Add(entry);
        var shouldRefreshRendered = PruneExpiredEntries(now);
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
            shouldRefreshRendered = true;
        }

        if (shouldRefreshRendered)
            RefreshRendered();
        else if (EntryMatchesFilter(entry))
            AppendRendered(entry);

        ScheduleChanged();
    }

    /// <summary>
    /// 开始新一轮执行前清除本轮项目的旧日志；准备阶段已经实时产生的日志可按时间保留。
    /// </summary>
    public int ClearProjectEntries(
        IEnumerable<QueueProjectItem> items,
        DateTime? preserveEntriesSince = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var projectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var path = NormalizeProjectPath(item.ProjectDir);
            if (!string.IsNullOrWhiteSpace(path))
                projectPaths.Add(path);

            AddProjectName(projectNames, item.Title);
            AddProjectName(projectNames, item.DisplayName);
            AddProjectName(projectNames, item.NewTitle);
            AddProjectName(projectNames, item.OriginalTitle);
        }

        if (projectPaths.Count == 0 && projectNames.Count == 0)
            return 0;

        var removed = _entries.RemoveAll(entry =>
        {
            if (preserveEntriesSince.HasValue &&
                entry.CreatedAt >= preserveEntriesSince.Value)
            {
                return false;
            }

            var entryPath = NormalizeProjectPath(entry.ProjectPath);
            if (!string.IsNullOrWhiteSpace(entryPath))
                return projectPaths.Contains(entryPath);

            if (!string.IsNullOrWhiteSpace(entry.ProjectName) &&
                projectNames.Contains(entry.ProjectName.Trim()))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(entry.ProjectName) &&
                   _nameIndex.TryGetValue(entry.ProjectName.Trim(), out var indexedPath) &&
                   projectPaths.Contains(NormalizeProjectPath(indexedPath));
        });

        if (removed > 0)
        {
            RefreshRendered();
            ScheduleChanged();
        }

        return removed;
    }

    /// <summary>开始“全部账号队列”新一轮执行前，清空日志面板。</summary>
    public int ClearAllEntries()
    {
        var removed = _entries.Count;
        if (removed == 0)
            return 0;

        _entries.Clear();
        RenderedEntries.Clear();
        ScheduleChanged();
        return removed;
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
        var failed = list.Count(r => r.HasFailure);
        var completed = list.Count(r => r.StatusText == QueueStepStatus.Completed);
        var pending = list.Count(r => r.IsPendingUpload);
        SummaryText =
            $"项目数：{list.Count} | 运行中：{running} | 下载中：0 | 上传已完成：{uploadDone} | 待后续：{pending} | 失败：{failed} | 已完成：{completed}";

        _nameIndex = list
            .Where(r => !string.IsNullOrWhiteSpace(r.Title))
            .GroupBy(r => r.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Item.ProjectDir, StringComparer.OrdinalIgnoreCase);

        var targetProjects = new List<LogProjectItem>
        {
            new() { Title = "全部项目", ProjectPath = "", StatusTone = "none" },
        };
        foreach (var row in list.Where(r => r.IsEnabled && !string.IsNullOrWhiteSpace(r.Title)))
        {
            targetProjects.Add(new LogProjectItem
            {
                Title = row.Title,
                ProjectPath = row.Item.ProjectDir,
                StatusTone = ToneForRow(row),
            });
        }

        // 项目列表内容未变化时不重建集合（每次 Clear+Add 会让左侧 ListBox 整体重建）。
        if (!ProjectListEquals(targetProjects))
        {
            Projects.Clear();
            foreach (var project in targetProjects)
                Projects.Add(project);
        }

        if (AutoFollowActiveProject)
        {
            var active = list.FirstOrDefault(IsUploadRunning)
                         ?? list.FirstOrDefault(r => r.StatusText == QueueStepStatus.Running)
                         ?? list.FirstOrDefault(r => r.IsPendingUpload);
            if (active is not null)
                SelectedProjectPath = active.Item.ProjectDir;
        }

        // 渲染条目由 Append 增量维护；SelectedProjectPath/ProblemsOnly 变化时其 setter 已负责重建。
        ScheduleChanged();
    }

    private bool ProjectListEquals(IReadOnlyList<LogProjectItem> target)
    {
        if (target.Count != Projects.Count)
            return false;

        for (var i = 0; i < target.Count; i++)
        {
            var a = target[i];
            var b = Projects[i];
            if (!string.Equals(a.Title, b.Title, StringComparison.Ordinal) ||
                !string.Equals(a.ProjectPath, b.ProjectPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a.StatusTone, b.StatusTone, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    public string BuildCopyText()
    {
        var filtered = FilterEntries().TakeLast(MaxRendered);
        return string.Join(Environment.NewLine, filtered.Select(e => e.Text));
    }

    private bool PruneExpiredEntries(DateTime now)
    {
        var cutoff = now - Retention;
        return _entries.RemoveAll(entry => entry.CreatedAt < cutoff) > 0;
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
                && !ContainsProblemKeyword(entry.Text))
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
                || ContainsProblemKeyword(e.Text));
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

    private static void AddProjectName(ISet<string> names, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            names.Add(value.Trim());
    }

    private static string NormalizeProjectPath(string? path)
    {
        var value = (path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            return "";

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch
        {
            return value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    public static string InferLevel(string text)
        => LogMessageLevelClassifier.InferLevel(text);

    public static string NormalizeLevel(string level)
    {
        var normalized = (level ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "e" or "err" or "error" or "fail" or "failed" or "failure" => "error",
            "w" or "warn" or "warning" => "warn",
            "ok" or "success" or "succeeded" or "done" => "success",
            _ => "info",
        };
    }

    public static IBrush BrushForLevel(string level) => NormalizeLevel(level) switch
    {
        "error" => Brushes.Firebrick,
        "warn" => Brushes.DarkOrange,
        "success" => Brushes.SeaGreen,
        _ => Brushes.Black,
    };

    public static IBrush AccentBrushForLevel(string level) => BrushForLevel(level);

    public static IBrush TimestampForeground => Brushes.Gray;

    public static string FormatLevel(string level) => NormalizeLevel(level) switch
    {
        "error" => "ERROR",
        "warn" => "WARN",
        "success" => "SUCCESS",
        _ => "INFO",
    };

    private static (string Level, string Project, string Line) ParseHeader(string line)
    {
        var match = HeaderRegex.Match(line);
        if (!match.Success)
        {
            var inferred = InferLevel(line);
            return (inferred, "", line);
        }

        var declaredLevel = NormalizeLevel(match.Groups["level"].Value);
        var project = match.Groups["project"].Value.Trim();
        var rest = match.Groups["rest"].Value.TrimStart();
        var inferredLevel = InferLevel(rest);
        var level = declaredLevel == "info" && inferredLevel != "info"
            ? inferredLevel
            : declaredLevel;
        var normalizedLine = BuildHeaderLine(
            match.Groups["time"].Value.Trim(),
            level,
            project,
            rest);
        return (level, project, normalizedLine);
    }

    private static bool IsProblemLevel(string level) =>
        NormalizeLevel(level) is "error" or "warn";

    private static bool ContainsProblemKeyword(string text) =>
        ContainsAny(text, "失败", "错误", "异常", "无法", "终止", "超时", "重试", "兜底", "警告",
            "failed", "failure", "error", "exception", "retry", "timeout", "warn", "warning");

    private static bool ContainsAny(string text, params string[] keywords)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildHeaderLine(string time, string level, string project, string rest)
    {
        var projectPart = string.IsNullOrWhiteSpace(project) ? "" : $" [{project}]";
        return $"[{time}] {FormatLevel(level)}{projectPart} {rest}".TrimEnd();
    }

    private static string ToneForRow(QueueProjectRowViewModel row)
    {
        if (row.HasFailure)
            return "failed";
        if (IsUploadRunning(row))
            return "running";
        if (row.StatusText == QueueStepStatus.Running)
            return "running";
        if (row.IsUploadCompleted)
            return "ok";
        if (row.StatusText == QueueStepStatus.WaitingUploadSlot || row.IsPendingUpload)
            return "waiting";
        return "none";
    }

    private static bool IsUploadRunning(QueueProjectRowViewModel row) =>
        string.Equals(row.Item.CurrentStep, QueueStepRegistry.UploadSeries, StringComparison.Ordinal)
        || string.Equals(row.UploadStatus, QueueStepStatus.Running, StringComparison.Ordinal);
}
