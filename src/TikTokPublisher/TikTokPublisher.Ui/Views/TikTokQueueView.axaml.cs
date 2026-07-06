using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Config;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class TikTokQueueView : UserControl
{
    private MainViewModel? _vm;
    private BrowserSessionHost? _browserHost;
    private Action? _ensureBrowserMounted;
    private readonly EmbeddedBrowserPublishAutomation _automation = new();
    private EmbeddedBrowserProvider? _browserProvider;
    private PublishScheduler? _scheduler;
    private bool _ready;
    private TikTokPublishConfig _publishConfig = TikTokPublishConfig.Load();
    private CancellationTokenSource? _publishCts;
    private readonly PublishRunStateStore _runState = PublishRunStateStore.Load();
    private readonly Queue<ManualInterventionDialogRequest> _manualInterventionDialogs = new();
    private bool _manualInterventionDialogOpen;
    private QueueUiProgressSink? _queueProgressSink;
    private static readonly double[] QueueTableDefaultColumnWidths =
    {
        48, 56, 104, 210, 210, 60, 128, 68, 68, 68, 68, 68, 68, 68, 68, 68, 180,
    };
    private static readonly double[] QueueTableMinColumnWidths =
    {
        42, 48, 72, 120, 120, 48, 92, 56, 56, 56, 56, 62, 62, 56, 56, 56, 120,
    };
    private readonly double[] _queueTableColumnWidths = QueueTableDefaultColumnWidths.ToArray();
    private readonly List<WeakReference<Grid>> _queueTableRowGrids = new();
    private int _queueResizeColumnIndex = -1;
    private double _queueResizeStartX;
    private double _queueResizeStartWidth;

    public event EventHandler? OpenBrowserRequested;
    public event EventHandler? OpenLogsRequested;
    public event Action<AccountItemViewModel>? PublishBrowserFocusRequested;

    public TikTokQueueView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    public void Initialize(MainViewModel vm, BrowserSessionHost browserHost, Action? ensureBrowserMounted = null)
    {
        _vm = vm;
        _browserHost = browserHost;
        _ensureBrowserMounted = ensureBrowserMounted;
        DataContext = vm;
        _queueProgressSink = new QueueUiProgressSink(vm.HandleQueueWorkerProgress);
        vm.NavigateRequested += OnNavigateRequested;
        vm.AccountSwitchRequested += OnAccountSwitchRequested;
        vm.ManualInterventionDialogRequested += OnManualInterventionDialogRequested;
        vm.PropertyChanged += OnQueueRunningPropertyChanged;
        RefreshQueueRunButtons();
    }

    private void OnQueueRunningPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 并行队列下 IsQueueRunning 不足以反映按钮状态：切工作目录 / 任一队列启停都要刷新。
        if (e.PropertyName is nameof(MainViewModel.IsQueueRunning)
            or nameof(MainViewModel.RunningWorkspacesSummary)
            or nameof(MainViewModel.WorkspacePath))
            Dispatcher.UIThread.Post(RefreshQueueRunButtons);
    }

    private void RefreshQueueRunButtons()
    {
        var anyRunning = _vm?.IsQueueRunning == true;
        var currentRunning = _vm?.IsCurrentWorkspaceQueueRunning() == true;
        // 仅当前工作目录在跑时才禁用「执行勾选队列」；其他账号的队列不影响本工作目录启动。
        if (StartQueueButton is not null) StartQueueButton.IsEnabled = !currentRunning;
        if (StartAllQueuesButton is not null) StartAllQueuesButton.IsEnabled = !anyRunning;
        if (StopQueueButton is not null) StopQueueButton.IsEnabled = anyRunning;
    }

    private void OnManualInterventionDialogRequested(ManualInterventionDialogRequest request)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            _manualInterventionDialogs.Enqueue(request);
            await ProcessManualInterventionDialogsAsync();
        });
    }

    private async Task ProcessManualInterventionDialogsAsync()
    {
        if (_manualInterventionDialogOpen) return;

        _manualInterventionDialogOpen = true;
        try
        {
            while (_manualInterventionDialogs.Count > 0)
            {
                var request = _manualInterventionDialogs.Dequeue();
                var action = await ShowManualInterventionDialogAsync(request);
                var handled = _vm?.ResolveManualIntervention(action, request.WorkspaceRoot) == true;
                if (!handled && _vm is not null)
                    _vm.StatusMessage = "人工介入请求已不存在，可能已被队列停止或处理。";
            }
        }
        finally
        {
            _manualInterventionDialogOpen = false;
        }
    }

    private Task<string> ShowManualInterventionDialogAsync(ManualInterventionDialogRequest request)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new Window
        {
            Title = "上传失败，等待人工介入",
            Width = 560,
            Height = 280,
            CanResize = false,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
        };

        var openBrowserButton = BuildDialogButton("打开外部浏览器", () => OpenSelectedAccountExternalBrowser());
        var skipButton = BuildDialogButton("跳过此项目", () =>
        {
            tcs.TrySetResult("failed");
            dialog.Close();
        });
        skipButton.Classes.Add("dangerAction");
        var successButton = BuildDialogButton("已人工处理，标记成功", () =>
        {
            tcs.TrySetResult("success");
            dialog.Close();
        }, primary: true);

        dialog.Closed += (_, _) => tcs.TrySetResult("failed");
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"项目：{request.ProjectTitle}",
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = request.ErrorMessage,
                    Foreground = Brushes.Firebrick,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "浏览器会保持打开。你可以先在浏览器中人工处理，处理完成后点击“已人工处理，标记成功”；如果不处理，点击“跳过此项目”继续队列。",
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        openBrowserButton,
                        skipButton,
                        successButton,
                    },
                },
            },
        };

        if (owner is not null)
            dialog.Show(owner);
        else
            dialog.Show();

        return tcs.Task;
    }

    public async void OpenAccountSettings() => await ShowAccountSettingsDialogAsync();

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _ready = true;
        ApplyQueueTableColumnWidths();
        ApplyConfigToVm();
        if (!string.IsNullOrWhiteSpace(_vm?.WorkspacePath))
            _vm.RefreshWorkspaceProjects();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.NavigateRequested -= OnNavigateRequested;
            _vm.AccountSwitchRequested -= OnAccountSwitchRequested;
            _vm.ManualInterventionDialogRequested -= OnManualInterventionDialogRequested;
            _vm.PropertyChanged -= OnQueueRunningPropertyChanged;
        }

        if (_vm is null && DataContext is MainViewModel vm)
            Initialize(vm, _browserHost ?? new BrowserSessionHost());
    }

    private void ApplyConfigToVm()
    {
        var vm = _vm;
        if (vm is null) return;
        var fa = _publishConfig.FinalAction switch
        {
            "publish" => FinalAction.Publish,
            "save" or "draft" => FinalAction.Draft,
            _ => FinalAction.None,
        };
        var choice = vm.FinalActionChoices.FirstOrDefault(c => c.Value == fa);
        if (choice != null) vm.SelectedFinalAction = choice;
    }

    private void ApplyConfigDefaults(PublishItem item)
    {
        var c = _publishConfig;
        if (c.FillDescription && string.IsNullOrEmpty(item.Description) && !string.IsNullOrWhiteSpace(c.DescriptionTemplate))
            item.Description = c.DescriptionTemplate.Trim();
        if (string.IsNullOrEmpty(item.DramaName) && !string.IsNullOrWhiteSpace(c.DramaName))
            item.DramaName = c.DramaName.Trim();
        if (string.IsNullOrEmpty(item.Title))
        {
            item.Title = !string.IsNullOrWhiteSpace(item.DramaName)
                ? item.DramaName!
                : Path.GetFileNameWithoutExtension(item.VideoPath);
        }
        if (c.ReplaceCover && item.CoverPath is null && !string.IsNullOrWhiteSpace(c.CoverImagePath))
            item.CoverPath = c.CoverImagePath.Trim();
    }

    private void OnAccountSwitchRequested(AccountItemViewModel account)
    {
        // 浏览器可见性由 TikTokBrowserView 统一处理；上传页切账号不再重复遍历 WebView2 host
        // 做 COM 可见性调用（多账号并行时这是切账号卡顿的主因之一）。
    }

    private void OnNavigateRequested(AccountItemViewModel account, string url)
    {
        if (!_ready || _browserHost is null) return;
        var host = _browserHost.GetOrCreateHost(account);
        _browserHost.ShowAccount(account);
        host.Navigate(url);
        OpenBrowserRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnPickWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (Storage is null || _vm is null) return;
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 TikTok 上传工作目录",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        var path = folder.Path.LocalPath;
        var boundId = WorkspaceBindingService.ResolveAccountProfileId(path);
        if (!string.IsNullOrWhiteSpace(boundId))
        {
            var bound = _vm.FindAccount(boundId);
            if (bound is not null && bound.Id != _vm.SelectedAccount?.Id)
            {
                _vm.SelectedAccount = bound;
                _vm.StatusMessage = $"工作目录已绑定账号「{bound.DisplayName}」，已自动切换";
            }
        }

        _vm.SetWorkspacePath(path);
    }

    private async Task ShowAccountSettingsDialogAsync()
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null)
        {
            if (vm is not null) vm.StatusMessage = "请先选择要编辑的账号";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var ok = await new AccountSettingsDialog(account.Model).ShowDialog<bool>(owner);
        if (!ok) return;

        vm.SaveAccountProfile(account.Model);
        vm.StatusMessage = $"已保存账号「{account.DisplayName}」的设置";
        await InfoDialog.ShowSaveSuccessAsync(owner, "账号设置已保存成功。");
    }

    private void OnScanWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (string.IsNullOrWhiteSpace(_vm.WorkspacePath))
        {
            _vm.StatusMessage = "请先选择工作目录";
            return;
        }
        _vm.RefreshWorkspaceProjects();
    }

    private void OnOpenBrowserClick(object? sender, RoutedEventArgs e)
    {
        OpenSelectedAccountExternalBrowser();
    }

    private void OpenSelectedAccountExternalBrowser()
    {
        var vm = _vm;
        if (vm?.SelectedAccount is null)
        {
            if (vm is not null) vm.StatusMessage = "请先选择账号";
            return;
        }

        var account = vm.SelectedAccount.Model;
        var url = string.IsNullOrWhiteSpace(account.TiktokSeriesUrl)
            ? TikTokUrls.DefaultSeriesDraftUrl
            : account.TiktokSeriesUrl.Trim();

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });

            vm.StatusMessage = $"[{vm.SelectedAccount.DisplayName}] 已打开外部浏览器：{url}";
            vm.AppendLog(vm.StatusMessage);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"打开外部浏览器失败：{ex.Message}";
        }
    }

    private void OnOpenLogsClick(object? sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenOriginalProjectFolderClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var row = (sender as Control)?.DataContext as QueueProjectRowViewModel;
        OpenQueueProjectFolder(row?.OriginalProjectDir, "原剧名");
    }

    private void OnOpenNewProjectFolderClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var row = (sender as Control)?.DataContext as QueueProjectRowViewModel;
        OpenQueueProjectFolder(row?.NewProjectDir, "新剧名");
    }

    private void OpenQueueProjectFolder(string? path, string label)
    {
        var vm = _vm;
        if (string.IsNullOrWhiteSpace(path))
        {
            if (vm is not null) vm.StatusMessage = $"未找到{label}目录";
            return;
        }

        try
        {
            var folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
            if (!Directory.Exists(folder))
            {
                if (vm is not null) vm.StatusMessage = $"{label}目录不存在：{folder}";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });

            if (vm is not null) vm.StatusMessage = $"已打开{label}目录：{folder}";
        }
        catch (Exception ex)
        {
            if (vm is not null) vm.StatusMessage = $"打开{label}目录失败：{ex.Message}";
        }
    }

    private void OnQueueTableRowLoaded(object? sender, RoutedEventArgs e)
    {
        if ((sender as Border)?.Child is not Grid grid)
            return;

        _queueTableRowGrids.Add(new WeakReference<Grid>(grid));
        ApplyColumnWidths(grid);
    }

    private void OnQueueColumnResizerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control ||
            !int.TryParse(control.Tag?.ToString(), out var columnIndex) ||
            columnIndex < 0 ||
            columnIndex >= _queueTableColumnWidths.Length)
        {
            return;
        }

        _queueResizeColumnIndex = columnIndex;
        _queueResizeStartX = e.GetPosition(QueueTableHeaderGrid).X;
        _queueResizeStartWidth = _queueTableColumnWidths[columnIndex];
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void OnQueueColumnResizerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_queueResizeColumnIndex < 0)
            return;

        var delta = e.GetPosition(QueueTableHeaderGrid).X - _queueResizeStartX;
        var minWidth = QueueTableMinColumnWidths[_queueResizeColumnIndex];
        var width = Math.Max(minWidth, _queueResizeStartWidth + delta);
        if (Math.Abs(width - _queueTableColumnWidths[_queueResizeColumnIndex]) < 0.5)
            return;

        _queueTableColumnWidths[_queueResizeColumnIndex] = width;
        ApplyQueueTableColumnWidths();
        e.Handled = true;
    }

    private void OnQueueColumnResizerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_queueResizeColumnIndex < 0)
            return;

        e.Pointer.Capture(null);
        _queueResizeColumnIndex = -1;
        e.Handled = true;
    }

    private void ApplyQueueTableColumnWidths()
    {
        var totalWidth = _queueTableColumnWidths.Sum();
        if (QueueTableRootGrid is not null)
            QueueTableRootGrid.Width = totalWidth;
        if (QueueTableHeaderGrid is not null)
            ApplyColumnWidths(QueueTableHeaderGrid);

        for (var i = _queueTableRowGrids.Count - 1; i >= 0; i--)
        {
            if (_queueTableRowGrids[i].TryGetTarget(out var grid))
                ApplyColumnWidths(grid);
            else
                _queueTableRowGrids.RemoveAt(i);
        }
    }

    private void ApplyColumnWidths(Grid grid)
    {
        if (grid.ColumnDefinitions.Count != _queueTableColumnWidths.Length)
            return;

        var totalWidth = _queueTableColumnWidths.Sum();
        grid.Width = totalWidth;
        if (grid.Parent is Border border)
            border.Width = totalWidth;

        for (var i = 0; i < _queueTableColumnWidths.Length; i++)
            grid.ColumnDefinitions[i].Width = new GridLength(_queueTableColumnWidths[i]);
    }

    private void OnSelectAllQueueClick(object? sender, RoutedEventArgs e)
    {
        if (QueueProjectList is null) return;
        QueueProjectList.SelectAll();
        _vm?.SetFilteredQueueRowsEnabled(true);
    }

    private void OnClearQueueSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (QueueProjectList is null) return;
        QueueProjectList.SelectedItems?.Clear();
        _vm?.SetFilteredQueueRowsEnabled(false);
    }

    private void OnSelectCompletedQueueClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;

        var completedRows = vm.SetFilteredCompletedQueueRowsEnabled();
        if (QueueProjectList is null) return;

        var selectedItems = QueueProjectList.SelectedItems;
        if (selectedItems is null) return;

        selectedItems.Clear();
        foreach (var row in completedRows)
            selectedItems.Add(row);
    }

    private void OnBindSelectedAccountClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null)
        {
            vm!.StatusMessage = "请先选择账号";
            return;
        }

        var projects = new List<QueueProjectItem>();
        if (QueueProjectList?.SelectedItems is { Count: > 0 } queueSelected)
        {
            foreach (var selected in queueSelected)
            {
                if (selected is QueueProjectRowViewModel row)
                    projects.Add(row.Item);
            }
        }

        if (projects.Count == 0)
        {
            vm.StatusMessage = "请先在队列表格中选择要绑定的项目";
            return;
        }

        if (!vm.BindAccountToProjects(account, projects))
        {
            vm.StatusMessage = "绑定失败（工作目录未设置？）";
            return;
        }

        vm.StatusMessage = $"已将 {projects.Count} 个项目绑定到「{account.DisplayName}」";
    }

    private void OnBindAllPendingClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null)
        {
            vm!.StatusMessage = "请先选择账号";
            return;
        }

        var pending = vm.GetPendingUploadProjects();
        if (pending.Count == 0)
        {
            vm.StatusMessage = "没有待上传项目";
            return;
        }

        if (!vm.BindAccountToProjects(account, pending))
        {
            vm.StatusMessage = "绑定失败";
            return;
        }

        vm.StatusMessage = $"已将 {pending.Count} 个待上传项目绑定到「{account.DisplayName}」";
    }

    private IEnumerable<QueueProjectRowViewModel> GetSelectedQueueRows() =>
        QueueProjectList.SelectedItems?.OfType<QueueProjectRowViewModel>() ?? Enumerable.Empty<QueueProjectRowViewModel>();

    private IEnumerable<QueueProjectRowViewModel> GetCheckedQueueRows() =>
        _vm?.FilteredQueueProjectRows.Where(row => row.IsEnabled) ?? Enumerable.Empty<QueueProjectRowViewModel>();

    private IReadOnlyList<QueueProjectRowViewModel> GetCheckedOrSelectedQueueRows()
    {
        var checkedRows = GetCheckedQueueRows().ToArray();
        return checkedRows.Length > 0 ? checkedRows : GetSelectedQueueRows().ToArray();
    }

    private QueueProjectRowViewModel? ResolveQueueRowFromSender(object? sender)
    {
        if ((sender as Control)?.DataContext is QueueProjectRowViewModel row)
            return row;

        var rows = GetCheckedOrSelectedQueueRows();
        return rows.Count == 1 ? rows[0] : null;
    }

    private void OnMarkUploadCompletedClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var row = ResolveQueueRowFromSender(sender);
        MarkUploadStatus(row is null ? Array.Empty<QueueProjectRowViewModel>() : new[] { row }, completed: true);
    }

    private void OnMarkUploadFailedClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var row = ResolveQueueRowFromSender(sender);
        MarkUploadStatus(row is null ? Array.Empty<QueueProjectRowViewModel>() : new[] { row }, completed: false);
    }

    private void OnMarkSelectedUploadCompletedClick(object? sender, RoutedEventArgs e) =>
        MarkUploadStatus(GetCheckedOrSelectedQueueRows(), completed: true);

    private void OnMarkSelectedUploadFailedClick(object? sender, RoutedEventArgs e) =>
        MarkUploadStatus(GetCheckedOrSelectedQueueRows(), completed: false);

    private void MarkUploadStatus(IReadOnlyList<QueueProjectRowViewModel> rows, bool completed)
    {
        var vm = _vm;
        if (vm is null) return;
        if (rows.Count == 0)
        {
            vm.StatusMessage = "请先勾选或选中要修改上传状态的项目";
            return;
        }

        try
        {
            vm.SetQueueProjectsUploadStatus(rows.Distinct().ToArray(), completed);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"修改上传状态失败：{ex.Message}";
        }
    }

    private IReadOnlyList<string> GetSelectedProjectDirs() =>
        GetSelectedQueueRows()
            .Select(row => row.Item.ProjectDir)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private QueueProjectRowViewModel? ResolveSingleRowForRename(object? sender)
    {
        if ((sender as Control)?.DataContext is QueueProjectRowViewModel direct)
            return direct;

        var selectedRows = GetSelectedQueueRows()
            .Distinct()
            .ToArray();
        if (selectedRows.Length == 1)
            return selectedRows[0];

        var checkedRows = GetCheckedQueueRows()
            .Distinct()
            .ToArray();
        return checkedRows.Length == 1 ? checkedRows[0] : null;
    }

    private IReadOnlyList<string>? GetCheckedProjectDirsInDisplayOrder()
    {
        var vm = _vm;
        if (vm is null) return null;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();

        void AddCheckedRows(IEnumerable<QueueProjectRowViewModel> rows)
        {
            foreach (var row in rows)
            {
                if (!row.IsEnabled || string.IsNullOrWhiteSpace(row.Item.ProjectDir))
                    continue;
                var fullPath = Path.GetFullPath(row.Item.ProjectDir);
                if (seen.Add(fullPath))
                    dirs.Add(fullPath);
            }
        }

        AddCheckedRows(vm.FilteredQueueProjectRows);
        AddCheckedRows(vm.QueueProjectRows);
        return dirs.Count == 0 ? null : dirs;
    }

    private sealed record UploadTitlesDialogResult(
        string RawText,
        string MatchMode);

    private static async Task<UploadTitlesDialogResult?> ShowUploadTitlesDialogAsync(Window owner)
    {
        var dialog = new Window
        {
            Title = "上传短剧 - TikTok",
            Width = 560,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var titleBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 220,
            Watermark = "例如：\n她的豪门，我的刑场\n岁岁冥婚鬼夫夜夜来\n\n剧名 + 集数匹配：\n凤月无凭 43",
        };
        var modeCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        modeCombo.Items.Add(new ComboBoxItem
        {
            Content = "仅剧名精确匹配",
            Tag = UploadTitleImportService.MatchModeTitle,
        });
        modeCombo.Items.Add(new ComboBoxItem
        {
            Content = "剧名 + 集数匹配",
            Tag = UploadTitleImportService.MatchModeTitleEpisode,
        });
        modeCombo.SelectedIndex = 0;
        var cancelButton = BuildDialogButton("取消", () => dialog.Close(null));
        var importButton = BuildDialogButton("确定", () =>
        {
            var mode = (modeCombo.SelectedItem as ComboBoxItem)?.Tag as string
                ?? UploadTitleImportService.MatchModeTitle;
            dialog.Close(new UploadTitlesDialogResult(titleBox.Text ?? "", mode));
        }, primary: true);
        var settings = ClientSettingsStore.Load();
        var episodeLimitText = settings.TiktokAllowOverLimitUploadImport
            ? $"当前导入集数限制：最小 {UploadTitleImportService.DefaultEpisodeMin} 集；超过 {UploadTitleImportService.DefaultEpisodeMax} 集也会加入队列，并只下载前 {settings.TiktokOverLimitDownloadEpisodeCount} 集。"
            : $"当前导入集数限制：最小 {UploadTitleImportService.DefaultEpisodeMin} 集，最大 {UploadTitleImportService.DefaultEpisodeMax} 集。超出范围的短剧会自动过滤，不加入队列。";

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "一行输入一个短剧名称。确定后会按精确搜索模式匹配短剧，并加入项目执行队列。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = episodeLimitText
                           + "确定后会自动执行下载、改写、海报、修复、校验、上传默认步骤，删除源视频默认不会自动启用。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "匹配模式",
                },
                modeCombo,
                titleBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, importButton },
                },
            },
        };

        return await dialog.ShowDialog<UploadTitlesDialogResult?>(owner);
    }

    private static async Task ShowMessageAsync(Window owner, string title, string message, bool warning = false)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 480,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var okButton = BuildDialogButton("确定", () => dialog.Close(), primary: !warning);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { okButton },
                },
            },
        };
        await dialog.ShowDialog<bool?>(owner);
    }

    private static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var cancelButton = BuildDialogButton("取消", () => dialog.Close(false));
        var continueButton = BuildDialogButton("继续", () => dialog.Close(true), primary: true);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        cancelButton,
                        continueButton,
                    },
                },
            },
        };

        return await dialog.ShowDialog<bool>(owner);
    }

    private static Button BuildDialogButton(string text, Action click, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 84,
        };
        if (primary)
            button.Classes.Add("primaryAction");
        button.Click += (_, _) => click();
        return button;
    }

    private static int ParseIntOrDefault(string? text, int fallback) =>
        int.TryParse((text ?? "").Trim(), out var value) ? value : fallback;

    private async void OnArchiveSelectedClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var rows = GetCheckedOrSelectedQueueRows();
        if (rows.Count == 0)
        {
            vm.StatusMessage = "请先勾选或选中要归档的项目";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            vm.StatusMessage = "无法打开确认弹窗";
            return;
        }

        if (!await ConfirmAsync(
                owner,
                "归档勾选项目",
                $"将归档勾选的 {rows.Count} 个项目，是否继续？"))
            return;

        await vm.ArchiveSelectedQueueProjectsAsync(rows);
    }

    private void OnRemoveSelectedClick(object? sender, RoutedEventArgs e)
    {
        _vm?.RemoveSelectedQueueProjects(GetSelectedQueueRows());
    }

    private async void OnImportLocalDramaClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null || Storage is null) return;
        if (string.IsNullOrWhiteSpace(vm.WorkspacePath))
        {
            vm.StatusMessage = "请先选择工作目录";
            return;
        }

        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择本地手动下载剧集目录",
            AllowMultiple = false,
            SuggestedStartLocation = await TryResolveFolderAsync(vm.WorkspacePath),
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        try
        {
            await vm.ImportLocalManualDramaAsync(folder.Path.LocalPath);
        }
        catch (Exception ex)
        {
            if (owner is not null)
                await ShowMessageAsync(owner, "导入本地剧集失败", ex.Message, warning: true);
        }
    }

    private async Task<IStorageFolder?> TryResolveFolderAsync(string path)
    {
        if (Storage is null || string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return await Storage.TryGetFolderFromPathAsync(path);
        }
        catch
        {
            return null;
        }
    }

    private async void OnUploadTitlesClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        if (string.IsNullOrWhiteSpace(vm.WorkspacePath))
        {
            vm.StatusMessage = "请先选择工作目录";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var request = await ShowUploadTitlesDialogAsync(owner);
        if (request is null) return;
        if (string.IsNullOrWhiteSpace(request.RawText))
        {
            await ShowMessageAsync(owner, "缺少剧名", "请输入至少一个短剧名称。", warning: true);
            return;
        }

        vm.StatusMessage = "正在按标题导入短剧…";
        UploadTitleImportResult? result;
        try
        {
            result = await vm.ImportUploadTitlesAsync(
                request.RawText,
                UploadTitleImportService.DefaultEpisodeMin,
                UploadTitleImportService.DefaultEpisodeMax,
                request.MatchMode,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"上传短剧导入失败：{ex.Message}";
            vm.AppendLog(vm.StatusMessage);
            await ShowMessageAsync(owner, "上传短剧失败", ex.Message, warning: true);
            return;
        }

        if (result is null)
            return;

        foreach (var failure in result.Failures)
            vm.AppendLog($"上传短剧未加入：{failure.Title}，{failure.Reason}");

        if (result.Duplicates.Count > 0)
        {
            var dupLines = string.Join('\n', result.Duplicates.Take(30).Select(title => $"· {title}"));
            var dupExtra = result.Duplicates.Count > 30 ? $"\n… 等共 {result.Duplicates.Count} 条" : "";
            await ShowMessageAsync(
                owner,
                "上传短剧 · 已跳过重复",
                $"以下 {result.Duplicates.Count} 个剧在管理系统已存在，已跳过未上传：\n\n{dupLines}{dupExtra}");
        }

        if (result.Failures.Count > 0)
        {
            var lines = string.Join('\n', result.Failures.Take(30).Select(item => $"· {item.Title}：{item.Reason}"));
            var extra = result.Failures.Count > 30 ? $"\n… 等共 {result.Failures.Count} 条" : "";
            await ShowMessageAsync(
                owner,
                "上传短剧 · 未加入的剧集",
                $"加入 {result.QueuedCount} 个，未加入 {result.FailedCount} 个：\n\n{lines}{extra}",
                warning: true);
        }
        else if (result.QueuedCount == 0)
        {
            await ShowMessageAsync(owner, "上传短剧", "没有可加入的剧集。");
            return;
        }

        if (vm.ShouldAutoStartQueueAfterUploadTitleImport(result))
            await StartQueueRunAsync();
    }

    private async void OnEditSelectedClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        // 优先取「启用」勾选的项目；没有勾选时回退到表格选中行。
        var dirs = GetCheckedOrSelectedQueueRows()
            .Select(row => row.Item.ProjectDir)
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (dirs.Count == 0)
        {
            vm.StatusMessage = "请先勾选要编辑的剧集";
            return;
        }

        var options = vm.CreateCurrentQueueRunOptionsSnapshot();
        options.EnabledSteps = new List<string> { QueueStepRegistry.UploadSeries };
        options.ForceRerunCompletedSteps = true;
        options.UploadEntryMode = "edit";
        await StartQueueRunAsync(options, dirs);
    }

    private async void OnRenameNewTitleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var vm = _vm;
        if (vm is null) return;

        var row = ResolveSingleRowForRename(sender);
        if (row is null)
        {
            vm.StatusMessage = "请只选中或勾选 1 个项目再修改新剧名";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            vm.StatusMessage = "无法打开修改新剧名弹窗";
            return;
        }

        var input = await TextPromptDialog.ShowAsync(
            owner,
            "修改新剧名",
            $"原剧名：{row.OriginalTitle}\n当前新剧名：{row.NewTitle}\n请输入新的新剧名：",
            row.NewTitle);
        if (input is null)
            return;

        try
        {
            await vm.RenameQueueProjectNewTitleAsync(row, input);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"修改新剧名失败：{ex.Message}";
            await ShowMessageAsync(owner, "修改新剧名失败", ex.Message, warning: true);
        }
    }

    private async void OnRepairSilenceClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var dirs = GetSelectedProjectDirs();
        if (dirs.Count == 0)
        {
            vm.StatusMessage = "请先选中要修复静音的视频";
            return;
        }

        var options = vm.CreateCurrentQueueRunOptionsSnapshot();
        options.EnabledSteps = new List<string> { QueueStepRegistry.SilenceRepair };
        options.ForceRerunCompletedSteps = true;
        options.UploadEntryMode = "";
        await StartQueueRunAsync(options, dirs);
    }

    private void OnSelectToCurrentProjectClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null || QueueProjectList is null) return;
        var anchor = GetSelectedQueueRows().FirstOrDefault();
        if (anchor is null)
        {
            vm.StatusMessage = "请先选中一个当前项目";
            return;
        }

        var visibleRows = vm.FilteredQueueProjectRows.ToArray();
        var currentRow = Array.IndexOf(visibleRows, anchor);
        if (currentRow < 0)
        {
            vm.StatusMessage = "当前项目不在筛选结果中";
            return;
        }

        var checkedRows = visibleRows
            .Select((row, index) => new { row, index })
            .Where(item => item.row.IsEnabled)
            .Select(item => item.index)
            .ToArray();
        var anchorRow = checkedRows.Length == 0
            ? currentRow
            : checkedRows.OrderBy(index => Math.Abs(index - currentRow)).First();
        var low = Math.Min(anchorRow, currentRow);
        var high = Math.Max(anchorRow, currentRow);
        var rowsToEnable = visibleRows.Skip(low).Take(high - low + 1).ToArray();
        var matched = vm.SetQueueRowsEnabled(rowsToEnable, enabled: true);
        vm.StatusMessage = matched > 0
            ? $"已勾选到当前项目：{matched} 个"
            : "没有匹配到当前项目";
    }

    private async void OnDeleteSelectedClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var rows = GetCheckedOrSelectedQueueRows();
        if (rows.Count == 0)
        {
            vm.StatusMessage = "请先勾选或选中要删除的项目";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            vm.StatusMessage = "无法打开确认弹窗";
            return;
        }

        var ok = await ConfirmAsync(
            owner,
            "删除勾选项目",
            $"将删除勾选项目的源目录和 workflow 目录，共 {rows.Count} 个项目。此操作不可撤销，是否继续？");
        if (!ok) return;

        await vm.DeleteSelectedQueueProjectsAsync(rows);
    }

    private void OnExportExcelClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        try
        {
            var path = vm.ExportQueueExcel();
            vm.StatusMessage = $"已导出 Excel：{path}";
            vm.AppendLog(vm.StatusMessage);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"导出 Excel 失败：{ex.Message}";
        }
    }

    private void OnOpenExcelClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        try
        {
            var path = vm.ExportQueueExcel();
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            vm.StatusMessage = $"已打开 Excel：{path}";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"打开 Excel 失败：{ex.Message}";
        }
    }

    private async void OnSyncSelectedManagementClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        try
        {
            await vm.SyncSelectedManagementAsync(GetCheckedOrSelectedQueueRows(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"同步管理系统失败：{ex.Message}";
        }
    }

    private async void OnStartQueueClick(object? sender, RoutedEventArgs e)
        => await StartQueueRunAsync();

    public Task StartQueueRunFromRemoteAsync(
        QueueRunOptions? optionsOverride = null,
        IReadOnlyCollection<string>? projectDirFilter = null)
        => StartQueueRunAsync(optionsOverride, projectDirFilter);

    private async Task StartQueueRunAsync(
        QueueRunOptions? optionsOverride = null,
        IReadOnlyCollection<string>? projectDirFilter = null)
    {
        var vm = _vm;
        if (vm is null) return;
        if (vm.IsCurrentWorkspaceQueueRunning())
        {
            vm.StatusMessage = "当前工作目录队列已在运行中，本次点击未生效；如需按新的步骤勾选重跑，请先点「停止」等待队列结束后再执行";
            vm.AppendLog(vm.StatusMessage);
            return;
        }

        if (string.IsNullOrWhiteSpace(vm.WorkspacePath))
        {
            vm.StatusMessage = "请先选择工作目录";
            return;
        }

        if (vm.FilteredQueueProjectRows.Count == 0)
        {
            vm.StatusMessage = "队列为空，请先刷新项目";
            return;
        }

        if (projectDirFilter is not null && projectDirFilter.Count == 0)
        {
            vm.StatusMessage = "请先在队列表格中选择项目";
            return;
        }

        var orderedProjectDirFilter = projectDirFilter ?? GetCheckedProjectDirsInDisplayOrder();

        await Task.Yield();

        var host = CreateQueuePublishHost();
        var ct = vm.BeginQueueRun();
        RefreshQueueRunButtons();
        vm.StatusMessage = "TikTok 队列执行中…";
        try
        {
            var summary = await vm.RunQueueWorkerAsync(
                host,
                p => _queueProgressSink?.Post(p),
                (root, items) => Dispatcher.UIThread.Post(() => vm.ApplyPersistedQueueItems(root, items)),
                ct,
                optionsOverride,
                orderedProjectDirFilter);
            if (summary is not null && !summary.Stopped && summary.StoppedAccountCount > 0)
                vm.StatusMessage = $"队列结束：成功 {summary.SuccessCount}，失败 {summary.FailedCount}，已按账号停止 {summary.StoppedAccountCount} 个";
            if (summary is not null && !summary.Stopped && summary.StoppedAccountCount == 0)
                vm.StatusMessage = $"队列结束：成功 {summary.SuccessCount}，失败 {summary.FailedCount}";
        }
        catch (OperationCanceledException)
        {
            vm.StatusMessage = "队列已停止";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"队列出错：{ex.Message}";
        }
        finally
        {
            vm.EndQueueRun();
            RefreshQueueRunButtons();
        }
    }

    private async void OnStartAllQueuesClick(object? sender, RoutedEventArgs e)
        => await StartAllQueueRunAsync();

    public Task StartAllQueueRunFromRemoteAsync(
        QueueRunOptions? optionsOverride,
        IReadOnlyList<WorkspaceQueueTarget> targets)
        => StartAllQueueRunAsync(optionsOverride, targets);

    private async Task StartAllQueueRunAsync(
        QueueRunOptions? optionsOverride = null,
        IReadOnlyList<WorkspaceQueueTarget>? targetsOverride = null)
    {
        var vm = _vm;
        if (vm is null) return;
        if (vm.IsQueueRunning)
        {
            vm.StatusMessage = "已有工作目录队列在运行中";
            return;
        }

        var targets = targetsOverride is { Count: > 0 }
            ? targetsOverride
            : vm.BuildAccountWorkspaceTargets();
        if (targets.Count == 0)
        {
            vm.StatusMessage = "没有可执行的工作目录（请为账号配置有效工作目录）";
            return;
        }

        var host = CreateQueuePublishHost();
        var ct = vm.BeginQueueRun();
        RefreshQueueRunButtons();
        vm.StatusMessage = $"并行执行 {targets.Count} 个工作目录队列…";
        try
        {
            var summaries = await vm.RunAllAccountWorkspaceQueuesAsync(
                host,
                p => _queueProgressSink?.Post(p),
                (root, items) => Dispatcher.UIThread.Post(() => vm.ApplyPersistedQueueItems(root, items)),
                ct,
                targets,
                optionsOverride);

            var success = summaries.Sum(s => s?.SuccessCount ?? 0);
            var failed = summaries.Sum(s => s?.FailedCount ?? 0);
            var stopped = summaries.Any(s => s?.Stopped == true);
            var stoppedAccounts = summaries.Sum(s => s?.StoppedAccountCount ?? 0);
            if (!stopped && stoppedAccounts > 0)
                vm.StatusMessage = $"多工作目录队列结束：成功 {success}，失败 {failed}，已按账号停止 {stoppedAccounts} 个";
            else
                vm.StatusMessage = stopped
                ? "多工作目录队列已停止"
                : $"多工作目录队列结束：成功 {success}，失败 {failed}";
        }
        catch (OperationCanceledException)
        {
            vm.StatusMessage = "多工作目录队列已停止";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"多工作目录队列出错：{ex.Message}";
        }
        finally
        {
            vm.EndQueueRun();
            RefreshQueueRunButtons();
        }
    }

    private void OnStopQueueClick(object? sender, RoutedEventArgs e)
    {
        _vm?.RequestStopQueue();
        if (_vm is not null) _vm.StatusMessage = "正在停止队列…";
        RefreshQueueRunButtons();
    }

    private void SetQueueRunning(bool running)
    {
        if (StartQueueButton is not null) StartQueueButton.IsEnabled = !running;
        if (StopQueueButton is not null) StopQueueButton.IsEnabled = running;
    }

    private QueuePublishHost CreateQueuePublishHost() => new(
        EnsureAccountBrowserReadyAsync,
        PublishQueueProjectAsync);

    private EmbeddedBrowserProvider RequireBrowserProvider()
    {
        if (_browserHost is null || _vm is null)
            throw new InvalidOperationException("队列视图尚未初始化内置浏览器。");

        return _browserProvider ??= new EmbeddedBrowserProvider(
            _browserHost,
            account => _vm.FindAccount(account.Id) ?? _vm.Accounts.FirstOrDefault(a => a.Id == account.Id),
            vm => PublishBrowserFocusRequested?.Invoke(vm),
            () => _ensureBrowserMounted?.Invoke());
    }

    private PublishScheduler RequireScheduler() =>
        _scheduler ??= new PublishScheduler(_automation, RequireBrowserProvider());

    private static bool UsesExternalUploadBrowser(TikTokAccountProfile account) =>
        string.Equals((account.TiktokUploadBrowserMode ?? "").Trim(), "external", StringComparison.OrdinalIgnoreCase);

    private static bool UsesPlaywrightUploadBrowser(TikTokAccountProfile account) =>
        string.Equals((account.TiktokUploadBrowserMode ?? "").Trim(), "playwright", StringComparison.OrdinalIgnoreCase);

    /// <summary>账号选了外部浏览器且其 CDP 端点确实可达时返回 true；否则（含未配置/不可达）返回 false，交由调用方回退内置浏览器。</summary>
    private static async Task<bool> IsExternalUploadBrowserReadyAsync(
        TikTokAccountProfile account,
        Action<string>? log,
        CancellationToken ct)
    {
        if (!UsesExternalUploadBrowser(account))
            return false;

        var endpoint = (account.TiktokFingerprintBrowserCdpEndpoint ?? "").Trim();
        if (string.IsNullOrEmpty(endpoint))
        {
            log?.Invoke("已选择外部浏览器上传，但未配置 CDP 端点，自动回退到内置浏览器。");
            return false;
        }

        if (!await EmbeddedBrowserCdpProbe.IsReachableAsync(endpoint, ct).ConfigureAwait(false))
        {
            log?.Invoke($"外部浏览器 CDP 不可达（{endpoint}），自动回退到内置浏览器上传。");
            return false;
        }

        log?.Invoke($"使用外部浏览器上传（CDP：{endpoint}）");
        return true;
    }

    private async Task<QueueBrowserReadyResult> EnsureAccountBrowserReadyAsync(
        TikTokAccountProfile account,
        Action<string>? log,
        CancellationToken ct)
    {
        // 独立 Playwright 浏览器：只需存在授权文件（发布时 launch 独立浏览器并复用登录态）。
        if (UsesPlaywrightUploadBrowser(account))
        {
            var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
            if (!File.Exists(authPath))
            {
                return QueueBrowserReadyResult.NotReady(
                    "独立浏览器上传需要先在「浏览器」页用内置浏览器登录一次以生成授权文件。");
            }

            log?.Invoke($"上传浏览器：独立浏览器（{(account.TiktokPlaywrightUploadHeadless ? "无头" : "有头")}）");
            return QueueBrowserReadyResult.Ready();
        }

        // 外部浏览器可用则用外部；不可用（未配置/不可达）自动回退内置浏览器。
        if (await IsExternalUploadBrowserReadyAsync(account, log, ct).ConfigureAwait(false))
            return QueueBrowserReadyResult.Ready();

        var provider = RequireBrowserProvider();
        return await provider
            .EnsureBrowserReadyAsync(account, ct, EmbeddedBrowserAccessOptions.Background, log)
            .ConfigureAwait(false);
    }

    private async Task<PublishResult> PublishQueueProjectAsync(
        TikTokAccountProfile account,
        QueueProjectItem project,
        FinalAction finalAction,
        QueueRunOptions options,
        Action<string> log,
        CancellationToken ct)
    {
        // 与 EnsureAccountBrowserReadyAsync 保持一致：独立浏览器 > 外部浏览器(可达) > 内置浏览器。
        IEmbeddedBrowser? browser;
        var usingEmbeddedBrowser = false;
        if (UsesPlaywrightUploadBrowser(account))
        {
            // 独立浏览器由发布自动化内部 launch，这里只传标记载体。
            browser = new PlaywrightLaunchBrowser(account);
        }
        else if (await IsExternalUploadBrowserReadyAsync(account, log, ct).ConfigureAwait(false))
        {
            browser = new ExternalCdpBrowser(account);
        }
        else
        {
            browser = await RequireBrowserProvider()
                .GetBrowserAsync(account, ct, EmbeddedBrowserAccessOptions.Background)
                .ConfigureAwait(false);
            usingEmbeddedBrowser = true;
        }

        if (browser is null)
            return PublishResult.Fail("内置浏览器未就绪或未登录，请先在「浏览器」页完成登录");

        var item = QueuePublishHost.ToPublishItem(project);
        item.ForceEditUpload = string.Equals(options.UploadEntryMode, "edit", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(item.VideoPath))
            return PublishResult.Fail("项目没有可用视频");

        ApplyConfigDefaults(item);
        // 队列上传按账号配置的「提交动作」决定最终动作（对齐 Python submit_action 行为）。
        var effectiveAction = ResolveAccountFinalAction(account, finalAction);
        log($"最终动作：{FinalActionLabel(effectiveAction)}（来自账号「{account.DisplayName}」的提交动作配置）");
        var attemptSignature = UploadAttemptSignature(project.ProjectDir);
        var result = await _automation.PublishAsync(account, item, browser, effectiveAction, log, ct).ConfigureAwait(false);
        if (!result.Ok &&
            usingEmbeddedBrowser &&
            IsRecoverableEmbeddedBrowserFailure(result.Message) &&
            string.Equals(attemptSignature, UploadAttemptSignature(project.ProjectDir), StringComparison.Ordinal))
        {
            var accountVm = _vm?.FindAccount(account.Id) ?? _vm?.FindAccount(account.DisplayName);
            if (accountVm is not null && _browserHost is not null)
            {
                log($"检测到内置浏览器连接异常，自动重建账号「{account.DisplayName}」浏览器并重试一次：{result.Message}");
                var targetUrl = string.IsNullOrWhiteSpace(account.TiktokSeriesUrl)
                    ? TikTokUrls.DefaultSeriesDraftUrl
                    : account.TiktokSeriesUrl.Trim();
                await _browserHost.RecreateHostAsync(accountVm, ct, log, targetUrl).ConfigureAwait(false);
                browser = await RequireBrowserProvider()
                    .GetBrowserAsync(account, ct, EmbeddedBrowserAccessOptions.Background)
                    .ConfigureAwait(false);
                if (browser is null)
                    return PublishResult.Fail($"{result.Message}；自动重建后内置浏览器仍未就绪");

                result = await _automation.PublishAsync(account, item, browser, effectiveAction, log, ct)
                    .ConfigureAwait(false);
            }
        }

        return result;
    }

    private static bool IsRecoverableEmbeddedBrowserFailure(string? message)
    {
        var text = message ?? "";
        return text.Contains("连接浏览器自动化端口", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("CDP", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("WebView2", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Target page, context or browser has been closed", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Browser closed", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("TikTok 页面刷新后仍显示异常", StringComparison.OrdinalIgnoreCase);
    }

    private static string UploadAttemptSignature(string? projectDir)
    {
        try
        {
            var workflowDir = TikTokUploadStateStore.ResolveWorkflowProjectDir(projectDir);
            var state = TikTokUploadStateStore.LoadState(workflowDir);
            var started = state.TryGetValue("last_upload_step_started_at", out var startedValue)
                ? startedValue.ToString()
                : "";
            var count = state.TryGetValue("upload_step_attempt_count", out var countValue)
                ? countValue.ToString()
                : "";
            return $"{started}|{count}";
        }
        catch
        {
            return "";
        }
    }

    private static FinalAction ResolveAccountFinalAction(TikTokAccountProfile account, FinalAction fallback) =>
        (account.TiktokSubmitAction ?? "").Trim().ToLowerInvariant() switch
        {
            "submit" => FinalAction.Publish,
            "save" or "draft" => FinalAction.Draft,
            "none" => FinalAction.None,
            _ => fallback,
        };

    private static string FinalActionLabel(FinalAction action) => action switch
    {
        FinalAction.Publish => "提交",
        FinalAction.Draft => "保存草稿",
        _ => "只填不发",
    };

    private async void OnPublishClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null) { vm!.StatusMessage = "请先选择账号"; return; }

        if (await RequireBrowserProvider()
                .GetBrowserAsync(account.Model, CancellationToken.None, EmbeddedBrowserAccessOptions.Interactive)
                .ConfigureAwait(true) is null)
        {
            vm.StatusMessage = "内置浏览器未就绪，请先登录";
            OpenBrowserRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (Storage is null) return;

        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发布的视频",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("视频") { Patterns = new[] { "*.mp4", "*.mov", "*.m4v" } } },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        var item = new PublishItem { VideoPath = file.Path.LocalPath, Description = "【TikTok 发布测试】" };
        ApplyConfigDefaults(item);
        var job = new AccountPublishJob(account.Model, new[] { item });

        vm.StatusMessage = $"[{account.DisplayName}] 发布中：{item.DisplayName}…";
        try
        {
            await RequireScheduler().RunAsync(new[] { job }, FinalAction.None, maxParallelAccounts: 1,
                p => Dispatcher.UIThread.Post(() => vm.StatusMessage = $"[{p.AccountName}] {p.ItemName}：{p.Message}"),
                CancellationToken.None);
        }
        catch (Exception ex) { vm.StatusMessage = $"发布失败：{ex.Message}"; }
    }

    private async void OnPublishConfigClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var ok = await new PublishConfigDialog().ShowDialog<bool>(owner);
        if (ok)
        {
            _publishConfig = TikTokPublishConfig.Load();
            ApplyConfigToVm();
            if (_vm != null) _vm.StatusMessage = "发布配置已保存";
            await InfoDialog.ShowSaveSuccessAsync(owner, "发布配置已保存成功。");
        }
    }

    private async void OnAddMaterialsClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        var account = vm?.SelectedAccount;
        if (vm is null || account is null) { vm!.StatusMessage = "请先选择账号"; return; }
        if (Storage is null) return;

        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择素材视频（可多选）",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("视频") { Patterns = new[] { "*.mp4", "*.mov", "*.m4v" } } },
        });
        var n = 0;
        foreach (var f in files)
        {
            var item = new PublishItem { VideoPath = f.Path.LocalPath };
            ApplyConfigDefaults(item);
            vm.Tasks.Add(new PublishTaskItemViewModel(item, account));
            n++;
        }
        if (n > 0) vm.StatusMessage = $"已添加 {n} 条素材到「{account.DisplayName}」";
    }

    private async void OnImportTasksClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null || Storage is null) return;
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入发布任务清单(JSON)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        PublishTaskFile taskFile;
        try { taskFile = PublishTaskFile.Load(file.Path.LocalPath); }
        catch (Exception ex) { vm.StatusMessage = $"导入失败：{ex.Message}"; return; }

        var added = 0;
        var skipped = 0;
        foreach (var dto in taskFile.Tasks)
        {
            var acct = vm.FindAccount(dto.Account);
            if (acct is null) { skipped++; continue; }
            vm.Tasks.Add(new PublishTaskItemViewModel(dto.ToItem(), acct));
            added++;
        }
        var fa = taskFile.ResolveFinalAction();
        var choice = vm.FinalActionChoices.FirstOrDefault(c => c.Value == fa);
        if (choice != null) vm.SelectedFinalAction = choice;
        vm.StatusMessage = $"已导入 {added} 条任务" + (skipped > 0 ? $"，跳过 {skipped} 条（账号未匹配）" : "");
    }

    private void OnClearDoneClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var done = vm.Tasks.Where(t => t.Status == PublishTaskStatus.Done).ToList();
        foreach (var t in done) vm.Tasks.Remove(t);
        vm.StatusMessage = $"已清空 {done.Count} 条完成任务";
    }

    private async void OnStartPublishClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        if (_publishCts is not null) { vm.StatusMessage = "发布进行中…"; return; }

        var strategy = (_publishConfig.RunStrategy ?? "all").Trim().ToLowerInvariant();
        var candidates = (strategy == "retry_failed"
                ? vm.Tasks.Where(t => t.Status == PublishTaskStatus.Failed)
                : vm.Tasks.Where(t => t.Status is PublishTaskStatus.Pending or PublishTaskStatus.Failed))
            .ToList();
        if (candidates.Count == 0) { vm.StatusMessage = "没有待发布任务"; return; }

        var resumed = 0;
        if (strategy == "resume")
        {
            var remaining = new List<PublishTaskItemViewModel>();
            foreach (var t in candidates)
            {
                if (_runState.IsDone(PublishRunStateStore.SignatureFor(t.Account.Id, t.Item)))
                {
                    t.Status = PublishTaskStatus.Done;
                    t.Message = "已发布·续传跳过";
                    resumed++;
                }
                else remaining.Add(t);
            }
            candidates = remaining;
            if (candidates.Count == 0)
            {
                vm.StatusMessage = resumed > 0 ? $"全部 {resumed} 条已在续传记录中" : "没有待发布任务";
                return;
            }
        }

        var jobs = new List<AccountPublishJob>();
        foreach (var group in candidates.GroupBy(t => t.Account.Id))
        {
            var acctVm = group.First().Account;
            if (await RequireBrowserProvider()
                    .GetBrowserAsync(acctVm.Model, CancellationToken.None, EmbeddedBrowserAccessOptions.Background)
                    .ConfigureAwait(true) is null)
            {
                foreach (var t in group) { t.Status = PublishTaskStatus.Failed; t.Message = "内置浏览器未就绪"; }
                continue;
            }

            foreach (var t in group) { t.Status = PublishTaskStatus.Pending; t.Message = "排队中"; }
            jobs.Add(new AccountPublishJob(acctVm.Model, group.Select(t => t.Item).ToList()));
        }
        if (jobs.Count == 0) { vm.StatusMessage = "无可发布账号（请先在内置浏览器页登录）"; return; }

        var finalAction = vm.SelectedFinalAction?.Value ?? FinalAction.None;
        _publishCts = new CancellationTokenSource();
        SetPublishing(true);
        vm.StatusMessage = $"开始发布：{jobs.Count} 账号 / {candidates.Count} 素材（并发 {vm.MaxParallel}）";
        try
        {
            await RequireScheduler().RunAsync(jobs, finalAction, vm.MaxParallel,
                p => Dispatcher.UIThread.Post(() => UpdateTaskProgress(p)), _publishCts.Token);
            vm.StatusMessage = "发布结束";
        }
        catch (OperationCanceledException)
        {
            foreach (var t in vm.Tasks.Where(t => t.Status is PublishTaskStatus.Running or PublishTaskStatus.Pending))
            {
                t.Status = PublishTaskStatus.Pending;
                t.Message = "已停止";
            }
            vm.StatusMessage = "已停止";
        }
        catch (Exception ex) { vm.StatusMessage = $"发布出错：{ex.Message}"; }
        finally
        {
            _publishCts?.Dispose();
            _publishCts = null;
            SetPublishing(false);
        }
    }

    private void OnStopPublishClick(object? sender, RoutedEventArgs e)
    {
        _publishCts?.Cancel();
        if (StopPublishButton is not null) StopPublishButton.IsEnabled = false;
        if (_vm != null) _vm.StatusMessage = "正在停止…";
    }

    private void OnClearResumeClick(object? sender, RoutedEventArgs e)
    {
        var n = _runState.Count;
        _runState.Reset();
        if (_vm != null) _vm.StatusMessage = $"已清除续传记录（{n} 条）";
    }

    private void SetPublishing(bool running)
    {
        if (StartPublishButton is not null) StartPublishButton.IsEnabled = !running;
        if (StopPublishButton is not null) StopPublishButton.IsEnabled = running;
    }

    private void UpdateTaskProgress(PublishProgress p)
    {
        var vm = _vm;
        if (vm is null) return;
        var task = vm.Tasks.FirstOrDefault(t =>
            t.Account.Id == p.AccountId && t.VideoName == p.ItemName && t.Status != PublishTaskStatus.Done);
        if (task is null) return;
        task.Message = p.Message;
        task.Status = p.Done ? (p.Ok ? PublishTaskStatus.Done : PublishTaskStatus.Failed) : PublishTaskStatus.Running;
        if (p.Done && p.Ok)
        {
            _runState.MarkDone(PublishRunStateStore.SignatureFor(task.Account.Id, task.Item), task.VideoName, task.AccountName);
            if (!string.IsNullOrWhiteSpace(task.Item.ProjectDir) && !string.IsNullOrWhiteSpace(vm.WorkspacePath))
                vm.MarkProjectUploadCompleted(task.Item.ProjectDir);
        }
    }
}
