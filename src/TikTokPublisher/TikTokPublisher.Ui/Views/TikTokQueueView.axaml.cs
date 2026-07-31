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
using Microsoft.Playwright;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Config;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.Services.TikTok;
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
    private readonly object _autoLoginLocksGate = new();
    private readonly Dictionary<string, SemaphoreSlim> _autoLoginLocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ManualExternalBrowserSession> _manualExternalBrowserSessions = new(StringComparer.Ordinal);
    private readonly Queue<ManualInterventionDialogRequest> _manualInterventionDialogs = new();
    private bool _manualInterventionDialogOpen;
    private QueueUiProgressSink? _queueProgressSink;
    private static readonly TimeSpan QueueAutoLoginTimeout = TimeSpan.FromMinutes(10);
    private static readonly double[] QueueTableDefaultColumnWidths =
    {
        48, 56, 104, 210, 210, 60, 128, 68, 68, 68, 76, 68, 0, 68, 68, 68, 68, 68, 180,
    };
    private static readonly double[] QueueTableMinColumnWidths =
    {
        42, 48, 72, 120, 120, 48, 92, 56, 56, 56, 62, 62, 0, 62, 62, 62, 62, 56, 120,
    };
    private readonly double[] _queueTableColumnWidths = QueueTableDefaultColumnWidths.ToArray();
    private readonly List<WeakReference<Grid>> _queueTableRowGrids = new();
    private int _queueResizeColumnIndex = -1;
    private double _queueResizeStartX;
    private double _queueResizeStartWidth;
    private readonly HashSet<string> _queueStopRequestedWorkspaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeQueueRunWorkspaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _copyrightProofPreparationRuns =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _uploadTitleImportActive;
    private string _uploadTitleImportWorkspaceRoot = "";
    private long _uploadTitleImportGeneration;

    public event EventHandler? OpenBrowserRequested;
    public event EventHandler? OpenLogsRequested;
    public event Action<AccountItemViewModel>? PublishBrowserFocusRequested;

    public TikTokQueueView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        DetachedFromVisualTree += async (_, _) => await CloseManualExternalBrowserSessionsAsync().ConfigureAwait(false);
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
        var anyRunning = _vm?.IsQueueRunning == true || _copyrightProofPreparationRuns.Count > 0;
        var currentRunning = IsStartQueueRunActiveForCurrentWorkspace() ||
                             _vm?.IsCurrentWorkspaceQueueRunning() == true;
        var currentProofPreparation = IsCopyrightProofPreparationActiveForCurrentWorkspace();
        var startBusy = currentRunning ||
                        currentProofPreparation ||
                        IsUploadTitleImportActiveForCurrentWorkspace();
        var currentRoot = NormalizeQueueWorkspaceRoot(_vm?.WorkspacePath ?? "");
        if (!startBusy && !string.IsNullOrWhiteSpace(currentRoot))
            _queueStopRequestedWorkspaces.Remove(currentRoot);
        var stopRequested = !string.IsNullOrWhiteSpace(currentRoot) &&
                            _queueStopRequestedWorkspaces.Contains(currentRoot);
        // 仅当前工作目录在跑时才禁用「执行勾选队列」；其他账号的队列不影响本工作目录启动。
        if (StartQueueButton is not null)
        {
            StartQueueButton.Content = stopRequested && startBusy
                ? "等待停止"
                : startBusy
                    ? "执行中"
                    : "开始生产";
            StartQueueButton.IsEnabled = !startBusy;
        }
        if (StartAllQueuesButton is not null) StartAllQueuesButton.IsEnabled = !anyRunning;
        if (StopQueueButton is not null)
        {
            StopQueueButton.Content = stopRequested && startBusy ? "停止中" : "停止";
            StopQueueButton.IsEnabled =
                (currentRunning || currentProofPreparation) && !stopRequested;
        }
    }

    private bool IsStartQueueRunActiveForCurrentWorkspace()
    {
        if (_vm is null)
            return false;

        var workspace = NormalizeQueueWorkspaceRoot(_vm.WorkspacePath ?? "");
        return !string.IsNullOrWhiteSpace(workspace) && _activeQueueRunWorkspaces.Contains(workspace);
    }

    private bool IsUploadTitleImportActiveForCurrentWorkspace()
    {
        if (!_uploadTitleImportActive || _vm is null)
            return false;

        var workspace = (_vm.WorkspacePath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(_uploadTitleImportWorkspaceRoot))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(workspace),
                _uploadTitleImportWorkspaceRoot,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(workspace, _uploadTitleImportWorkspaceRoot, StringComparison.OrdinalIgnoreCase);
        }
    }

    private bool IsCopyrightProofPreparationActiveForCurrentWorkspace()
    {
        if (_vm is null)
            return false;
        var root = NormalizeQueueWorkspaceRoot(_vm.WorkspacePath);
        return !string.IsNullOrWhiteSpace(root) &&
               _copyrightProofPreparationRuns.ContainsKey(root);
    }

    private CancellationToken? BeginCopyrightProofPreparation(string workspace)
    {
        var root = NormalizeQueueWorkspaceRoot(workspace);
        if (string.IsNullOrWhiteSpace(root) ||
            _copyrightProofPreparationRuns.ContainsKey(root))
        {
            return null;
        }

        var cts = new CancellationTokenSource();
        _copyrightProofPreparationRuns[root] = cts;
        _queueStopRequestedWorkspaces.Remove(root);
        RefreshQueueRunButtons();
        return cts.Token;
    }

    private void EndCopyrightProofPreparation(string workspace)
    {
        var root = NormalizeQueueWorkspaceRoot(workspace);
        if (_copyrightProofPreparationRuns.Remove(root, out var cts))
            cts.Dispose();
        _queueStopRequestedWorkspaces.Remove(root);
        RefreshQueueRunButtons();
    }

    private bool CancelCopyrightProofPreparation(string workspace)
    {
        var root = NormalizeQueueWorkspaceRoot(workspace);
        if (!_copyrightProofPreparationRuns.TryGetValue(root, out var cts))
            return false;
        cts.Cancel();
        return true;
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

        var openBrowserButton = BuildDialogButton("打开外部浏览器", () => _ = OpenSelectedAccountExternalBrowserAsync());
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
                    Foreground = new SolidColorBrush(Color.Parse("#FF9EAA")),
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
            }
        }

        _vm.SetWorkspacePath(path);
    }

    private async void OnMergeWorkspacesClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (Storage is null || vm is null || owner is null)
            return;

        var targetRoot = (vm.WorkspacePath ?? "").Trim();
        var targetAccount = vm.SelectedAccount?.Model;
        if (string.IsNullOrWhiteSpace(targetRoot) || !Directory.Exists(targetRoot))
        {
            vm.StatusMessage = "请先选择有效的当前工作目录";
            return;
        }
        if (targetAccount is null)
        {
            vm.StatusMessage = "请先选择当前工作目录所属账号";
            return;
        }
        if (vm.IsWorkspaceQueueBusy(targetRoot))
        {
            vm.StatusMessage = "当前工作目录队列正在运行或收尾，请停止后再合并";
            return;
        }

        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要合并的来源工作目录（可多选）",
            AllowMultiple = true,
        });
        var sourceRoots = folders
            .Select(folder => folder.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourceRoots.Length == 0)
            return;

        var busySource = sourceRoots.FirstOrDefault(vm.IsWorkspaceQueueBusy);
        if (!string.IsNullOrWhiteSpace(busySource))
        {
            await ShowMessageAsync(
                owner,
                "无法合并工作目录",
                $"来源工作目录队列正在运行或收尾，请先停止：{busySource}",
                warning: true);
            return;
        }

        WorkspaceMergeAnalysis analysis;
        try
        {
            vm.StatusMessage = "正在分析来源工作目录…";
            analysis = await Task.Run(() => WorkspaceMergeService.Analyze(targetRoot, sourceRoots));
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"分析工作目录失败：{ex.Message}";
            await ShowMessageAsync(owner, "分析工作目录失败", ex.Message, warning: true);
            return;
        }

        var sourceLines = analysis.Sources.Select(source =>
            $"• {source.WorkspaceRoot}\n  普通项目 {source.ActiveProjectCount} 个，归档项目 {source.ArchivedProjectCount} 个");
        var warningText = analysis.Warnings.Count == 0
            ? "未发现目录缺失。"
            : $"注意：{analysis.Warnings.Count} 条警告，合并完成后会在结果中列出。";
        var confirmed = await ConfirmAsync(
            owner,
            "确认合并工作目录",
            $"目标目录：{analysis.TargetWorkspaceRoot}\n" +
            $"目标账号：{targetAccount.DisplayName}\n\n" +
            string.Join("\n", sourceLines) +
            $"\n\n合计：普通项目 {analysis.ActiveProjectCount} 个，归档项目 {analysis.ArchivedProjectCount} 个。\n" +
            $"{warningText}\n\n" +
            "本次使用“复制并验证”方式：来源目录不会删除；导入项目绑定到当前账号，已有步骤和上传状态保留。" +
            "同名目录会自动改为不重复名称，归档回退路径会改到当前工作目录。\n\n确认继续？");
        if (!confirmed)
        {
            vm.StatusMessage = "已取消合并工作目录";
            return;
        }

        try
        {
            var progress = new Progress<WorkspaceMergeProgress>(value =>
            {
                vm.StatusMessage = $"合并工作目录 {value.Completed}/{value.Total}：{value.Message}";
                if (value.Completed == value.Total || value.Completed % 5 == 0)
                    vm.AppendLog(vm.StatusMessage);
            });
            var result = await Task.Run(() =>
                WorkspaceMergeService.Merge(
                    analysis,
                    targetAccount,
                    targetArchiveRootDir: vm.ArchivedProjects.ArchiveRootDir,
                    progress: progress,
                    cancellationToken: CancellationToken.None));

            vm.RefreshWorkspaceProjects(targetRoot, force: true);
            vm.ArchivedProjects.SetWorkspace(targetRoot);
            var resultText =
                $"工作目录合并完成。\n\n" +
                $"新增普通项目：{result.ImportedProjectCount}\n" +
                $"已存在并复用：{result.ReusedProjectCount}\n" +
                $"新增归档项目：{result.ImportedArchiveCount}\n" +
                $"已存在并复用归档：{result.ReusedArchiveCount}\n" +
                $"警告：{result.Warnings.Count}\n" +
                (string.IsNullOrWhiteSpace(result.BackupDatabasePath)
                    ? ""
                    : $"\n合并前数据库备份：{result.BackupDatabasePath}\n") +
                (result.Warnings.Count == 0
                    ? ""
                    : "\n" + string.Join("\n", result.Warnings.Take(20)));
            vm.StatusMessage =
                $"合并完成：普通项目 {result.ImportedProjectCount + result.ReusedProjectCount} 个，" +
                $"归档项目 {result.ImportedArchiveCount + result.ReusedArchiveCount} 个";
            vm.AppendLog(vm.StatusMessage);
            await ShowMessageAsync(owner, "合并工作目录", resultText, warning: result.Warnings.Count > 0);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"合并工作目录失败：{ex.Message}";
            vm.AppendLog(vm.StatusMessage);
            await ShowMessageAsync(
                owner,
                "合并工作目录失败",
                $"{ex.Message}\n\n来源目录未删除；本次新复制的目标目录已尽力回滚。",
                warning: true);
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

    private async void OnOpenBrowserClick(object? sender, RoutedEventArgs e)
    {
        await OpenSelectedAccountExternalBrowserAsync();
    }

    private async Task OpenSelectedAccountExternalBrowserAsync()
    {
        var vm = _vm;
        if (vm is null)
            return;

        var accountVm = ResolveManualExternalBrowserAccount(vm);
        if (accountVm is null)
        {
            vm.StatusMessage = "请先选择账号";
            return;
        }

        var account = accountVm.Model;
        var url = EmbeddedBrowserLoginHelper.ResolveHomeUrl(account);
        var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);

        try
        {
            await CloseManualExternalBrowserSessionsAsync().ConfigureAwait(true);
            var (pw, browser, _) = await EmbeddedBrowserAutomationBridge
                .LaunchPageAsync(account, url, authPath, headless: false, vm.AppendLog, CancellationToken.None)
                .ConfigureAwait(true);
            _manualExternalBrowserSessions[account.Id] = new ManualExternalBrowserSession(pw, browser);

            vm.StatusMessage = $"[{accountVm.DisplayName}] 已打开账号专属外部浏览器：{url}";
            vm.AppendLog(vm.StatusMessage);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"打开外部浏览器失败：{ex.Message}";
            vm.AppendLog(vm.StatusMessage);
        }
    }

    private AccountItemViewModel? ResolveManualExternalBrowserAccount(MainViewModel vm)
    {
        var workspace = (vm.WorkspacePath ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var boundId = WorkspaceBindingService.ResolveAccountProfileId(workspace);
            if (!string.IsNullOrWhiteSpace(boundId))
            {
                var bound = vm.FindAccount(boundId);
                if (bound is not null)
                    return bound;
            }
        }

        return vm.SelectedAccount;
    }

    private async Task CloseManualExternalBrowserSessionsAsync()
    {
        if (_manualExternalBrowserSessions.Count == 0)
            return;

        var sessions = _manualExternalBrowserSessions.Values.ToArray();
        _manualExternalBrowserSessions.Clear();
        foreach (var session in sessions)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch { /* Best-effort cleanup for manually opened browsers. */ }
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
        _vm?.SetFilteredQueueRowsEnabled(true);
    }

    private long BeginUploadTitleImport(string workspaceRoot)
    {
        var generation = unchecked(++_uploadTitleImportGeneration);
        _uploadTitleImportWorkspaceRoot = NormalizeQueueWorkspaceRoot(workspaceRoot);
        _uploadTitleImportActive = true;
        RefreshQueueRunButtons();
        return generation;
    }

    private void CompleteUploadTitleImport(long generation)
    {
        // 导入可在启动长队列前主动释放；旧操作的 finally 不得清掉随后开始的新导入状态。
        if (_uploadTitleImportGeneration != generation || !_uploadTitleImportActive)
            return;

        _uploadTitleImportActive = false;
        _uploadTitleImportWorkspaceRoot = "";
        RefreshQueueRunButtons();
    }

    private void OnClearQueueSelectionClick(object? sender, RoutedEventArgs e)
    {
        _vm?.SetFilteredQueueRowsEnabled(false);
    }

    private void OnSelectCompletedQueueClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;

        vm.SetFilteredCompletedQueueRowsEnabled();
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

    private async void OnMoveSelectedProjectsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var vm = _vm;
        if (vm is null) return;

        var rows = GetCheckedOrSelectedQueueRows()
            .Distinct()
            .ToArray();
        if (rows.Length == 0)
        {
            vm.StatusMessage = "请先勾选或选中要移动的项目";
            return;
        }

        if (vm.IsCurrentWorkspaceQueueRunning())
        {
            vm.StatusMessage = "当前工作目录队列正在运行，请停止后再移动项目";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            vm.StatusMessage = "无法打开移动项目弹窗";
            return;
        }

        var targetAccount = await ShowMoveProjectsDialogAsync(owner, vm, rows.Length);
        if (targetAccount is null)
            return;

        if (!await ConfirmAsync(
                owner,
                "移动项目到账号",
                $"将 {rows.Length} 个项目移动到「{targetAccount.DisplayName}」的工作目录，并重置 TikTok 上传状态。是否继续？"))
        {
            return;
        }

        try
        {
            await vm.MoveQueueProjectsToAccountAsync(rows, targetAccount);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"移动项目失败：{ex.Message}";
            await ShowMessageAsync(owner, "移动项目失败", ex.Message, warning: true);
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

    private sealed record LocalDramaImportDialogResult(
        IReadOnlyList<string> ProjectDirs,
        bool AutoRun);

    private sealed record MoveTargetAccountOption(
        AccountItemViewModel Account,
        string Workspace)
    {
        public override string ToString() => $"{Account.DisplayName} · {Workspace}";
    }

    private static async Task<AccountItemViewModel?> ShowMoveProjectsDialogAsync(
        Window owner,
        MainViewModel vm,
        int projectCount)
    {
        var options = vm.Accounts
            .Select(account => new MoveTargetAccountOption(account, account.Model.ResolveWorkspacePath()))
            .Where(option => !string.IsNullOrWhiteSpace(option.Workspace))
            .OrderBy(option => option.Account.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (options.Count == 0)
        {
            await ShowMessageAsync(owner, "移动项目到账号", "没有配置有效工作目录的账号。", warning: true);
            return null;
        }

        var dialog = new Window
        {
            Title = "移动项目到账号",
            Width = 620,
            Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var targetCombo = new ComboBox
        {
            ItemsSource = options,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var cancelButton = BuildDialogButton("取消", () => dialog.Close(null));
        var moveButton = BuildDialogButton("移动", () =>
        {
            if (targetCombo.SelectedItem is MoveTargetAccountOption option)
                dialog.Close(option.Account);
        }, primary: true);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"选择目标账号。将移动 {projectCount} 个项目目录和 workflow 目录，并同步队列账号绑定。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "目标账号",
                    FontWeight = FontWeight.SemiBold,
                },
                targetCombo,
                new TextBlock
                {
                    Text = "移动后 TikTok 上传状态会重置，避免沿用原账号的草稿或已上传记录；本地处理步骤会保留。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#B8C8D8")),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, moveButton },
                },
            },
        };

        return await dialog.ShowDialog<AccountItemViewModel?>(owner);
    }

    private static async Task<UploadTitlesDialogResult?> ShowUploadTitlesDialogAsync(Window owner)
    {
        var dialog = new Window
        {
            Title = "创建发布单",
            Width = 560,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var titleBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 180,
            Height = 220,
            MaxHeight = 220,
            Watermark = "例如：\n她的豪门，我的刑场\n岁岁冥婚鬼夫夜夜来\n\n剧名 + 集数匹配：\n凤月无凭 43",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(
            titleBox,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
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

    private static async Task<LocalDramaImportDialogResult?> ShowLocalDramaImportDialogAsync(
        Window owner,
        IReadOnlyList<LocalManualDramaImportPreview> candidates)
    {
        var dialog = new Window
        {
            Title = "导入本地剧集 - TikTok",
            Width = 720,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var listBox = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            MinHeight = 300,
            MaxHeight = 340,
        };

        foreach (var preview in candidates)
        {
            var item = new ListBoxItem
            {
                Content = FormatLocalDramaImportCandidate(preview),
                Tag = preview,
                IsSelected = !preview.MetadataExists,
            };
            listBox.Items.Add(item);
        }

        var autoRunCheck = new CheckBox
        {
            Content = "导入后自动按当前启用步骤执行",
            IsChecked = false,
        };

        var selectNewButton = BuildDialogButton("选择未导入", () =>
        {
            foreach (var item in listBox.Items.OfType<ListBoxItem>())
                item.IsSelected = item.Tag is LocalManualDramaImportPreview preview && !preview.MetadataExists;
        });
        var selectAllButton = BuildDialogButton("全选", () =>
        {
            foreach (var item in listBox.Items.OfType<ListBoxItem>())
                item.IsSelected = true;
        });
        var clearButton = BuildDialogButton("取消全选", () =>
        {
            foreach (var item in listBox.Items.OfType<ListBoxItem>())
                item.IsSelected = false;
        });
        var cancelButton = BuildDialogButton("取消", () => dialog.Close(null));
        var importButton = BuildDialogButton("确定", () =>
        {
            var selected = listBox.SelectedItems?
                .OfType<ListBoxItem>()
                .Select(item => item.Tag is LocalManualDramaImportPreview preview ? preview.ProjectDir : "")
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
            dialog.Close(new LocalDramaImportDialogResult(selected, autoRunCheck.IsChecked == true));
        }, primary: true);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "从当前工作目录中批量选择已由下载器下载好的短剧文件夹。导入后会生成 shortdrama-project.json 并加入 TikTok 上传队列，本地导入项目会自动跳过下载剧集步骤。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = $"发现 {candidates.Count} 个可导入目录。默认选中未导入项目。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { selectNewButton, selectAllButton, clearButton },
                },
                listBox,
                autoRunCheck,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, importButton },
                },
            },
        };

        return await dialog.ShowDialog<LocalDramaImportDialogResult?>(owner);
    }

    private static string FormatLocalDramaImportCandidate(LocalManualDramaImportPreview preview)
    {
        var parts = new List<string>
        {
            preview.DisplayName,
            $"{preview.EpisodeCount} 集",
            string.IsNullOrWhiteSpace(preview.IntroPath) ? "缺简介" : "有简介",
            string.IsNullOrWhiteSpace(preview.PosterPath) ? "缺海报" : "有海报",
        };
        if (preview.MetadataExists)
            parts.Add("已导入");
        return string.Join(" | ", parts);
    }

    private static async Task ShowMessageAsync(Window owner, string title, string message, bool warning = false)
    {
        var lineCount = Math.Max(1, message.Split('\n').Length);
        var dialogHeight = Math.Clamp(180 + lineCount * 22, 260, 560);
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = dialogHeight,
            MinWidth = 460,
            MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var okButton = BuildDialogButton("确定", () => dialog.Close(), primary: !warning);
        var grid = new Grid
        {
            Margin = new Thickness(16),
        };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var messageViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            },
        };
        Grid.SetRow(messageViewer, 0);
        grid.Children.Add(messageViewer);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { okButton },
        };
        Grid.SetRow(buttons, 1);
        grid.Children.Add(buttons);

        dialog.Content = grid;
        await dialog.ShowDialog<bool?>(owner);
    }

    private static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        var lineCount = Math.Max(1, message.Split('\n').Length);
        var dialogHeight = Math.Clamp(180 + lineCount * 22, 260, 560);
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = dialogHeight,
            MinWidth = 460,
            MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var cancelButton = BuildDialogButton("取消", () => dialog.Close(false));
        var continueButton = BuildDialogButton("继续", () => dialog.Close(true), primary: true);
        var grid = new Grid
        {
            Margin = new Thickness(16),
        };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var messageViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            },
        };
        Grid.SetRow(messageViewer, 0);
        grid.Children.Add(messageViewer);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
            Children =
            {
                cancelButton,
                continueButton,
            },
        };
        Grid.SetRow(buttons, 1);
        grid.Children.Add(buttons);

        dialog.Content = grid;

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
        if (vm is null) return;
        if (string.IsNullOrWhiteSpace(vm.WorkspacePath))
        {
            vm.StatusMessage = "请先选择工作目录";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        IReadOnlyList<LocalManualDramaImportPreview> candidates;
        try
        {
            vm.StatusMessage = "正在扫描工作目录中的本地剧集…";
            candidates = await vm.ListLocalManualDramaCandidatesAsync();
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"扫描本地剧集失败：{ex.Message}";
            await ShowMessageAsync(owner, "扫描本地剧集失败", ex.Message, warning: true);
            return;
        }

        if (candidates.Count == 0)
        {
            await ShowMessageAsync(owner, "导入本地剧集", "当前工作目录下未发现包含视频文件的本地剧集文件夹。");
            return;
        }

        var request = await ShowLocalDramaImportDialogAsync(owner, candidates);
        if (request is null)
            return;
        if (request.ProjectDirs.Count == 0)
        {
            await ShowMessageAsync(owner, "导入本地剧集", "请至少选择一个本地剧集文件夹。", warning: true);
            return;
        }

        LocalManualDramaBatchImportResult result;
        try
        {
            result = await vm.ImportLocalManualDramasAsync(request.ProjectDirs);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"导入本地剧集失败：{ex.Message}";
            await ShowMessageAsync(owner, "导入本地剧集失败", ex.Message, warning: true);
            return;
        }

        if (result.Failures.Count > 0)
        {
            var lines = string.Join('\n', result.Failures.Take(30).Select(item => $"· {item}"));
            var extra = result.Failures.Count > 30 ? $"\n… 等共 {result.Failures.Count} 条" : "";
            await ShowMessageAsync(
                owner,
                "导入本地剧集 · 部分失败",
                $"{result.SummaryText}\n\n{lines}{extra}",
                warning: true);
        }
        else if (result.SuccessCount == 0)
        {
            await ShowMessageAsync(owner, "导入本地剧集", "没有可导入的本地剧集。");
            return;
        }

        if (request.AutoRun && result.SuccessCount > 0 && !vm.IsCurrentWorkspaceQueueRunning())
        {
            await StartQueueRunAsync(projectDirFilter: BuildLocalManualImportProjectFilter(result));
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
        if (_uploadTitleImportActive)
        {
            vm.StatusMessage = "已有上传短剧导入正在处理，请等待完成";
            return;
        }

        // 弹窗和导入都会跨越 await；在任何 await 前固定工作目录、账号和运行配置。
        var importTarget = vm.CaptureCurrentWorkspaceQueueTarget();
        if (importTarget is null)
        {
            vm.StatusMessage = "请先选择工作目录";
            return;
        }
        var runOptions = vm.CreateCurrentQueueRunOptionsSnapshot();

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var importGeneration = BeginUploadTitleImport(importTarget.WorkspaceRoot);
        try
        {
            var request = await ShowUploadTitlesDialogAsync(owner);
            if (request is null) return;
            if (string.IsNullOrWhiteSpace(request.RawText))
            {
                await ShowMessageAsync(owner, "缺少剧名", "请输入至少一个短剧名称。", warning: true);
                return;
            }

            vm.StatusMessage = "正在按标题导入短剧…";
            UploadTitleImportOutcome? importOutcome;
            try
            {
                importOutcome = await vm.ImportUploadTitlesAsync(
                    importTarget,
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

            if (importOutcome is null)
                return;

            var result = importOutcome.ImportResult;
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

            UploadTitleAutoRunPreparation preparation;
            try
            {
                preparation = await vm.PrepareUploadTitleImportAutoRunAsync(importOutcome);
            }
            catch (Exception ex)
            {
                vm.StatusMessage = $"短剧已导入，但自动执行未启动：{ex.Message}";
                vm.AppendLog(vm.StatusMessage);
                await ShowMessageAsync(owner, "自动执行未启动", vm.StatusMessage, warning: true);
                return;
            }

            if (preparation.AppendedCount > 0)
            {
                vm.StatusMessage = $"已将 {preparation.AppendedCount} 个导入项目追加到原工作目录的运行队列末尾。";
                vm.AppendLog(vm.StatusMessage);
                return;
            }

            if (preparation.RunTarget is not null)
            {
                var projectCount = preparation.RunTarget.ProjectDirFilter?.Count ?? 0;
                vm.AppendLog($"上传短剧导入完成，自动执行原工作目录队列：{projectCount} 个项目。");
                CompleteUploadTitleImport(importGeneration);
                var started = await StartQueueRunAsync(
                    runOptions,
                    preparation.RunTarget.ProjectDirFilter,
                    targetOverride: preparation.RunTarget);
                if (!started)
                {
                    var appended = vm.TryAppendUploadTitleImportToRunningQueue(importOutcome);
                    if (appended > 0)
                    {
                        vm.StatusMessage = $"目标队列已被其它操作启动，已将 {appended} 个导入项目追加到队列末尾。";
                        vm.AppendLog(vm.StatusMessage);
                    }
                }
            }
        }
        finally
        {
            CompleteUploadTitleImport(importGeneration);
        }
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

    private async void OnCompleteCopyrightProofClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var vm = _vm;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (vm is null || owner is null)
            return;

        var workspace = (vm.WorkspacePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            vm.StatusMessage = "请先选择有效的 TikTok 工作目录";
            return;
        }

        var proofAccount = vm.SelectedAccount?.Model;
        if (proofAccount is null)
        {
            vm.StatusMessage = "请先选择要补全版权证明的账号";
            return;
        }

        IReadOnlyList<ArchivedProjectItem> archivedProjects;
        IReadOnlyList<TikTokExecutionProjectSnapshot> deletedHistoryProjects;
        try
        {
            vm.StatusMessage = "正在读取当前队列、已归档项目和已删除项目历史…";
            (archivedProjects, deletedHistoryProjects) = await Task.Run(() =>
            {
                var archives = TikTokArchivedProjectService.List(
                    workspace,
                    proofAccount.ResolveArchiveRootPath(workspace));
                var history = LoadDeletedCopyrightProofHistory(
                    workspace,
                    proofAccount,
                    vm.ArchivedProjects.ArchiveRootDir);
                return (archives, history);
            });
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"读取版权项目记录失败：{ex.Message}";
            return;
        }

        var queueProjects = vm.QueueProjectRows.Select(row => row.Item).ToArray();
        IReadOnlyList<CopyrightProofProjectMatch> Match(string input) =>
            CopyrightProofProjectMatcher.MatchByNewTitleExact(
                CopyrightProofProjectMatcher.ParseNewTitles(input),
                queueProjects,
                archivedProjects,
                deletedHistoryProjects);

        var dialogResult = await CopyrightProofBatchDialog.ShowAsync(owner, Match);
        if (dialogResult is null || dialogResult.SelectedMatches.Count == 0)
        {
            vm.StatusMessage = "已取消补全版权证明";
            return;
        }

        await ExecuteCopyrightProofMatchesAsync(
            owner,
            vm,
            workspace,
            proofAccount,
            dialogResult.SelectedMatches);
    }

    private async void OnManualDeletedCopyrightProofClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var vm = _vm;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (vm is null || owner is null)
            return;

        var workspace = (vm.WorkspacePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            vm.StatusMessage = "请先选择有效的 TikTok 工作目录";
            return;
        }

        var proofAccount = vm.SelectedAccount?.Model;
        if (proofAccount is null)
        {
            vm.StatusMessage = "请先选择要补全版权证明的账号";
            return;
        }

        var dialogResult = await ManualDeletedCopyrightProofDialog.ShowAsync(owner);
        if (dialogResult is null || dialogResult.Entries.Count == 0)
        {
            vm.StatusMessage = "已取消手动补全已删除剧集证明";
            return;
        }

        IReadOnlyList<ArchivedProjectItem> archivedProjects;
        IReadOnlyList<QueueProjectItem> queueProjects;
        try
        {
            vm.StatusMessage = dialogResult.Mode ==
                               ManualDeletedCopyrightProofInputMode.KnownOriginalTitle
                ? "正在校验手动填写的新剧名和原剧名…"
                : "正在校验批量填写的新剧名…";
            queueProjects = vm.QueueProjectRows.Select(row => row.Item).ToArray();
            archivedProjects = await Task.Run(() => TikTokArchivedProjectService.List(
                workspace,
                proofAccount.ResolveArchiveRootPath(workspace)));
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"校验手动补全项目失败：{ex.Message}";
            return;
        }

        var matches = ManualDeletedCopyrightProofService.BuildMatches(
            dialogResult.Entries,
            workspace,
            proofAccount,
            queueProjects,
            archivedProjects);
        var validatedMatches = new List<CopyrightProofProjectMatch>();
        var validationFailures = matches
            .Where(match => !match.CanExecute)
            .Select(match => $"「{match.NewTitle}」存在同名冲突")
            .ToList();
        foreach (var match in matches.Where(match => match.CanExecute))
        {
            if (match.Location != CopyrightProofProjectLocation.DeletedHistory)
            {
                validatedMatches.Add(match);
                continue;
            }

            var originalTitle = (match.HistorySnapshot?.Item.OriginalTitle ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(originalTitle))
            {
                validatedMatches.Add(match);
                continue;
            }

            try
            {
                vm.StatusMessage = $"正在验证原剧资源：{originalTitle}";
                var lookup = await UploadTitleImportService.FindExactDramaAsync(
                    originalTitle,
                    0,
                    CancellationToken.None);
                if (lookup.Item is null)
                {
                    validationFailures.Add(
                        $"「{match.NewTitle}」：找不到唯一原剧「{originalTitle}」（{lookup.Reason}）");
                    continue;
                }

                match.HistorySnapshot!.Item.EpisodeCount = Math.Max(0, lookup.Item.EpisodeTotal);
                validatedMatches.Add(match);
            }
            catch (Exception ex)
            {
                validationFailures.Add($"「{match.NewTitle}」：验证原剧「{originalTitle}」失败（{ex.Message}）");
            }
        }

        if (validationFailures.Count > 0)
        {
            await ShowMessageAsync(
                owner,
                validatedMatches.Count > 0
                    ? "部分项目将跳过"
                    : "无法开始手动补全",
                string.Join(Environment.NewLine, validationFailures),
                warning: true);
        }
        if (validatedMatches.Count == 0)
        {
            vm.StatusMessage = "没有通过原剧资源校验的手动补全项目";
            return;
        }

        await ExecuteCopyrightProofMatchesAsync(
            owner,
            vm,
            workspace,
            proofAccount,
            validatedMatches,
            manualDeletedMode: dialogResult.Mode);
    }

    private async Task ExecuteCopyrightProofMatchesAsync(
        Window owner,
        MainViewModel vm,
        string workspace,
        TikTokAccountProfile proofAccount,
        IReadOnlyList<CopyrightProofProjectMatch> selectedMatches,
        ManualDeletedCopyrightProofInputMode? manualDeletedMode = null)
    {
        var archivedTargets = selectedMatches
            .Where(match => match.Location == CopyrightProofProjectLocation.Archived)
            .ToArray();
        if (archivedTargets.Length > 0)
        {
            var names = string.Join(
                Environment.NewLine,
                archivedTargets.Select(match => $"• {match.NewTitle}"));
            var confirmed = await ConfirmAsync(
                owner,
                "确认回退归档项目",
                $"以下 {archivedTargets.Length} 个项目已归档，将自动回退到原工作区并继续补全版权证明：" +
                $"{Environment.NewLine}{Environment.NewLine}{names}" +
                $"{Environment.NewLine}{Environment.NewLine}确认继续吗？");
            if (!confirmed)
            {
                vm.StatusMessage = "已取消回退归档和补全版权证明";
                return;
            }
        }

        var deletedTargets = selectedMatches
            .Where(match => match.Location == CopyrightProofProjectLocation.DeletedHistory)
            .ToArray();
        if (deletedTargets.Length > 0)
        {
            var names = string.Join(
                Environment.NewLine,
                deletedTargets.Select(match =>
                {
                    var item = match.HistorySnapshot?.Item;
                    var episodeText = item?.EpisodeCount > 0 ? $"，{item.EpisodeCount} 集" : string.Empty;
                    var originalTitle = (item?.OriginalTitle ?? string.Empty).Trim();
                    return string.IsNullOrWhiteSpace(originalTitle)
                        ? $"• {match.NewTitle}（从 TikTok 已发布项目恢复视频）"
                        : $"• {match.NewTitle}（原剧：{originalTitle}{episodeText}）";
                }));
            var recoverySource = manualDeletedMode switch
            {
                ManualDeletedCopyrightProofInputMode.KnownOriginalTitle =>
                    "将根据你填写的新剧名和原剧名恢复项目，并优先使用原片源，",
                ManualDeletedCopyrightProofInputMode.UnknownOriginalTitle =>
                    "将根据你批量填写的新剧名恢复项目，并从当前账号的 TikTok 已发布项目下载必要视频，",
                _ => "将根据历史记录重新建立项目，",
            };
            var confirmed = await ConfirmAsync(
                owner,
                "确认重建已删除项目",
                $"以下 {deletedTargets.Length} 个项目的本地目录已被删除，{recoverySource}" +
                "并在生成证明材料时按需恢复所需视频：" +
                $"{Environment.NewLine}{Environment.NewLine}{names}" +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "本次只会生成证明材料并编辑 TikTok 版权证明页面，不会重新上传剧集。确认继续吗？");
            if (!confirmed)
            {
                vm.StatusMessage = "已取消重建已删除项目和补全版权证明";
                return;
            }
        }

        var recoveryToken = BeginCopyrightProofPreparation(workspace);
        if (recoveryToken is null)
        {
            vm.StatusMessage = "当前工作目录已有补全版权证明任务正在执行";
            return;
        }

        var ct = recoveryToken.Value;
        vm.StatusMessage = "补全版权证明执行中…";
        try
        {
            var selectedTitles = selectedMatches
                .Select(match => match.NewTitle)
                .ToHashSet(StringComparer.Ordinal);
            var restoreFailures = new List<string>();
            var restoredCount = 0;
            var recoveredCount = 0;
            var preparationLogStartedAt = DateTime.Now;

            Action<string> CreatePreparationLog(string newTitle)
            {
                var title = (newTitle ?? string.Empty).Trim();
                return message =>
                {
                    var text = (message ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(text))
                        return;

                    vm.AppendLog($"[{title}] {text}");
                };
            }

            foreach (var match in selectedMatches
                         .Where(match => match.Location == CopyrightProofProjectLocation.Archived))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    vm.StatusMessage = $"正在回退归档项目：{match.NewTitle}";
                    await Task.Run(() => TikTokArchivedProjectService.Restore(
                        workspace,
                        match.ArchivedProject!.ArchiveProjectDir,
                        proofAccount.ResolveArchiveRootPath(workspace)), ct);
                    restoredCount++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    restoreFailures.Add($"{match.NewTitle}：{ex.Message}");
                    selectedTitles.Remove(match.NewTitle);
                }
            }

            var proofSettings = ClientSettingsStore.Load();
            foreach (var match in deletedTargets)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var historySnapshot = match.HistorySnapshot!;
                    var originalTitle = (historySnapshot.Item.OriginalTitle ?? string.Empty).Trim();
                    DeletedCopyrightProofProjectRecoveryResult recovery;
                    if (string.IsNullOrWhiteSpace(originalTitle))
                    {
                        var preparationLog = CreatePreparationLog(match.NewTitle);
                        var requiredEpisodes =
                            DeletedCopyrightProofPublishedVideoRecoveryService.ResolveRequiredEpisodeCount(
                                proofSettings,
                                proofAccount);
                        requiredEpisodes = Math.Max(1, requiredEpisodes);
                        vm.StatusMessage =
                            $"正在从 TikTok 已发布项目恢复视频：{match.NewTitle}（需要 {requiredEpisodes} 集）";
                        preparationLog(
                            $"准备从 TikTok 已发布项目恢复视频：计划获取前 {requiredEpisodes} 集。");
                        var ready = await EnsureAccountBrowserReadyAsync(
                            proofAccount,
                            preparationLog,
                            ct);
                        if (!ready.Ok)
                        {
                            preparationLog($"浏览器准备失败：{ready.Message}");
                            restoreFailures.Add($"{match.NewTitle}：{ready.Message}");
                            selectedTitles.Remove(match.NewTitle);
                            continue;
                        }

                        IEmbeddedBrowser? browser = null;
                        if (!UsesPlaywrightUploadBrowser(proofAccount))
                            browser = _browserHost?.TryGetHost(proofAccount.Id);
                        var download =
                            await TikTokPublishedSeriesVideoDownloadService.DownloadAsync(
                                proofAccount,
                                browser,
                                match.NewTitle,
                                workspace,
                                requiredEpisodes,
                                preparationLog,
                                ct);
                        if (!download.Ok)
                        {
                            preparationLog($"平台视频恢复失败：{download.Message}");
                            restoreFailures.Add($"{match.NewTitle}：{download.Message}");
                            selectedTitles.Remove(match.NewTitle);
                            continue;
                        }
                        preparationLog(download.Message);

                        recovery = DeletedCopyrightProofPublishedVideoRecoveryService.Recover(
                            workspace,
                            historySnapshot,
                            new TikTokPublishedVideoRecoverySource(
                                download.SeriesId,
                                download.DetailUrl,
                                download.StagingDirectory,
                                download.PlatformEpisodeCount,
                                download.DownloadedEpisodeCount),
                            proofAccount,
                            preparationLog);
                    }
                    else
                    {
                        vm.StatusMessage = $"正在重建已删除项目：{match.NewTitle}";
                        recovery = await DeletedCopyrightProofProjectRecoveryService.RecoverAsync(
                            workspace,
                            historySnapshot,
                            proofSettings,
                            proofAccount,
                            vm.AppendLog,
                            ct);
                    }

                    if (!recovery.Ok)
                    {
                        restoreFailures.Add($"{match.NewTitle}：{recovery.Message}");
                        selectedTitles.Remove(match.NewTitle);
                        continue;
                    }

                    recoveredCount++;
                    try
                    {
                        TikTokExecutionHistoryService.PersistDeletionSnapshot(
                            workspace,
                            recovery.Project ?? historySnapshot.Item,
                            proofAccount);
                    }
                    catch (Exception historyEx)
                    {
                        vm.AppendLog(
                            $"已重建「{match.NewTitle}」，但保存恢复历史失败：{historyEx.Message}；" +
                            "不影响本次继续补全版权证明。");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    restoreFailures.Add($"{match.NewTitle}：{ex.Message}");
                    selectedTitles.Remove(match.NewTitle);
                }
            }

            ct.ThrowIfCancellationRequested();
            var refreshedProjects = await Task.Run(
                () => WorkspaceQueueService.ScanProjects(workspace),
                ct);
            var repairedInterruptedRecovery = false;
            foreach (var project in refreshedProjects.Where(item =>
                         !item.Archived &&
                         selectedTitles.Contains((item.NewTitle ?? string.Empty).Trim())))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (!DeletedCopyrightProofWorkflowRecoveryService.RepairExistingProject(
                            project.ProjectDir,
                            project.NewTitle,
                            vm.AppendLog))
                    {
                        continue;
                    }

                    repairedInterruptedRecovery = true;
                    vm.AppendLog($"已自动修复上次中断留下的版权项目：{project.NewTitle}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    restoreFailures.Add($"{project.NewTitle}：修复中断项目失败（{ex.Message}）");
                    selectedTitles.Remove(project.NewTitle);
                }
            }
            if (repairedInterruptedRecovery)
            {
                refreshedProjects = await Task.Run(
                    () => WorkspaceQueueService.ScanProjects(workspace),
                    ct);
            }

            var matchedProjects = refreshedProjects
                .Where(item => !item.Archived &&
                               selectedTitles.Contains((item.NewTitle ?? string.Empty).Trim()))
                .GroupBy(item => item.NewTitle.Trim(), StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single())
                .ToArray();

            vm.StatusMessage = "正在检查匹配项目的已有证明材料…";
            var currentProofMaterialProjects = await Task.Run(() =>
                matchedProjects
                    .Where(item => TikTokProofMaterialService
                        .HasReusableProofMaterialForCopyrightCompletion(
                            item,
                            proofSettings,
                            proofAccount))
                    .Select(item => Path.GetFullPath(item.ProjectDir))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase), ct);
            var preparation = CopyrightProofQueuePreparationService.Prepare(
                refreshedProjects,
                matchedProjects,
                currentProofMaterialProjects);

            var missingAfterRestore = selectedTitles
                .Except(matchedProjects.Select(item => item.NewTitle), StringComparer.Ordinal)
                .ToArray();
            if (matchedProjects.Length == 0)
            {
                vm.RefreshWorkspaceProjects(workspace, force: true);
                var detail = restoreFailures.Concat(
                    missingAfterRestore.Select(title => $"{title}：恢复后未找到队列项目"));
                await ShowMessageAsync(
                    owner,
                    "无法开始补全版权证明",
                    string.Join(Environment.NewLine, detail),
                    warning: true);
                return;
            }

            ct.ThrowIfCancellationRequested();
            var persistedOptions = WorkspaceQueueService.LoadRunOptions(workspace);
            WorkspaceQueueService.SaveRunOptions(workspace, refreshedProjects, persistedOptions);
            await vm.ApplyPreparedWorkspaceQueueSnapshotAsync(
                workspace,
                refreshedProjects,
                persistedOptions);

            var options = vm.CreateCurrentQueueRunOptionsSnapshot();
            options.ConfigureForCopyrightProofCompletion();
            var executionProjectDirs = matchedProjects
                .Select(item => Path.GetFullPath(item.ProjectDir))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var executionTarget = vm.CaptureCurrentWorkspaceQueueTarget()! with
            {
                ProjectDirFilter = executionProjectDirs,
                PreferPersistedQueueSnapshot = true,
            };

            vm.AppendLog(
                $"补全版权证明：匹配 {selectedMatches.Count} 个，" +
                $"回退归档 {restoredCount} 个，重建已删除项目 {recoveredCount} 个，" +
                $"准备执行 {matchedProjects.Length} 个；" +
                $"复用已有证明材料 {preparation.ReusedProofMaterialCount} 个，" +
                $"需要生成 {preparation.PendingProofMaterialCount} 个。");
            foreach (var failure in restoreFailures)
                vm.AppendLog($"补全版权证明回退失败：{failure}");
            foreach (var title in missingAfterRestore)
                vm.AppendLog($"补全版权证明跳过：恢复后未找到唯一的新剧名项目「{title}」");

            ct.ThrowIfCancellationRequested();
            await StartQueueRunAsync(
                options,
                executionProjectDirs,
                confirmForceRerun: false,
                targetOverride: executionTarget,
                preserveProjectLogsSince: preparationLogStartedAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            vm.StatusMessage = "补全版权证明已停止";
            vm.AppendLog(vm.StatusMessage);
        }
        finally
        {
            EndCopyrightProofPreparation(workspace);
        }
    }

    private static IReadOnlyList<TikTokExecutionProjectSnapshot> LoadDeletedCopyrightProofHistory(
        string workspaceRoot,
        TikTokAccountProfile account,
        string? archiveRootDir)
    {
        var workspace = Path.GetFullPath(workspaceRoot);
        var accountKeys = new[]
            {
                account.Id,
                account.Name,
                account.DisplayName,
                account.ResolveTikTokAccountName(),
                account.TiktokLoginEmail,
                account.TiktokLastLoginEmail,
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var persistedHistory = TikTokExecutionHistoryService.LoadProjectSnapshots();
        var discoveredHistory = CopyrightProofLocalHistoryDiscoveryService.Discover(
            workspace,
            account,
            archiveRootDir);
        return persistedHistory
            .Concat(discoveredHistory)
            .Where(snapshot =>
            {
                var item = snapshot.Item;
                if (string.IsNullOrWhiteSpace(item.NewTitle))
                    return false;

                if (!string.IsNullOrWhiteSpace(item.ProjectDir))
                {
                    try
                    {
                        if (WorkspaceProjectScanner.IsValidProjectDirectory(
                                Path.GetFullPath(item.ProjectDir)))
                        {
                            return false;
                        }
                    }
                    catch
                    {
                        // Invalid or stale historic paths are treated as deleted.
                    }
                }

                var snapshotAccountKeys = new[]
                    {
                        item.AccountProfileId,
                        item.AccountProfileName,
                    }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .ToArray();
                if (snapshotAccountKeys.Length > 0)
                    return snapshotAccountKeys.Any(accountKeys.Contains);

                if (string.IsNullOrWhiteSpace(snapshot.Workspace))
                    return false;
                try
                {
                    return string.Equals(
                        Path.GetFullPath(snapshot.Workspace),
                        workspace,
                        StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            })
            .GroupBy(
                snapshot =>
                    $"{snapshot.Item.NewTitle.Trim()}\n{snapshot.Item.OriginalTitle.Trim()}",
                StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(snapshot =>
                    DateTimeOffset.TryParse(snapshot.Timestamp, out var parsed)
                        ? parsed
                        : DateTimeOffset.MinValue)
                .First())
            .ToArray();
    }

    private async void OnResumeSelectedCopyrightProofClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var vm = _vm;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (vm is null || owner is null)
            return;

        var workspace = (vm.WorkspacePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            vm.StatusMessage = "请先选择有效的 TikTok 工作目录";
            return;
        }

        if (vm.IsWorkspaceQueueBusy(workspace))
        {
            vm.StatusMessage = "当前工作目录队列正在运行或收尾，请结束后再继续补全版权证明";
            return;
        }

        var proofAccount = vm.SelectedAccount?.Model;
        if (proofAccount is null)
        {
            vm.StatusMessage = "请先选择要补全版权证明的账号";
            return;
        }

        var selectedRows = vm.QueueProjectRows
            .Where(row => row.Item.Enabled && !row.Item.Archived)
            .ToArray();
        if (selectedRows.Length == 0)
        {
            vm.StatusMessage = "请先勾选要继续补全版权证明的剧集";
            return;
        }

        var selectedTitlesByDir = selectedRows
            .Where(row => !string.IsNullOrWhiteSpace(row.Item.ProjectDir))
            .GroupBy(
                row => Path.GetFullPath(row.Item.ProjectDir),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().NewTitle,
                StringComparer.OrdinalIgnoreCase);
        if (selectedTitlesByDir.Count == 0)
        {
            vm.StatusMessage = "勾选项目没有有效的项目目录";
            return;
        }

        QueueProjectItem[] refreshedProjects;
        try
        {
            vm.StatusMessage = "正在检查勾选项目和已有证明材料…";
            refreshedProjects = await Task.Run(() =>
                WorkspaceQueueService.ScanProjects(workspace).ToArray());
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"读取勾选项目失败：{ex.Message}";
            return;
        }

        var selectedDirs = selectedTitlesByDir.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedProjects = refreshedProjects
            .Where(item =>
                !item.Archived &&
                !string.IsNullOrWhiteSpace(item.ProjectDir) &&
                selectedDirs.Contains(Path.GetFullPath(item.ProjectDir)))
            .GroupBy(
                item => Path.GetFullPath(item.ProjectDir),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (matchedProjects.Length == 0)
        {
            vm.StatusMessage = "勾选项目刷新后均未找到，无法继续补全版权证明";
            return;
        }

        var matchedDirs = matchedProjects
            .Select(item => Path.GetFullPath(item.ProjectDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingTitles = selectedTitlesByDir
            .Where(pair => !matchedDirs.Contains(pair.Key))
            .Select(pair => pair.Value)
            .ToArray();
        var proofSettings = ClientSettingsStore.Load();
        var reusableProofMaterialProjects = await Task.Run(() =>
            matchedProjects
                .Where(item => TikTokProofMaterialService
                    .HasReusableProofMaterialForCopyrightCompletion(
                        item,
                        proofSettings,
                        proofAccount))
                .Select(item => Path.GetFullPath(item.ProjectDir))
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
        var pendingProofMaterialCount =
            matchedProjects.Length - reusableProofMaterialProjects.Count;
        var previewNames = string.Join(
            Environment.NewLine,
            matchedProjects
                .Take(12)
                .Select(item => $"• {item.NewTitle}"));
        if (matchedProjects.Length > 12)
            previewNames += $"{Environment.NewLine}• …另有 {matchedProjects.Length - 12} 个项目";

        var confirmation =
            $"已勾选 {selectedTitlesByDir.Count} 个项目，准备执行 {matchedProjects.Length} 个。" +
            $"{Environment.NewLine}{Environment.NewLine}" +
            $"可复用证明材料：{reusableProofMaterialProjects.Count} 个" +
            $"{Environment.NewLine}" +
            $"需要继续生成材料：{pendingProofMaterialCount} 个" +
            $"{Environment.NewLine}" +
            $"需要执行版权证明页面编辑：{matchedProjects.Length} 个";
        if (missingTitles.Length > 0)
        {
            confirmation +=
                $"{Environment.NewLine}" +
                $"刷新后未找到、将跳过：{missingTitles.Length} 个";
        }
        confirmation +=
            $"{Environment.NewLine}{Environment.NewLine}{previewNames}" +
            $"{Environment.NewLine}{Environment.NewLine}" +
            "已完成的材料会直接复用；失败或中止的材料将从缺失步骤继续，不会强制重跑已完成步骤。";

        if (!await ConfirmAsync(owner, "继续补全勾选项目", confirmation))
        {
            vm.StatusMessage = "已取消继续补全勾选项目";
            return;
        }

        var preparation = CopyrightProofQueuePreparationService.Prepare(
            refreshedProjects,
            matchedProjects,
            reusableProofMaterialProjects);
        var persistedOptions = WorkspaceQueueService.LoadRunOptions(workspace);
        WorkspaceQueueService.SaveRunOptions(workspace, refreshedProjects, persistedOptions);
        await vm.ApplyPreparedWorkspaceQueueSnapshotAsync(
            workspace,
            refreshedProjects,
            persistedOptions);

        var options = vm.CreateCurrentQueueRunOptionsSnapshot();
        options.ConfigureForCopyrightProofCompletion();
        var executionProjectDirs = matchedProjects
            .Select(item => Path.GetFullPath(item.ProjectDir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var baseTarget = vm.CaptureCurrentWorkspaceQueueTarget();
        if (baseTarget is null)
        {
            vm.StatusMessage = "无法创建当前工作目录执行目标";
            return;
        }

        var executionTarget = baseTarget with
        {
            ProjectDirFilter = executionProjectDirs,
            PreferPersistedQueueSnapshot = true,
        };
        vm.AppendLog(
            $"继续补全勾选项目：准备执行 {preparation.TargetCount} 个；" +
            $"复用已有证明材料 {preparation.ReusedProofMaterialCount} 个，" +
            $"继续生成 {preparation.PendingProofMaterialCount} 个；" +
            $"网页编辑 {preparation.TargetCount} 个。");
        foreach (var title in missingTitles)
            vm.AppendLog($"继续补全勾选项目跳过：刷新后未找到「{title}」");

        var runCompleted = await StartQueueRunAsync(
            options,
            executionProjectDirs,
            confirmForceRerun: false,
            targetOverride: executionTarget);
        if (!runCompleted)
            return;

        var executionDirs = executionProjectDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completedRows = vm.QueueProjectRows
            .Where(row =>
                !string.IsNullOrWhiteSpace(row.Item.ProjectDir) &&
                executionDirs.Contains(Path.GetFullPath(row.Item.ProjectDir)) &&
                row.Item.StepStates.GetValueOrDefault(QueueStepRegistry.UploadSeries) ==
                    QueueStepStatus.Completed)
            .ToArray();

        var retryCount = executionProjectDirs.Length - completedRows.Length;
        vm.StatusMessage = retryCount == 0
            ? $"勾选项目版权证明补全完成：成功 {completedRows.Length} 个"
            : $"勾选项目版权证明补全结束：成功 {completedRows.Length} 个，仍需重试 {retryCount} 个";
        vm.AppendLog(vm.StatusMessage);
    }

    private async void OnMatchPublishedSeriesClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var vm = _vm;
        var owner = TopLevel.GetTopLevel(this) as Window;
        var accountVm = vm?.SelectedAccount;
        if (vm is null || owner is null)
            return;
        if (accountVm is null)
        {
            vm.StatusMessage = "请先选择要查询的账号";
            return;
        }

        var account = accountVm.Model;
        await TikTokPublishedSeriesMatchDialog.ShowAsync(
            owner,
            accountVm.DisplayName,
            (titles, progress, ct) =>
                LookupPublishedSeriesAsync(account, titles, progress, ct));
    }

    private async Task<IReadOnlyList<TikTokPublishedSeriesMatch>> LookupPublishedSeriesAsync(
        TikTokAccountProfile account,
        IReadOnlyList<string> titles,
        IProgress<TikTokPublishedSeriesLookupProgress> progress,
        CancellationToken ct)
    {
        var vm = _vm ?? throw new InvalidOperationException("TikTok 上传视图尚未初始化。");
        vm.StatusMessage = $"正在准备账号「{account.DisplayName}」的浏览器登录态…";
        var ready = await EnsureAccountBrowserReadyAsync(account, vm.AppendLog, ct);
        if (!ready.Ok)
            throw new InvalidOperationException(ready.Message);

        IEmbeddedBrowser? browser = null;
        if (!UsesPlaywrightUploadBrowser(account))
            browser = _browserHost?.TryGetHost(account.Id);

        vm.StatusMessage = $"正在匹配账号「{account.DisplayName}」的已发布剧集…";
        try
        {
            var results = await TikTokPublishedSeriesLookupService.LookupAsync(
                account,
                browser,
                titles,
                progress,
                vm.AppendLog,
                ct);
            var published = results.Count(match => match.IsPublished);
            vm.StatusMessage =
                $"已完成已发布剧集匹配：输入 {titles.Count} 个，已发布 {published} 个";
            vm.AppendLog(vm.StatusMessage);
            return results;
        }
        catch (OperationCanceledException)
        {
            vm.StatusMessage = "已停止匹配已发布剧集";
            throw;
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"匹配已发布剧集失败：{ex.Message}";
            vm.AppendLog(vm.StatusMessage);
            throw;
        }
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
            var result = await vm.RenameQueueProjectNewTitleAsync(row, input);
            var localStepsToRegenerate = new List<string>();
            if (result.ResetPoster)
                localStepsToRegenerate.Add(QueueStepRegistry.GeneratePoster);
            if (result.ResetProofMaterial)
                localStepsToRegenerate.Add(QueueStepRegistry.GenerateProofMaterial);
            if (result.ResetMaterialValidate)
                localStepsToRegenerate.Add(QueueStepRegistry.MaterialValidate);

            if (localStepsToRegenerate.Count > 0)
            {
                var options = vm.CreateCurrentQueueRunOptionsSnapshot();
                options.EnabledSteps = localStepsToRegenerate;
                options.ForceRerunCompletedSteps = false;
                options.UploadEntryMode = "";
                await StartQueueRunAsync(options, new[] { result.SourceProjectDir }, confirmForceRerun: false);
            }
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

    private async void OnExportExcelClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            vm.StatusMessage = "正在导出 Excel...";
            var path = await vm.ExportQueueExcelAsync();
            vm.StatusMessage = $"已导出 Excel：{path}";
            vm.AppendLog(vm.StatusMessage);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"导出 Excel 失败：{ex.Message}";
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private async void OnOpenExcelClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            vm.StatusMessage = "正在导出 Excel...";
            var path = await vm.ExportQueueExcelAsync();
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
        finally
        {
            if (button is not null) button.IsEnabled = true;
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
    {
        await StartQueueRunAsync(confirmForceRerun: true);
    }

    public async Task<bool> StartQueueRunFromRemoteAsync(
        WorkspaceQueueTarget target,
        QueueRunOptions? optionsOverride = null)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runTask = StartQueueRunAsync(
            optionsOverride,
            target.ProjectDirFilter,
            onWorkerStarted: () => started.TrySetResult(true),
            targetOverride: target);

        // 远程命令只等待 runner 注册成功；完整运行必须被持续观察，避免后台异常成为未观察任务异常。
        _ = ObserveRemoteQueueRunAsync(runTask, started);
        return await started.Task;
    }

    private async Task ObserveRemoteQueueRunAsync(
        Task<bool> runTask,
        TaskCompletionSource<bool> started)
    {
        try
        {
            var completedNormally = await runTask;
            started.TrySetResult(completedNormally);
        }
        catch (OperationCanceledException)
        {
            started.TrySetResult(false);
        }
        catch (Exception ex)
        {
            started.TrySetResult(false);
            try
            {
                var vm = _vm;
                if (vm is not null)
                {
                    vm.StatusMessage = $"远程启动的 TikTok 队列后台执行异常：{ex.Message}";
                    vm.AppendLog(vm.StatusMessage);
                }
            }
            catch
            {
                // 观察器本身不得再产生未观察异常。
            }
        }
    }

    private async Task<bool> StartQueueRunAsync(
        QueueRunOptions? optionsOverride = null,
        IReadOnlyCollection<string>? projectDirFilter = null,
        bool confirmForceRerun = false,
        Action? onWorkerStarted = null,
        WorkspaceQueueTarget? targetOverride = null,
        DateTime? preserveProjectLogsSince = null)
    {
        var vm = _vm;
        if (vm is null) return false;
        var runTarget = targetOverride ?? vm.CaptureCurrentWorkspaceQueueTarget();
        if (runTarget is null || string.IsNullOrWhiteSpace(runTarget.WorkspaceRoot))
        {
            vm.StatusMessage = "请先选择工作目录";
            return false;
        }

        var runWorkspaceRoot = NormalizeQueueWorkspaceRoot(runTarget.WorkspaceRoot);
        if (_activeQueueRunWorkspaces.Contains(runWorkspaceRoot) ||
            vm.IsWorkspaceQueueBusy(runWorkspaceRoot))
        {
            vm.StatusMessage = "目标工作目录队列正在启动、运行或安全收尾，本次启动未生效；请等待结束后再执行";
            vm.AppendLog(vm.StatusMessage);
            return false;
        }

        if (targetOverride is null && projectDirFilter is null && vm.FilteredQueueProjectRows.Count == 0)
        {
            vm.StatusMessage = "队列为空，请先刷新项目";
            return false;
        }

        var orderedProjectDirFilter = targetOverride is not null
            ? runTarget.ProjectDirFilter ?? projectDirFilter
            : projectDirFilter ?? GetCheckedProjectDirsInDisplayOrder();
        if (orderedProjectDirFilter is not null && orderedProjectDirFilter.Count == 0)
        {
            vm.StatusMessage = "请先在队列表格中选择项目";
            return false;
        }

        if (targetOverride is null && orderedProjectDirFilter is null)
        {
            vm.StatusMessage = "请先在队列表格中勾选要执行的项目";
            return false;
        }

        runTarget = runTarget with { ProjectDirFilter = orderedProjectDirFilter };
        if (confirmForceRerun)
        {
            var confirmedOptions = optionsOverride ?? vm.CreateCurrentQueueRunOptionsSnapshot();
            if (confirmedOptions.ForceRerunCompletedSteps)
            {
                var owner = TopLevel.GetTopLevel(this) as Window;
                if (owner is null)
                {
                    vm.StatusMessage = "无法打开确认弹窗";
                    return false;
                }

                var confirmed = await ConfirmAsync(
                    owner,
                    "确认强制重跑",
                    "已勾选“强制重跑已完成步骤”。继续执行会重新运行已完成的步骤，可能重新下载、改写、生成、删除源视频或上传。确认继续执行勾选队列？");
                if (!confirmed)
                {
                    vm.StatusMessage = "已取消执行勾选队列";
                    return false;
                }
            }

            optionsOverride = confirmedOptions;
        }

        if (!_activeQueueRunWorkspaces.Add(runWorkspaceRoot))
        {
            vm.StatusMessage = "目标工作目录已有启动请求正在处理，本次启动未生效";
            vm.AppendLog(vm.StatusMessage);
            return false;
        }
        if (vm.IsWorkspaceQueueBusy(runWorkspaceRoot))
        {
            _activeQueueRunWorkspaces.Remove(runWorkspaceRoot);
            vm.StatusMessage = "目标工作目录在确认期间已被其它任务启动，本次启动未生效";
            vm.AppendLog(vm.StatusMessage);
            return false;
        }
        _queueStopRequestedWorkspaces.Remove(runWorkspaceRoot);
        RefreshQueueRunButtons();

        var queueRunStarted = false;
        var workerReturnedSummary = false;
        var displayOptions = optionsOverride ?? vm.CreateCurrentQueueRunOptionsSnapshot();
        var isEditRun = string.Equals(displayOptions.UploadEntryMode, "edit", StringComparison.OrdinalIgnoreCase);
        var isCopyrightProofRun = displayOptions.IsCopyrightProofOnlyRun();
        vm.StatusMessage = isCopyrightProofRun
            ? "补全版权证明执行中…"
            : isEditRun
                ? "编辑剧集执行中…"
                : "TikTok 队列执行中…";
        try
        {
            var host = CreateQueuePublishHost();
            var ct = vm.BeginQueueRun();
            queueRunStarted = true;
            RefreshQueueRunButtons();

            var summary = await vm.RunQueueWorkerAsync(
                host,
                p => _queueProgressSink?.Post(p),
                (root, items) => vm.EnqueuePersistedQueueItems(root, items),
                ct,
                optionsOverride,
                orderedProjectDirFilter,
                onWorkerStarted,
                runTarget,
                preserveProjectLogsSince);
            workerReturnedSummary = summary is not null;
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
            vm.AppendLog(vm.StatusMessage);
        }
        finally
        {
            _activeQueueRunWorkspaces.Remove(runWorkspaceRoot);
            if (queueRunStarted)
                vm.EndQueueRun();
            RefreshQueueRunButtons();
        }

        return workerReturnedSummary;
    }

    private static IReadOnlyCollection<string> BuildLocalManualImportProjectFilter(LocalManualDramaBatchImportResult result)
        => result.ProjectDirs
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizeQueueWorkspaceRoot(string workspace)
    {
        var root = (workspace ?? "").Trim();
        if (string.IsNullOrWhiteSpace(root))
            return "";

        try
        {
            return Path.GetFullPath(root);
        }
        catch
        {
            return root;
        }
    }

    private async void OnStartAllQueuesClick(object? sender, RoutedEventArgs e)
        => await StartAllQueueRunAsync();

    public Task<bool> StartAllQueueRunFromRemoteAsync(
        QueueRunOptions? optionsOverride,
        IReadOnlyList<WorkspaceQueueTarget> targets)
        => StartAllQueueRunAsync(optionsOverride, targets);

    private async Task<bool> StartAllQueueRunAsync(
        QueueRunOptions? optionsOverride = null,
        IReadOnlyList<WorkspaceQueueTarget>? targetsOverride = null)
    {
        var vm = _vm;
        if (vm is null) return false;
        var targets = targetsOverride ?? vm.BuildAccountWorkspaceTargets();
        if (targets.Count == 0)
        {
            vm.StatusMessage = "没有可执行的工作目录（请为账号配置有效工作目录）";
            return false;
        }

        if (targetsOverride is null && vm.IsQueueRunning)
        {
            vm.StatusMessage = "已有工作目录队列在运行中";
            return false;
        }

        var targetRoots = targets
            .Select(target => NormalizeQueueWorkspaceRoot(target.WorkspaceRoot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targetRoots.Length != targets.Count)
        {
            vm.StatusMessage = "执行目标中包含重复工作目录，已取消启动";
            vm.AppendLog(vm.StatusMessage);
            return false;
        }

        var occupiedTarget = targets.FirstOrDefault(target =>
        {
            var root = NormalizeQueueWorkspaceRoot(target.WorkspaceRoot);
            return _activeQueueRunWorkspaces.Contains(root) || vm.IsWorkspaceQueueBusy(root);
        });
        if (occupiedTarget is not null)
        {
            vm.StatusMessage = $"目标工作目录队列正在启动、运行或安全收尾：{occupiedTarget.WorkspaceRoot}";
            vm.AppendLog(vm.StatusMessage);
            return false;
        }

        foreach (var root in targetRoots)
            _activeQueueRunWorkspaces.Add(root);

        var completed = false;
        var queueRunBegun = false;
        try
        {
            var host = CreateQueuePublishHost();
            var ct = vm.BeginQueueRun();
            queueRunBegun = true;
            foreach (var root in targetRoots)
                _queueStopRequestedWorkspaces.Remove(root);
            RefreshQueueRunButtons();
            vm.StatusMessage = $"并行执行 {targets.Count} 个工作目录队列…";

            var summaries = await vm.RunAllAccountWorkspaceQueuesAsync(
                host,
                p => _queueProgressSink?.Post(p),
                (root, items) => vm.EnqueuePersistedQueueItems(root, items),
                ct,
                targetsOverride,
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
            completed = !stopped;
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
            if (queueRunBegun)
                vm.EndQueueRun();
            foreach (var root in targetRoots)
                _activeQueueRunWorkspaces.Remove(root);
            RefreshQueueRunButtons();
        }

        return completed;
    }

    private void OnStopQueueClick(object? sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is not null)
        {
            var root = NormalizeQueueWorkspaceRoot(vm.WorkspacePath);
            var preparationCancelled = false;
            if (!string.IsNullOrWhiteSpace(root))
            {
                _queueStopRequestedWorkspaces.Add(root);
                preparationCancelled = CancelCopyrightProofPreparation(root);
            }
            vm.RequestStopQueue(root);
            vm.StatusMessage = preparationCancelled
                ? "正在停止补全版权证明…"
                : "正在停止队列…";
        }
        RefreshQueueRunButtons();
    }

    private void SetQueueRunning(bool running)
    {
        if (StartQueueButton is not null)
        {
            StartQueueButton.Content = running ? "生产中" : "开始生产";
            StartQueueButton.IsEnabled = !running;
        }
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
        false;

    private static bool UsesPlaywrightUploadBrowser(TikTokAccountProfile account) =>
        string.Equals((account.TiktokUploadBrowserMode ?? "").Trim(), "playwright", StringComparison.OrdinalIgnoreCase);

    private SemaphoreSlim GetAutoLoginLock(TikTokAccountProfile account)
    {
        var key = string.IsNullOrWhiteSpace(account.Id) ? account.DisplayName : account.Id;
        lock (_autoLoginLocksGate)
        {
            if (!_autoLoginLocks.TryGetValue(key, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _autoLoginLocks[key] = gate;
            }

            return gate;
        }
    }

    private async Task<QueueBrowserReadyResult> EnsureAutoLoginStateAsync(
        TikTokAccountProfile account,
        Action<string>? log,
        CancellationToken ct,
        bool forceRefresh,
        string reason)
    {
        var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
        if (!forceRefresh && File.Exists(authPath))
            return QueueBrowserReadyResult.Ready();

        if (string.IsNullOrWhiteSpace(account.TiktokLoginEmail) ||
            string.IsNullOrWhiteSpace(account.TiktokLoginPassword))
        {
            return QueueBrowserReadyResult.NotReady(
                "自动登录失败：请先在「账号管理」为当前账号配置 TikTok 用户名和密码。");
        }

        var gate = GetAutoLoginLock(account);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && File.Exists(authPath))
                return QueueBrowserReadyResult.Ready();

            var vm = _vm;
            var browserHost = _browserHost;
            if (vm is null)
                return QueueBrowserReadyResult.NotReady("自动登录失败：内置浏览器尚未初始化。");

            var accountVm = vm.FindAccount(account.Id) ?? vm.FindAccount(account.DisplayName);
            if (accountVm is null)
                return QueueBrowserReadyResult.NotReady($"自动登录失败：未找到账号「{account.DisplayName}」。");

            if (UsesPlaywrightUploadBrowser(account))
            {
                if (forceRefresh)
                    AccountLoginStatusHelper.DeleteAuthState(account);
                log?.Invoke(string.IsNullOrWhiteSpace(reason)
                    ? "TikTok 授权文件缺失，正在启动外部浏览器登录…"
                    : $"{reason}（使用外部浏览器登录）");
                var externalResult = await TikTokLoginService.LoginAsync(
                        account,
                        log,
                        ct,
                        timeoutSeconds: 300,
                        forceLaunchBrowser: true)
                    .ConfigureAwait(false);
                account.TiktokStorageStatePath = externalResult.AuthPath;
                account.TiktokLastLoginEmail = externalResult.Email;
                account.TiktokLastLoginAt = externalResult.LoggedInAt;
                if (!ReferenceEquals(accountVm.Model, account))
                {
                    accountVm.Model.TiktokStorageStatePath = externalResult.AuthPath;
                    accountVm.Model.TiktokLastLoginEmail = externalResult.Email;
                    accountVm.Model.TiktokLastLoginAt = externalResult.LoggedInAt;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    vm.SaveAccountProfile(account);
                    accountVm.Status = AccountStatus.Online;
                    accountVm.RefreshFromModel();
                }).GetTask().ConfigureAwait(false);
                if (browserHost is not null)
                    await browserHost.SyncExternalAuthAsync(accountVm).ConfigureAwait(false);
                log?.Invoke($"TikTok 外部浏览器登录完成，授权文件已更新：{externalResult.AuthPath}");
                return QueueBrowserReadyResult.Ready();
            }

            if (browserHost is null)
                return QueueBrowserReadyResult.NotReady("自动登录失败：内置浏览器尚未初始化。");

            log?.Invoke(string.IsNullOrWhiteSpace(reason)
                ? "检测到 TikTok 授权文件缺失，开始通过内置浏览器自动登录..."
                : $"{reason}（使用内置浏览器登录）");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (vm.SelectedAccount?.Id != accountVm.Id)
                    vm.SelectedAccount = accountVm;
                PublishBrowserFocusRequested?.Invoke(accountVm);
                _ensureBrowserMounted?.Invoke();
            })
                .GetTask()
                .ConfigureAwait(false);

            var result = await browserHost
                .BeginLoginAndWaitForAuthAsync(
                    accountVm,
                    forceRefresh,
                    QueueAutoLoginTimeout,
                    ct,
                    log)
                .ConfigureAwait(false);

            var loginEmail = account.ResolveTikTokAccountName();
            account.TiktokStorageStatePath = result.AuthPath;
            account.TiktokLastLoginEmail = loginEmail;
            account.TiktokLastLoginAt = result.SavedAt;
            if (!ReferenceEquals(accountVm.Model, account))
            {
                accountVm.Model.TiktokStorageStatePath = result.AuthPath;
                accountVm.Model.TiktokLastLoginEmail = loginEmail;
                accountVm.Model.TiktokLastLoginAt = result.SavedAt;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm.SaveAccountProfile(account);
                accountVm.Status = AccountStatus.Online;
                accountVm.RefreshFromModel();
            }).GetTask().ConfigureAwait(false);

            log?.Invoke($"TikTok 内置浏览器自动登录完成，授权文件已更新：{result.AuthPath}");
            return QueueBrowserReadyResult.Ready();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return QueueBrowserReadyResult.NotReady($"自动登录失败：{ex.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsLoginNotReadyMessage(string? message)
    {
        var text = message ?? "";
        return text.Contains("未登录", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("登录页", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("login", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUploadLoginFailure(string? message)
    {
        var text = message ?? "";
        return text.Contains("登录态失效", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("未登录", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("跳转到登录页", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("login", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>账号选了外部浏览器时必须使用外部 CDP；端点缺失或不可达时直接失败，避免静默回退内置浏览器。</summary>
    private static async Task<QueueBrowserReadyResult> EnsureExternalUploadBrowserReadyAsync(
        TikTokAccountProfile account,
        Action<string>? log,
        CancellationToken ct)
    {
        var endpoint = (account.TiktokExternalBrowserCdpEndpoint ?? "").Trim();
        if (string.IsNullOrEmpty(endpoint))
        {
            return QueueBrowserReadyResult.NotReady(
                $"账号「{account.DisplayName}」已选择外部浏览器上传，但未配置 CDP 端点，请在「账号管理 > 网络/IP」中填写。");
        }

        if (!await EmbeddedBrowserCdpProbe.IsReachableAsync(endpoint, ct).ConfigureAwait(false))
        {
            return QueueBrowserReadyResult.NotReady(
                $"账号「{account.DisplayName}」已选择外部浏览器上传，但 CDP 端点不可达：{endpoint}。请确认外部浏览器已启动且端口正确。");
        }

        log?.Invoke($"使用外部浏览器上传（CDP：{endpoint}）");
        return QueueBrowserReadyResult.Ready();
    }

    private async Task<QueueBrowserReadyResult> EnsureAccountBrowserReadyAsync(
        TikTokAccountProfile account,
        Action<string>? log,
        CancellationToken ct)
    {
        // 程序自动打开的外部浏览器：只需存在授权文件（发布时 launch 浏览器并复用登录态）。
        if (UsesPlaywrightUploadBrowser(account))
        {
            var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
            if (!File.Exists(authPath))
            {
                var loginReady = await EnsureAutoLoginStateAsync(
                    account,
                    log,
                    ct,
                    forceRefresh: false,
                    reason: "外部浏览器上传缺少授权文件，正在执行 TikTok 自动登录...")
                    .ConfigureAwait(false);
                if (!loginReady.Ok)
                    return loginReady;
            }

            log?.Invoke($"上传浏览器：外部浏览器（程序自动打开，{(account.TiktokPlaywrightUploadHeadless ? "无头" : "有头")}）");
            return QueueBrowserReadyResult.Ready();
        }

        if (UsesExternalUploadBrowser(account))
            return await EnsureExternalUploadBrowserReadyAsync(account, log, ct).ConfigureAwait(false);

        var provider = RequireBrowserProvider();
        var ready = await provider
            .EnsureBrowserReadyAsync(account, ct, EmbeddedBrowserAccessOptions.Background, log)
            .ConfigureAwait(false);
        if (ready.Ok || !IsLoginNotReadyMessage(ready.Message))
            return ready;

        return await EnsureAutoLoginStateAsync(
            account,
            log,
            ct,
            forceRefresh: false,
            reason: "检测到内置浏览器尚未登录，正在执行 TikTok 自动登录...")
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
        // 与 EnsureAccountBrowserReadyAsync 保持一致：程序自动打开的外部浏览器 > 外部浏览器(CDP 可达) > 内置浏览器。
        IEmbeddedBrowser? browser;
        var usingEmbeddedBrowser = false;
        if (UsesPlaywrightUploadBrowser(account))
        {
            var ready = await EnsureAutoLoginStateAsync(
                account,
                log,
                ct,
                forceRefresh: false,
                reason: "外部浏览器上传前正在确认 TikTok 自动登录授权...")
                .ConfigureAwait(false);
            if (!ready.Ok)
                return PublishResult.Fail(ready.Message);

            // 程序自动打开的外部浏览器由发布自动化内部 launch，这里只传标记载体。
            browser = new PlaywrightLaunchBrowser(account);
        }
        else if (UsesExternalUploadBrowser(account))
        {
            var ready = await EnsureExternalUploadBrowserReadyAsync(account, log, ct).ConfigureAwait(false);
            if (!ready.Ok)
                return PublishResult.Fail(ready.Message);

            browser = new ExternalCdpBrowser(account);
        }
        else
        {
            var ready = await RequireBrowserProvider()
                .EnsureBrowserReadyAsync(account, ct, EmbeddedBrowserAccessOptions.Background, log)
                .ConfigureAwait(false);
            if (!ready.Ok && IsLoginNotReadyMessage(ready.Message))
            {
                ready = await EnsureAutoLoginStateAsync(
                    account,
                    log,
                    ct,
                    forceRefresh: false,
                    reason: "检测到内置浏览器尚未登录，正在执行 TikTok 自动登录...")
                    .ConfigureAwait(false);
            }

            if (!ready.Ok)
                return PublishResult.Fail(ready.Message);

            browser = _browserHost?.TryGetHost(account.Id);
            usingEmbeddedBrowser = true;
        }

        if (browser is null)
            return PublishResult.Fail("内置浏览器未就绪或未登录，请先在「浏览器」页完成登录");

        var item = QueuePublishHost.ToPublishItem(project);
        item.ForceEditUpload = string.Equals(options.UploadEntryMode, "edit", StringComparison.OrdinalIgnoreCase);
        item.CopyrightProofOnly = options.IsCopyrightProofOnlyRun();
        if (!item.CopyrightProofOnly && string.IsNullOrWhiteSpace(item.VideoPath))
            return PublishResult.Fail("项目没有可用视频");

        ApplyConfigDefaults(item);
        // 队列上传按账号配置的「提交动作」决定最终动作（对齐 Python submit_action 行为）。
        var effectiveAction = ResolveAccountFinalAction(account, finalAction);
        log($"最终动作：{FinalActionLabel(effectiveAction)}（来自账号「{account.DisplayName}」的提交动作配置）");
        var attemptSignature = UploadAttemptSignature(project.ProjectDir);
        var result = item.CopyrightProofOnly
            ? await TikTokCopyrightProofEditService
                .UpdateAsync(account, item, browser, effectiveAction, log, ct)
                .ConfigureAwait(false)
            : await _automation
                .PublishPreflightedAsync(account, item, browser, effectiveAction, log, ct)
                .ConfigureAwait(false);
        if (!result.Ok && IsUploadLoginFailure(result.Message))
        {
            var loginReady = await EnsureAutoLoginStateAsync(
                account,
                log,
                ct,
                forceRefresh: true,
                reason: "检测到 TikTok 登录态失效，正在自动重新登录后重试当前剧集...")
                .ConfigureAwait(false);
            if (loginReady.Ok)
            {
                log("TikTok 自动登录完成，正在重试当前剧集上传...");
                if (UsesPlaywrightUploadBrowser(account))
                    browser = new PlaywrightLaunchBrowser(account);
                else if (UsesExternalUploadBrowser(account))
                {
                    var externalReady = await EnsureExternalUploadBrowserReadyAsync(account, log, ct).ConfigureAwait(false);
                    if (!externalReady.Ok)
                        return PublishResult.Fail(externalReady.Message);

                    browser = new ExternalCdpBrowser(account);
                }
                else
                    browser = _browserHost?.TryGetHost(account.Id) ?? browser;

                result = item.CopyrightProofOnly
                    ? await TikTokCopyrightProofEditService
                        .UpdateAsync(account, item, browser, effectiveAction, log, ct)
                        .ConfigureAwait(false)
                    : await _automation
                        .PublishPreflightedAsync(account, item, browser, effectiveAction, log, ct)
                        .ConfigureAwait(false);
            }
            else
            {
                log(loginReady.Message);
            }
        }

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

                result = item.CopyrightProofOnly
                    ? await TikTokCopyrightProofEditService
                        .UpdateAsync(account, item, browser, effectiveAction, log, ct)
                        .ConfigureAwait(false)
                    : await _automation
                        .PublishPreflightedAsync(account, item, browser, effectiveAction, log, ct)
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
               text.Contains("TargetClosedException", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
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

    private sealed class ManualExternalBrowserSession : IAsyncDisposable
    {
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;

        public ManualExternalBrowserSession(IPlaywright playwright, IBrowser browser)
        {
            _playwright = playwright;
            _browser = browser;
        }

        public async ValueTask DisposeAsync()
        {
            try { await _browser.DisposeAsync().ConfigureAwait(false); }
            finally { _playwright.Dispose(); }
        }
    }
}
