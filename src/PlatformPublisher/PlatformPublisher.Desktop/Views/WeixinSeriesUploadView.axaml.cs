using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Weixin.Publishing;
using TikTokPublisher.Ui.Views;
using ChannelAccount = ChannelsPublisher.Core.Models.PublishAccount;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinSeriesUploadView : UserControl
{
    private Func<PublishJob, IProgress<string>, CancellationToken, Task>? _run;
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
        _accountProvider = accountProvider;
        RefreshAccountSummary();
    }

    internal void BindWorkspace(WeixinChannelPublishAdapter adapter, Func<ChannelAccount?> accountProvider,
        Func<bool> canStart, Func<string, CancellationToken, Task> prepareSession)
    {
        _reviewStore = WeixinSeriesReviewStore.CreateDefault();
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
            SetStatus("素材发布正在运行，请等任务结束后再开始剧集上架。", isError: true);
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
        if (_canStart is not null && configPath.Length > 0 && !File.Exists(configPath))
        {
            SetStatus("找不到选择的剧集配置。", isError: true);
            return;
        }
        if (_canStart is not null && string.IsNullOrWhiteSpace(account.ProfileDir))
        {
            SetStatus("当前账号缺少会话目录，请在账号配置中核对。", isError: true);
            return;
        }

        RefreshAccountSummary();
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
        if (_reviewStore?.HasReachedReview(job.AccountId, job.ProjectDirectory) == true &&
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
            await _run(job, progress, cancellation.Token);
            var status = _canStart is null ? "剧集上架流程完成。" : "已结束提审页面操作，请在平台核对剧集状态。";
            SetStatus(status, isError: false);
            AddLog(status);
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
            SetRunning(false);
            RuntimeChanged?.Invoke();
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
