using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Desktop.ViewModels;

namespace PlatformPublisher.Desktop.Views;

public partial class KuaishouQueueLogView : UserControl
{
    private MainWindowViewModel? _vm;
    private bool _syncingSelection;
    private string _selectedProject = "";

    public event EventHandler? ReturnRequested;

    public KuaishouQueueLogView()
    {
        InitializeComponent();
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
            _vm.ActivityLogRecords.CollectionChanged -= OnLogsChanged;
        _vm = vm;
        _vm.ActivityLogRecords.CollectionChanged += OnLogsChanged;
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
        var personal = _vm.IsKuaishouPersonalPlatform;
        TitleLabel.Text = personal ? "快手分账 · 个人" : "快手分账 · 企业";
        ReturnButton.Content = personal ? "返回快手分账个人" : "返回快手分账企业";
        WorkspaceLabel.Text = string.IsNullOrWhiteSpace(_vm.DraftProjectDirectory)
            ? "工作目录：未选择"
            : $"工作目录：{_vm.DraftProjectDirectory}";
        StopButton.IsEnabled = _vm.IsBusy;
        RebuildProjects();
        RefreshLog();
    }

    private void RebuildProjects()
    {
        if (_vm is null) return;
        var rows = new List<LogProjectRow> { new("全部项目", "") };
        foreach (var job in _vm.VisibleJobs)
        {
            var name = job.ProjectName;
            if (string.IsNullOrWhiteSpace(name) || rows.Any(row => row.ProjectName == name))
                continue;
            rows.Add(new LogProjectRow(name, name));
        }

        _syncingSelection = true;
        ProjectList.ItemsSource = rows;
        var selected = rows.FirstOrDefault(row => row.ProjectName == _selectedProject) ?? rows[0];
        ProjectList.SelectedItem = selected;
        _selectedProject = selected.ProjectName;
        _syncingSelection = false;
    }

    private void RefreshLog()
    {
        if (_vm is null) return;
        var platform = _vm.SelectedPlatform.Value;
        var problemsOnly = ProblemsOnlyBox.IsChecked == true;
        var lines = _vm.ActivityLogRecords
            .Where(record => record.Platform == platform)
            .Where(record => string.IsNullOrEmpty(_selectedProject) ||
                             string.Equals(record.ProjectName, _selectedProject, StringComparison.Ordinal) ||
                             record.Text.Contains(_selectedProject, StringComparison.Ordinal))
            .Where(record => !problemsOnly || IsProblem(record.Text))
            .Select(record => record.Text)
            .ToList();
        SummaryLabel.Text = string.IsNullOrEmpty(_selectedProject)
            ? $"日志 {lines.Count} 条"
            : $"{_selectedProject} · 日志 {lines.Count} 条";
        var atEnd = LogScrollViewer.Offset.Y >=
                    Math.Max(0, LogScrollViewer.Extent.Height - LogScrollViewer.Viewport.Height - 8);
        LogTextBlock.Text = string.Join(Environment.NewLine, lines);
        if (atEnd || AutoFollowBox.IsChecked == true)
            LogScrollViewer.ScrollToEnd();
    }

    private static bool IsProblem(string text) =>
        text.Contains("失败", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("错误", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("WARN", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

    private void OnStopClick(object? sender, RoutedEventArgs e) => _vm?.StopCommand.Execute(null);

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not IClipboard clipboard) return;
        await clipboard.SetTextAsync(LogTextBlock.Text ?? "");
        if (_vm is not null) _vm.StatusMessage = "已复制日志";
    }

    private async void OnCopySelectionClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not IClipboard clipboard) return;
        var text = LogTextBlock.Text ?? "";
        var start = Math.Clamp(LogTextBlock.SelectionStart, 0, text.Length);
        var end = Math.Clamp(LogTextBlock.SelectionEnd, 0, text.Length);
        if (end < start) (start, end) = (end, start);
        await clipboard.SetTextAsync(start == end ? text : text[start..end]);
        if (_vm is not null) _vm.StatusMessage = "已复制选中日志";
    }

    private void OnSelectAllLogClick(object? sender, RoutedEventArgs e)
    {
        var text = LogTextBlock.Text ?? "";
        LogTextBlock.SelectionStart = 0;
        LogTextBlock.SelectionEnd = text.Length;
    }

    private void OnReturnClick(object? sender, RoutedEventArgs e) => ReturnRequested?.Invoke(this, EventArgs.Empty);

    private sealed record LogProjectRow(string Title, string ProjectName);
}
