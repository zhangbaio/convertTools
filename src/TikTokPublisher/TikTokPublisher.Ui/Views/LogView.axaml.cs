using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class LogView : UserControl
{
    private LogService? _logs;
    private MainViewModel? _vm;
    private bool _syncingProjectSelection;
    private string _renderedText = "";
    private readonly List<LogEntry> _renderedEntries = [];
    private readonly List<int> _renderedInlineCounts = [];

    public event EventHandler? ReturnRequested;
    public event EventHandler? StopRequested;

    public LogView()
    {
        InitializeComponent();
        AutoFollowBox.IsCheckedChanged += (_, _) =>
        {
            if (_logs is not null) _logs.AutoFollowActiveProject = AutoFollowBox.IsChecked == true;
        };
        ProblemsOnlyBox.IsCheckedChanged += (_, _) =>
        {
            if (_logs is not null)
            {
                _logs.ProblemsOnly = ProblemsOnlyBox.IsChecked == true;
                RefreshView();
            }
        };
        ProjectList.SelectionChanged += (_, _) =>
        {
            if (_syncingProjectSelection) return;
            if (_logs is null || ProjectList.SelectedItem is not LogProjectItem item) return;
            if (AutoFollowBox.IsChecked == true)
                AutoFollowBox.IsChecked = false;
            _logs.SelectedProjectPath = item.ProjectPath;
            RefreshView();
        };
    }

    public void Bind(MainViewModel vm, LogService logs)
    {
        _vm = vm;
        _logs = logs;
        logs.Changed += RefreshView;
        RefreshView();
    }

    private void RefreshView()
    {
        if (_logs is null) return;
        WorkspaceLabel.Text = _logs.WorkspaceLabel;
        SummaryLabel.Text = _logs.SummaryText;
        StopButton.IsEnabled = _logs.IsRunning;
        ProjectList.ItemsSource = _logs.Projects;
        RefreshLogText();
        SyncProjectSelection();
    }

    private void RefreshLogText()
    {
        if (_logs is null) return;
        var next = _logs.BuildCopyText();
        if (string.Equals(_renderedText, next, StringComparison.Ordinal)) return;

        var selectionStart = LogTextBlock.SelectionStart;
        var selectionEnd = LogTextBlock.SelectionEnd;
        var hadSelection = selectionStart != selectionEnd;
        var wasAtEnd = LogScrollViewer.Offset.Y >=
                       Math.Max(0, LogScrollViewer.Extent.Height - LogScrollViewer.Viewport.Height - 4);

        var entries = _logs.RenderedEntries;
        var canUpdateIncrementally = TryResolveIncrementalUpdate(
            _renderedEntries,
            entries,
            out var removeLeadingCount);
        if (canUpdateIncrementally)
        {
            RemoveLeadingRenderedEntries(removeLeadingCount);
            for (var i = _renderedEntries.Count; i < entries.Count; i++)
            {
                if (i > 0)
                    LogTextBlock.Inlines?.Add(new LineBreak());
                _renderedInlineCounts.Add(AppendLogEntry(entries[i]));
                _renderedEntries.Add(entries[i]);
            }
        }
        else
        {
            LogTextBlock.Inlines?.Clear();
            _renderedInlineCounts.Clear();
            for (var i = 0; i < entries.Count; i++)
            {
                _renderedInlineCounts.Add(AppendLogEntry(entries[i]));
                if (i < entries.Count - 1)
                    LogTextBlock.Inlines?.Add(new LineBreak());
            }

            _renderedEntries.Clear();
            _renderedEntries.AddRange(entries);
            LogTextBlock.SelectionStart = 0;
            LogTextBlock.SelectionEnd = 0;
        }

        _renderedText = next;
        if (canUpdateIncrementally && removeLeadingCount == 0 && hadSelection)
        {
            // Appending preserves the existing text prefix, so the old selection indices
            // still refer to the same characters regardless of the platform newline width.
            LogTextBlock.SelectionStart = selectionStart;
            LogTextBlock.SelectionEnd = selectionEnd;
        }
        else if (wasAtEnd && !hadSelection)
        {
            Dispatcher.UIThread.Post(LogScrollViewer.ScrollToEnd, DispatcherPriority.Background);
        }
    }

    private void RemoveLeadingRenderedEntries(int count)
    {
        if (count <= 0)
            return;

        var inlines = LogTextBlock.Inlines;
        for (var i = 0; i < count; i++)
        {
            var nodesToRemove = _renderedInlineCounts[i] + 1;
            for (var node = 0; node < nodesToRemove && inlines is { Count: > 0 }; node++)
                inlines.RemoveAt(0);
        }

        _renderedEntries.RemoveRange(0, count);
        _renderedInlineCounts.RemoveRange(0, count);
        LogTextBlock.SelectionStart = 0;
        LogTextBlock.SelectionEnd = 0;
    }

    internal static bool TryResolveIncrementalUpdate(
        IReadOnlyList<LogEntry> previous,
        IReadOnlyList<LogEntry> current,
        out int removeLeadingCount)
    {
        if (previous.Count == 0)
        {
            removeLeadingCount = 0;
            return true;
        }

        for (var remove = 0; remove < previous.Count; remove++)
        {
            var retained = previous.Count - remove;
            if (retained > current.Count)
                continue;

            var matches = true;
            for (var i = 0; i < retained; i++)
            {
                if (ReferenceEquals(previous[remove + i], current[i]))
                    continue;

                matches = false;
                break;
            }

            if (!matches)
                continue;

            removeLeadingCount = remove;
            return true;
        }

        removeLeadingCount = 0;
        return false;
    }

    private static readonly char[] HeaderSeparators = [' ', '\t'];

    private int AppendLogEntry(LogEntry entry)
    {
        var before = LogTextBlock.Inlines?.Count ?? 0;
        var text = entry.Text;
        var timestampEnd = text.StartsWith("[", StringComparison.Ordinal)
            ? text.IndexOf(']')
            : -1;
        var levelLabel = LogService.FormatLevel(entry.Level);
        var levelStart = timestampEnd >= 0
            ? text.IndexOf(levelLabel, timestampEnd + 1, StringComparison.OrdinalIgnoreCase)
            : -1;

        if (timestampEnd < 0 || levelStart < 0)
        {
            AddRun(text, entry.Foreground);
            return (LogTextBlock.Inlines?.Count ?? before) - before;
        }

        AddRun(text[..(timestampEnd + 1)], LogService.TimestampForeground);
        if (levelStart > timestampEnd + 1)
            AddRun(text[(timestampEnd + 1)..levelStart], entry.Foreground);

        var levelEnd = levelStart + levelLabel.Length;
        if (levelEnd < text.Length &&
            Array.IndexOf(HeaderSeparators, text[levelEnd]) < 0)
        {
            AddRun(text[(timestampEnd + 1)..], entry.Foreground);
            return (LogTextBlock.Inlines?.Count ?? before) - before;
        }

        AddRun(text[levelStart..levelEnd], LogService.AccentBrushForLevel(entry.Level), FontWeight.Bold);
        if (levelEnd < text.Length)
            AddRun(text[levelEnd..], entry.Foreground);
        return (LogTextBlock.Inlines?.Count ?? before) - before;
    }

    private void AddRun(string text, IBrush foreground, FontWeight? fontWeight = null)
    {
        var run = new Run(text) { Foreground = foreground };
        if (fontWeight.HasValue)
            run.FontWeight = fontWeight.Value;
        LogTextBlock.Inlines?.Add(run);
    }

    private void SyncProjectSelection()
    {
        if (_logs is null) return;

        var selected = _logs.Projects.FirstOrDefault(p =>
                           string.Equals(p.ProjectPath, _logs.SelectedProjectPath, StringComparison.OrdinalIgnoreCase))
                       ?? _logs.Projects.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.ProjectPath));
        if (selected is null || ReferenceEquals(ProjectList.SelectedItem, selected))
            return;

        _syncingProjectSelection = true;
        try
        {
            ProjectList.SelectedItem = selected;
        }
        finally
        {
            _syncingProjectSelection = false;
        }
    }

    private void OnReturnClick(object? sender, RoutedEventArgs e) => ReturnRequested?.Invoke(this, EventArgs.Empty);

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        StopRequested?.Invoke(this, EventArgs.Empty);
        _vm?.RequestStopQueue();
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_logs is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(_logs.BuildCopyText());
        if (_vm is not null) _vm.StatusMessage = "已复制日志到剪贴板";
    }

    private async void OnCopySelectionClick(object? sender, RoutedEventArgs e)
    {
        var selected = LogTextBlock.SelectedText;
        if (string.IsNullOrEmpty(selected))
        {
            if (_vm is not null) _vm.StatusMessage = "请先在日志区域选择要复制的内容";
            return;
        }
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(selected);
        if (_vm is not null) _vm.StatusMessage = "已复制选中的日志内容";
    }

    private void OnSelectAllLogClick(object? sender, RoutedEventArgs e)
    {
        LogTextBlock.Focus();
        LogTextBlock.SelectAll();
        if (_vm is not null) _vm.StatusMessage = "已全选当前日志";
    }
}
