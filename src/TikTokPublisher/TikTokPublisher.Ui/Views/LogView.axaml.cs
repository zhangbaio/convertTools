using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class LogView : UserControl
{
    private LogService? _logs;
    private MainViewModel? _vm;
    private bool _syncingProjectSelection;

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
        var current = LogTextBox.Text ?? string.Empty;
        if (string.Equals(current, next, StringComparison.Ordinal)) return;

        var selectionStart = LogTextBox.SelectionStart;
        var selectionEnd = LogTextBox.SelectionEnd;
        var hadSelection = selectionStart != selectionEnd;
        var caretWasAtEnd = LogTextBox.CaretIndex >= current.Length;
        LogTextBox.Text = next;
        if (hadSelection)
        {
            LogTextBox.SelectionStart = Math.Clamp(selectionStart, 0, next.Length);
            LogTextBox.SelectionEnd = Math.Clamp(selectionEnd, 0, next.Length);
        }
        else
        {
            LogTextBox.CaretIndex = caretWasAtEnd
                ? next.Length
                : Math.Clamp(selectionEnd, 0, next.Length);
        }
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
        var selected = LogTextBox.SelectedText;
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
        LogTextBox.Focus();
        LogTextBox.SelectAll();
        if (_vm is not null) _vm.StatusMessage = "已全选当前日志";
    }
}
