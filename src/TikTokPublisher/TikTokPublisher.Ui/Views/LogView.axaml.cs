using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class LogView : UserControl
{
    private LogService? _logs;
    private MainViewModel? _vm;

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
            if (_logs is null || ProjectList.SelectedItem is not LogProjectItem item) return;
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
        LogItems.ItemsSource = _logs.RenderedEntries;
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
}
