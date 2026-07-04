using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
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
    private readonly EmbeddedBrowserPublishAutomation _automation = new();
    private EmbeddedBrowserProvider? _browserProvider;
    private PublishScheduler? _scheduler;
    private bool _ready;
    private TikTokPublishConfig _publishConfig = TikTokPublishConfig.Load();
    private CancellationTokenSource? _publishCts;
    private readonly PublishRunStateStore _runState = PublishRunStateStore.Load();
    private readonly Queue<ManualInterventionDialogRequest> _manualInterventionDialogs = new();
    private bool _manualInterventionDialogOpen;

    public event EventHandler? OpenBrowserRequested;
    public event EventHandler? OpenLogsRequested;
    public event Action<AccountItemViewModel>? PublishBrowserFocusRequested;

    public TikTokQueueView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    public void Initialize(MainViewModel vm, BrowserSessionHost browserHost)
    {
        _vm = vm;
        _browserHost = browserHost;
        DataContext = vm;
        vm.NavigateRequested += OnNavigateRequested;
        vm.AccountSwitchRequested += OnAccountSwitchRequested;
        vm.ManualInterventionDialogRequested += OnManualInterventionDialogRequested;
        vm.PropertyChanged += OnQueueRunningPropertyChanged;
        RefreshQueueRunButtons();
    }

    private void OnQueueRunningPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsQueueRunning))
            Dispatcher.UIThread.Post(RefreshQueueRunButtons);
    }

    private void RefreshQueueRunButtons()
    {
        var running = _vm?.IsQueueRunning == true;
        if (StartQueueButton is not null) StartQueueButton.IsEnabled = !running;
        if (StartAllQueuesButton is not null) StartAllQueuesButton.IsEnabled = !running;
        if (StopQueueButton is not null) StopQueueButton.IsEnabled = running;
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

        var openBrowserButton = BuildDialogButton("打开浏览器", () => OpenBrowserRequested?.Invoke(this, EventArgs.Empty));
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
            "draft" => FinalAction.Draft,
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

    private void OnAccountSwitchRequested(AccountItemViewModel account) =>
        _browserHost?.ShowAccount(account);

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
        _vm.SetWorkspacePath(path);

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
        if (_vm?.SelectedAccount is null)
        {
            if (_vm is not null) _vm.StatusMessage = "请先选择账号";
            return;
        }

        _vm.BeginAccountLogin(forceRelogin: false);
        OpenBrowserRequested?.Invoke(this, EventArgs.Empty);
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

    private void OnSelectAllQueueClick(object? sender, RoutedEventArgs e)
    {
        if (QueueProjectList is null) return;
        QueueProjectList.SelectAll();
        _vm?.SetFilteredQueueRowsEnabled(true);
    }

    private void OnClearQueueSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (QueueProjectList is null) return;
        QueueProjectList.SelectedItems.Clear();
        _vm?.SetFilteredQueueRowsEnabled(false);
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

    private IReadOnlyList<string> GetSelectedProjectDirs() =>
        GetSelectedQueueRows()
            .Select(row => row.Item.ProjectDir)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private sealed record UploadTitlesDialogResult(
        string RawText,
        int EpisodeMin,
        int EpisodeMax,
        string MatchMode);

    private static async Task<UploadTitlesDialogResult?> ShowUploadTitlesDialogAsync(Window owner)
    {
        var dialog = new Window
        {
            Title = "上传短剧",
            Width = 560,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var titleBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 220,
            Watermark = "每行一个短剧名；按剧名+集数匹配时格式：剧名 80",
        };
        var matchEpisodeBox = new CheckBox
        {
            Content = "按剧名 + 集数匹配",
        };
        var minBox = new TextBox
        {
            Text = UploadTitleImportService.DefaultEpisodeMin.ToString(),
            Width = 80,
        };
        var maxBox = new TextBox
        {
            Text = UploadTitleImportService.DefaultEpisodeMax.ToString(),
            Width = 80,
        };
        var cancelButton = BuildDialogButton("取消", () => dialog.Close(null));
        var importButton = BuildDialogButton("导入", () =>
        {
            var min = ParseIntOrDefault(minBox.Text, UploadTitleImportService.DefaultEpisodeMin);
            var max = ParseIntOrDefault(maxBox.Text, UploadTitleImportService.DefaultEpisodeMax);
            var mode = matchEpisodeBox.IsChecked == true
                ? UploadTitleImportService.MatchModeTitleEpisode
                : UploadTitleImportService.MatchModeTitle;
            dialog.Close(new UploadTitlesDialogResult(titleBox.Text ?? "", min, max, mode));
        }, primary: true);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "批量输入短剧名称" },
                titleBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        matchEpisodeBox,
                        new TextBlock { Text = "最小集数", VerticalAlignment = VerticalAlignment.Center },
                        minBox,
                        new TextBlock { Text = "最大集数", VerticalAlignment = VerticalAlignment.Center },
                        maxBox,
                    },
                },
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

        vm.StatusMessage = "正在按标题导入短剧…";
        try
        {
            await vm.ImportUploadTitlesAsync(
                request.RawText,
                request.EpisodeMin,
                request.EpisodeMax,
                request.MatchMode,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"上传短剧导入失败：{ex.Message}";
            vm.AppendLog(vm.StatusMessage);
        }
    }

    private async void OnEditSelectedClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var dirs = GetSelectedProjectDirs();
        if (dirs.Count == 0)
        {
            vm.StatusMessage = "请先选中要编辑的剧集";
            return;
        }

        var options = vm.CreateCurrentQueueRunOptionsSnapshot();
        options.EnabledSteps = new List<string> { QueueStepRegistry.UploadSeries };
        options.ForceRerunCompletedSteps = true;
        options.UploadEntryMode = "edit";
        await StartQueueRunAsync(options, dirs);
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

        QueueProjectList.SelectedItems.Clear();
        var key = string.IsNullOrWhiteSpace(anchor.OriginalTitle) ? anchor.Title : anchor.OriginalTitle;
        var matched = 0;
        foreach (var row in vm.FilteredQueueProjectRows)
        {
            var rowKey = string.IsNullOrWhiteSpace(row.OriginalTitle) ? row.Title : row.OriginalTitle;
            if (!string.Equals(rowKey, key, StringComparison.OrdinalIgnoreCase)) continue;
            QueueProjectList.SelectedItems.Add(row);
            matched++;
        }

        vm.StatusMessage = matched > 0 ? $"已选中当前项目相关记录：{matched} 个" : "没有匹配到当前项目";
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

    private async Task StartQueueRunAsync(
        QueueRunOptions? optionsOverride = null,
        IReadOnlyCollection<string>? projectDirFilter = null)
    {
        var vm = _vm;
        if (vm is null) return;
        if (vm.IsCurrentWorkspaceQueueRunning())
        {
            vm.StatusMessage = "当前工作目录队列已在运行中";
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

        var host = CreateQueuePublishHost();
        var ct = vm.BeginQueueRun();
        RefreshQueueRunButtons();
        vm.StatusMessage = "TikTok 队列执行中…";
        try
        {
            var summary = await vm.RunQueueWorkerAsync(
                host,
                p => Dispatcher.UIThread.Post(() => vm.HandleQueueWorkerProgress(p)),
                items => Dispatcher.UIThread.Post(() => vm.ApplyPersistedQueueItems(items)),
                ct,
                optionsOverride,
                projectDirFilter);
            if (summary is not null && !summary.Stopped)
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
    {
        var vm = _vm;
        if (vm is null) return;
        if (vm.IsQueueRunning)
        {
            vm.StatusMessage = "已有工作目录队列在运行中";
            return;
        }

        var targets = vm.BuildAccountWorkspaceTargets();
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
                p => Dispatcher.UIThread.Post(() => vm.HandleQueueWorkerProgress(p)),
                (root, items) => Dispatcher.UIThread.Post(() =>
                {
                    if (string.Equals(Path.GetFullPath(root), Path.GetFullPath(vm.WorkspacePath), StringComparison.OrdinalIgnoreCase))
                        vm.ApplyPersistedQueueItems(items);
                }),
                ct);

            var success = summaries.Sum(s => s?.SuccessCount ?? 0);
            var failed = summaries.Sum(s => s?.FailedCount ?? 0);
            var stopped = summaries.Any(s => s?.Stopped == true);
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
            vm => PublishBrowserFocusRequested?.Invoke(vm));
    }

    private PublishScheduler RequireScheduler() =>
        _scheduler ??= new PublishScheduler(_automation, RequireBrowserProvider());

    private async Task<bool> EnsureAccountBrowserReadyAsync(TikTokAccountProfile account, CancellationToken ct)
    {
        var browser = await RequireBrowserProvider()
            .GetBrowserAsync(account, ct, EmbeddedBrowserAccessOptions.Background)
            .ConfigureAwait(false);
        return browser is not null;
    }

    private async Task<PublishResult> PublishQueueProjectAsync(
        TikTokAccountProfile account,
        QueueProjectItem project,
        FinalAction finalAction,
        QueueRunOptions options,
        Action<string> log,
        CancellationToken ct)
    {
        var browser = await RequireBrowserProvider()
            .GetBrowserAsync(account, ct, EmbeddedBrowserAccessOptions.Background)
            .ConfigureAwait(false);
        if (browser is null)
            return PublishResult.Fail("内置浏览器未就绪或未登录，请先在「浏览器」页完成登录");

        var item = QueuePublishHost.ToPublishItem(project);
        item.ForceEditUpload = string.Equals(options.UploadEntryMode, "edit", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(item.VideoPath))
            return PublishResult.Fail("项目没有可用视频");

        ApplyConfigDefaults(item);
        return await _automation.PublishAsync(account, item, browser, finalAction, log, ct).ConfigureAwait(false);
    }

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
                    .GetBrowserAsync(acctVm.Model, CancellationToken.None, EmbeddedBrowserAccessOptions.Interactive)
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
