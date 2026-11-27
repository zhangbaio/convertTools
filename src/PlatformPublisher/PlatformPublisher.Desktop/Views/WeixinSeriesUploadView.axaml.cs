using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Weixin.Publishing;
using TikTokPublisher.Ui.Views;
using ChannelAccount = ChannelsPublisher.Core.Models.PublishAccount;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinSeriesUploadView : UserControl
{
    private Func<PublishJob, IProgress<string>, CancellationToken, Task>? _run;
    private WeixinChannelPublishAdapter? _seriesAdapter;
    private Func<string, CancellationToken, Task>? _prepareSession;
    private Func<bool>? _canStart;
    private WeixinSeriesReviewStore? _reviewStore;
    private bool _confirmingRetry;
    private TaskCompletionSource? _reviewComplete;
    private string? _runningAccountName;

    public bool IsRunning => _cancellation is not null;
    public string RuntimeStatus => _runningAccountName is null
        ? StatusText.Text ?? string.Empty
        : $"[{_runningAccountName}] {StatusText.Text}";
    public event Action? RuntimeChanged;
    private Func<ChannelAccount?>? _accountProvider;
    private CancellationTokenSource? _cancellation;
    private WeixinPublishOptions _options = new();
    private Action<string, string, WeixinSeriesUploadMode>? _openSeriesForm;

    public WeixinSeriesUploadView()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => RefreshAccountSummary();
    }

    public ObservableCollection<string> Logs { get; } = new();

    public void Bind(IPlatformPublishAdapter adapter, Func<ChannelAccount?> accountProvider)
    {
        _run = adapter.RunAsync;
        BindUploadMode(adapter as WeixinChannelPublishAdapter);
        _accountProvider = accountProvider;
        RefreshAccountSummary();
    }

    internal void BindWorkspace(WeixinChannelPublishAdapter adapter, Func<ChannelAccount?> accountProvider,
        Func<bool> canStart, Func<string, CancellationToken, Task> prepareSession)
    {
        _reviewStore = WeixinSeriesReviewStore.CreateDefault();
        BindUploadMode(adapter);
        _prepareSession = prepareSession;
        _run = async (job, progress, token) =>
        {
            await prepareSession(job.AccountId, token);
            await adapter.RunAsync(job, progress, token, async reviewToken =>
            {
                _reviewStore.RecordReachedReview(job.AccountId, job.ProjectDirectory);
                await ConfirmReviewCompleteAsync(reviewToken);
            });
        };
        _accountProvider = accountProvider;
        _canStart = canStart;
        RefreshAccountSummary();
    }

    private void BindUploadMode(WeixinChannelPublishAdapter? adapter)
    {
        _seriesAdapter = adapter;
        UploadModeLabel.IsVisible = UploadModePanel.IsVisible = adapter is not null;
        UpdateUploadModeControls();
    }

    internal void BindSeriesForm(Action<string, string, WeixinSeriesUploadMode> openForm)
    {
        _openSeriesForm = openForm;
        LegacySeriesOptionsButton.IsVisible = false;
        SeriesFormButton.IsVisible = true;
        OptionsSummary.Text = "按账号方案和项目表单上传";
    }

    private void OnSeriesFormClick(object? sender, RoutedEventArgs e) =>
        _openSeriesForm?.Invoke(ProjectDirectoryInput.Text ?? "", ConfigPathInput.Text ?? "", SelectedUploadMode());

    private WeixinSeriesUploadMode SelectedUploadMode() =>
        _seriesAdapter is not null && UploadBackend.SelectedIndex == 1
            ? SubmitWithApi.IsChecked == true ? WeixinSeriesUploadMode.ApiSubmit : WeixinSeriesUploadMode.ApiUpload
            : WeixinSeriesUploadMode.Browser;

    private void OnUploadBackendChanged(object? sender, SelectionChangedEventArgs e) => UpdateUploadModeControls();

    private void UpdateUploadModeControls()
    {
        if (SubmitWithApi is null || ForceApiReupload is null)
            return;
        var api = _seriesAdapter is not null && UploadBackend.SelectedIndex == 1;
        SubmitWithApi.IsVisible = ForceApiReupload.IsVisible = api;
    }

    internal void SetAccount(ChannelAccount? account)
    {
        RefreshAccountSummary();
        if (!IsRunning)
            ProjectDirectoryInput.Text = account?.WorkRootDirectory;
    }

    public void Stop() => _cancellation?.Cancel();

    private async Task ConfirmReviewCompleteAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            _reviewComplete = completion;
            ReviewCompleteButton.IsVisible = true;
            SetStatus("已到提审页。请在浏览器中核对并决定是否提交，处理后点击「已处理浏览器中的提审页面」。", isError: false);
            AddLog(RuntimeStatus);
        });
        try
        {
            await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _reviewComplete = null;
                ReviewCompleteButton.IsVisible = false;
            });
        }
    }

    private void OnReviewCompleteClick(object? sender, RoutedEventArgs e) => _reviewComplete?.TrySetResult();

    private void OnRefreshAccountClick(object? sender, RoutedEventArgs e) => RefreshAccountSummary();

    private void RefreshAccountSummary()
    {
        var account = _accountProvider?.Invoke();
        AccountSummary.Text = account is null
            ? "请先在左侧添加并选择一个账号"
            : $"{account.Name} · {account.Status}";
    }

    private async void OnPickProjectDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择视频号剧集项目目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
            ProjectDirectoryInput.Text = folders[0].Path.LocalPath;
    }

    private async void OnPickConfigClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择视频号剧集配置",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON 配置") { Patterns = ["*.json"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count > 0)
            ConfigPathInput.Text = files[0].Path.LocalPath;
    }

    private async void OnAdvancedConfigClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var result = await WeixinPublishConfigDialog.ShowAsync(owner, _options);
        if (result is null) return;
        _options = result;
        _options.OverrideSeriesEpisodeSelection = true;
        OptionsSummary.Text = result.EpisodeSelectionMode switch
        {
            "all" => "全部集数",
            "explicit" => $"具体集数：{result.EpisodeIndexes}",
            _ => $"从第 {result.StartEpisodeIndex} 集开始",
        };
    }

    private async void OnStartClick(object? sender, RoutedEventArgs e)
    {
        if (IsRunning || _confirmingRetry)
            return;
        if (_canStart?.Invoke() == false)
        {
            SetStatus("有其他视频号任务正在运行，请等任务结束后再开始剧集上架。", isError: true);
            return;
        }
        if (_run is null)
        {
            SetStatus("视频号剧集上架服务尚未绑定。", isError: true);
            return;
        }
        var account = _accountProvider?.Invoke();
        if (account is null)
        {
            SetStatus("请先在左侧添加并选择一个视频号账号。", isError: true);
            return;
        }
        var projectDirectory = ProjectDirectoryInput.Text?.Trim() ?? string.Empty;
        if (!Directory.Exists(projectDirectory))
        {
            SetStatus("请选择存在的剧集项目目录。", isError: true);
            return;
        }

        var configPath = ConfigPathInput.Text?.Trim() ?? string.Empty;
        if (_canStart is not null && string.IsNullOrWhiteSpace(account.ProfileDir))
        {
            SetStatus("当前账号缺少会话目录，请在账号配置中核对。", isError: true);
            return;
        }

        RefreshAccountSummary();
        var api = _seriesAdapter is not null && UploadBackend.SelectedIndex == 1;
        var mode = SelectedUploadMode();
        _options.SeriesUploadMode = mode;
        _options.ReuploadSeriesApiFiles = api && ForceApiReupload.IsChecked == true;
        var job = new PublishJob
        {
            Id = $"series-{Guid.NewGuid():N}",
            Platform = PublishPlatform.WeixinChannel,
            Kind = PublishJobKind.Series,
            ProjectDirectory = Path.GetFullPath(projectDirectory),
            ProjectName = Path.GetFileName(projectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            ConfigPath = configPath,
            AccountId = account.Id,
            AccountName = account.Name,
            AccountSessionDirectory = account.ProfileDir,
            PublishCount = 9999,
            PlatformOptionsJson = _options.ToJson(),
        };
        if (!api && _reviewStore?.HasReachedReview(job.AccountId, job.ProjectDirectory) == true &&
            !_reviewStore.HasConfirmedSubmission(job.AccountId, job.ProjectDirectory) &&
            TopLevel.GetTopLevel(this) is Window owner)
        {
            _confirmingRetry = true;
            try
            {
                if (!await ConfirmDialog.ShowAsync(owner, "核对原剧集记录",
                        "此账号的该项目已执行到提审页面。请先核对视频号后台的草稿和审核列表；只有平台没有该剧集记录时才应重新上传。",
                        confirmText: "已核对，重新上传") || _canStart?.Invoke() == false)
                    return;
            }
            finally
            {
                _confirmingRetry = false;
            }
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _runningAccountName = account.Name;
        SetRunning(true);
        SetStatus($"开始剧集上架：{job.ProjectName}", isError: false);
        var progress = new Progress<string>(message =>
        {
            if (!ReferenceEquals(_cancellation, cancellation) || cancellation.IsCancellationRequested)
                return;
            SetStatus(message, isError: false);
            AddLog(message);
        });
        AddLog($"开始剧集上架：{job.ProjectName}，账号：{account.Name}");
        try
        {
            string status;
            if (api && _seriesAdapter is { } adapter)
            {
                if (_prepareSession is not null)
                    await _prepareSession(job.AccountId, cancellation.Token);
                var result = await adapter.RunSeriesApiAsync(job, progress, ConfirmApiSubmitAsync, cancellation.Token);
                status = WeixinChannelPublishAdapter.DescribeApiResult(result);
            }
            else
            {
                await _run(job, progress, cancellation.Token);
                status = _canStart is null ? "剧集上架流程完成。" : "已结束提审页面操作，请在平台核对剧集状态。";
            }
            SetStatus(status, isError: false);
            AddLog(status);
        }
        catch (WeixinSeriesSubmissionPendingException ex)
        {
            SetStatus(ex.Message, isError: true);
            AddLog(ex.Message);
        }
        catch (OperationCanceledException)
        {
            SetStatus("任务已停止，可重新开始继续。", isError: false);
            AddLog("任务已停止。");
        }
        catch (Exception ex)
        {
            SetStatus($"剧集上架失败：{ex.Message}", isError: true);
            AddLog($"失败：{ex.Message}");
        }
        finally
        {
            _cancellation = null;
            _runningAccountName = null;
            ForceApiReupload.IsChecked = false;
            SetRunning(false);
            RuntimeChanged?.Invoke();
        }
    }

    private Task<bool> ConfirmApiSubmitAsync(WeixinSeriesApiConfirmation details, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            if (TopLevel.GetTopLevel(this) is not Window owner)
                return false;
            return await ConfirmDialog.ShowAsync(owner, "确认视频号接口提审",
                $"账号：{details.Account}\n剧名：{details.Title}\n集数：{details.EpisodeCount}\n\n上传完成后将向视频号正式提审。",
                500, 240, confirmText: "上传并提审");
        });

    private async void OnResolveReviewClick(object? sender, RoutedEventArgs e)
    {
        if (IsRunning || _confirmingRetry || _seriesAdapter is not { } adapter ||
            TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var account = _accountProvider?.Invoke();
        var directory = ProjectDirectoryInput.Text?.Trim() ?? string.Empty;
        if (account is null || !Directory.Exists(directory))
        {
            SetStatus("请先选择视频号账号和剧集项目目录。", isError: true);
            return;
        }
        _confirmingRetry = true;
        try
        {
            var submitted = await ConfirmDialog.ShowChoiceAsync(owner, "核对视频号剧集提审",
                $"请在「{account.Name}」的视频号后台核对本项目的草稿与审核记录。只有确认平台没有记录时，才允许重新提交。",
                "平台已有记录", "确认无记录");
            if (submitted is null)
                return;
            var resolved = await Task.Run(() => adapter.ResolveSeriesReview(account.Id, directory, submitted.Value));
            var message = resolved
                ? submitted.Value ? "已记录平台提审结果。" : "已记录平台无提审结果，可继续执行。"
                : "当前项目没有需要核对的提审记录。";
            SetStatus(message, isError: false);
            AddLog(message);
        }
        catch (Exception ex)
        {
            SetStatus($"保存核对结果失败：{ex.Message}", isError: true);
        }
        finally
        {
            _confirmingRetry = false;
        }
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        SetStatus("正在停止并保存当前进度…", isError: false);
    }

    private void OnClearLogsClick(object? sender, RoutedEventArgs e) => Logs.Clear();

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        UploadModePanel.IsEnabled = !running;
    }

    private void SetStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = Avalonia.Media.Brush.Parse(isError ? "#D92D20" : "#475569");
        RuntimeChanged?.Invoke();
    }

    private void AddLog(string message)
    {
        Logs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        while (Logs.Count > 500)
            Logs.RemoveAt(Logs.Count - 1);
    }
}
