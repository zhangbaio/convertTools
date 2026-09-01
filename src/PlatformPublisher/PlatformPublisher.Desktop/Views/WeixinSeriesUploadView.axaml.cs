using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Weixin.Publishing;
using ChannelAccount = ChannelsPublisher.Core.Models.PublishAccount;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinSeriesUploadView : UserControl
{
    private IPlatformPublishAdapter? _adapter;
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
        _adapter = adapter;
        _accountProvider = accountProvider;
        RefreshAccountSummary();
    }

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
        if (_adapter is null)
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

        _cancellation = new CancellationTokenSource();
        SetRunning(true);
        RefreshAccountSummary();
        var job = new PublishJob
        {
            Id = $"series-{Guid.NewGuid():N}",
            Platform = PublishPlatform.WeixinChannel,
            Kind = PublishJobKind.Series,
            ProjectDirectory = Path.GetFullPath(projectDirectory),
            ProjectName = Path.GetFileName(projectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            ConfigPath = ConfigPathInput.Text?.Trim() ?? string.Empty,
            AccountId = account.Id,
            AccountName = account.Name,
            AccountSessionDirectory = account.ProfileDir,
            PublishCount = 9999,
            PlatformOptionsJson = _options.ToJson(),
        };
        var progress = new Progress<string>(message =>
        {
            SetStatus(message, isError: false);
            AddLog(message);
        });
        AddLog($"开始剧集上架：{job.ProjectName}，账号：{account.Name}");
        try
        {
            await _adapter.RunAsync(job, progress, _cancellation.Token);
            SetStatus("剧集上架流程完成。", isError: false);
            AddLog("剧集上架流程完成。");
        }
        catch (OperationCanceledException)
        {
            SetStatus("任务已停止，可重新开始继续。", isError: false);
            AddLog("任务已停止。");
        }
        catch (Exception ex)
        {
            SetStatus($"剧集上架失败：{ex.Message}", isError: true);
            AddLog($"失败：{ex}");
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetRunning(false);
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
    }

    private void AddLog(string message)
    {
        Logs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        while (Logs.Count > 500)
            Logs.RemoveAt(Logs.Count - 1);
    }
}
