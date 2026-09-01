using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class WeixinDramaDownloadViewModel : ObservableObject
{
    private readonly IDramaSearchService _searchService;
    private readonly IDramaProjectBootstrapper _bootstrapper;
    private readonly IWorkService _workService;
    private readonly MainWindowViewModel _mainViewModel;
    private CancellationTokenSource? _cts;

    public WeixinDramaDownloadViewModel(
        IDramaSearchService searchService,
        IDramaProjectBootstrapper bootstrapper,
        IWorkService workService,
        MainWindowViewModel mainViewModel)
    {
        _searchService = searchService;
        _bootstrapper = bootstrapper;
        _workService = workService;
        _mainViewModel = mainViewModel;
        SearchCommand = new AsyncRelayCommand(SearchAsync, CanSearch);
        TodayCommand = new AsyncRelayCommand(LoadTodayAsync, () => !IsBusy);
        ImportCheckedCommand = new AsyncRelayCommand(() => ProcessCheckedAsync(download: false), CanProcess);
        DownloadCheckedCommand = new AsyncRelayCommand(() => ProcessCheckedAsync(download: true), CanProcess);
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
    }

    public ObservableCollection<DramaSearchRowViewModel> Results { get; } = [];
    public IReadOnlyList<string> QualityChoices { get; } = ["1080P", "720P"];
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand TodayCommand { get; }
    public IAsyncRelayCommand ImportCheckedCommand { get; }
    public IAsyncRelayCommand DownloadCheckedCommand { get; }
    public IRelayCommand StopCommand { get; }

    [ObservableProperty] private string _rootDirectory = string.Empty;
    [ObservableProperty] private string _keyword = string.Empty;
    [ObservableProperty] private string _episodes = "all";
    [ObservableProperty] private string _quality = "1080P";
    [ObservableProperty] private int _concurrent = 5;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "输入剧名搜索，或加载今日短剧。";

    partial void OnRootDirectoryChanged(string value) => NotifyCommands();
    partial void OnKeywordChanged(string value) => SearchCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    private bool CanSearch() => !IsBusy && !string.IsNullOrWhiteSpace(Keyword);
    private bool CanProcess() => !IsBusy && Directory.Exists(RootDirectory) && Results.Any(row => row.IsChecked);

    public void CheckAll(bool value)
    {
        foreach (var row in Results) row.IsChecked = value;
        NotifyCommands();
    }

    private Task SearchAsync() => RunBusyAsync(async cancellationToken =>
    {
        StatusMessage = $"正在搜索：{Keyword.Trim()}";
        ReplaceResults(await _searchService.SearchAsync(Keyword.Trim(), 1, cancellationToken));
        StatusMessage = $"搜索完成，共 {Results.Count} 条。";
    });

    private Task LoadTodayAsync() => RunBusyAsync(async cancellationToken =>
    {
        StatusMessage = "正在加载今日短剧…";
        ReplaceResults(await _searchService.GetTodayAsync(cancellationToken));
        StatusMessage = $"今日短剧加载完成，共 {Results.Count} 条。";
    });

    private Task ProcessCheckedAsync(bool download) => RunBusyAsync(async cancellationToken =>
    {
        var selected = Results.Where(row => row.IsChecked).ToArray();
        var importedDirectories = new List<string>();
        for (var index = 0; index < selected.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = selected[index];
            StatusMessage = $"[{index + 1}/{selected.Length}] 正在导入：{row.Title}";
            var bootstrap = await _bootstrapper.BootstrapAsync(new DramaProjectBootstrapRequest(
                RootDirectory,
                row.Drama,
                CompanyName: null,
                Episodes: NormalizeEpisodes(Episodes),
                Quality: Quality,
                Concurrent: Math.Clamp(Concurrent, 1, 16)), cancellationToken);
            importedDirectories.Add(bootstrap.SourceProjectDir);
            if (!download) continue;
            StatusMessage = $"[{index + 1}/{selected.Length}] 正在下载：{bootstrap.DisplayName}";
            var result = await _workService.RunProjectStepAsync(
                bootstrap.SourceProjectDir,
                null,
                "download",
                force: true,
                new Progress<WorkRunEvent>(item =>
                {
                    if (!string.IsNullOrWhiteSpace(item.Message)) StatusMessage = $"[{bootstrap.DisplayName}] {item.Message}";
                }),
                cancellationToken);
            if (!result.Ok) throw new InvalidOperationException(result.Message ?? $"下载失败：{bootstrap.DisplayName}");
        }
        await _mainViewModel.ImportLocalProjectDirectoriesAsync(importedDirectories);
        StatusMessage = download
            ? $"下载并加入视频号队列完成，共 {selected.Length} 部。"
            : $"导入并加入视频号队列完成，共 {selected.Length} 部。";
    });

    private async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try { await action(_cts.Token); }
        catch (OperationCanceledException) { StatusMessage = "操作已停止，可继续执行。"; }
        catch (Exception ex) { StatusMessage = $"操作失败：{ex.Message}"; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    private void ReplaceResults(IEnumerable<DramaSearchItem> items)
    {
        Results.Clear();
        foreach (var item in items)
        {
            var row = new DramaSearchRowViewModel(item);
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DramaSearchRowViewModel.IsChecked)) NotifyCommands();
            };
            Results.Add(row);
        }
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        SearchCommand.NotifyCanExecuteChanged();
        TodayCommand.NotifyCanExecuteChanged();
        ImportCheckedCommand.NotifyCanExecuteChanged();
        DownloadCheckedCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private static string NormalizeEpisodes(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? "all" : text;
    }
}
