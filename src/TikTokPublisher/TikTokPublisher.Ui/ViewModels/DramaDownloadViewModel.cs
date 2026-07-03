using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class DramaSearchRowViewModel : ViewModelBase
{
    public DramaSearchItem Item { get; }

    public DramaSearchRowViewModel(DramaSearchItem item) => Item = item;

    public int RowIndex { get; set; }

    public bool Selected
    {
        get => Item.Selected;
        set
        {
            if (Item.Selected == value) return;
            Item.Selected = value;
            OnPropertyChanged();
        }
    }

    public string Title => Item.Title;
    public string Author => Item.Author;
    public int EpisodeTotal => Item.EpisodeTotal;
    public string FavoriteText => Item.FavoriteCount > 0 ? Item.FavoriteCount.ToString() : "-";
    public string Category => Item.Category;
    public string PublishTime => Item.PublishTime;
    public string Intro => Item.Intro;
}

public sealed partial class DramaQueueRowViewModel : ViewModelBase
{
    public DramaDownloadQueueItem Item { get; }

    public DramaQueueRowViewModel(DramaDownloadQueueItem item) => Item = item;

    public void Refresh() => OnPropertyChanged(string.Empty);

    public string Title => Item.Title;
    public int EpisodeCount => ParseEpisodeCount(Item.Episodes);
    public string Status => Item.Status;
    public string Progress => Item.Progress;
    public string Speed => Item.Speed;
    public string Detail => string.IsNullOrWhiteSpace(Item.StatusDetail) ? Item.LastError : Item.StatusDetail;

    private static int ParseEpisodeCount(string episodes)
    {
        if (string.Equals(episodes, "all", StringComparison.OrdinalIgnoreCase)) return 0;
        return episodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }
}

public sealed partial class DramaDownloadViewModel : ViewModelBase
{
    private readonly DramaDownloadRunner _runner = new();
    private CancellationTokenSource? _downloadCts;

    public ObservableCollection<DramaSearchRowViewModel> SearchResults { get; } = new();
    public ObservableCollection<DramaQueueRowViewModel> QueueRows { get; } = new();

    [ObservableProperty] private string _downloadWorkspace = "";
    [ObservableProperty] private string _searchKeyword = "";
    [ObservableProperty] private bool _exactSearch;
    [ObservableProperty] private int _queryDays = 1;
    [ObservableProperty] private string _episodeSelection = "all";
    [ObservableProperty] private int _downloadConcurrent = 3;
    [ObservableProperty] private bool _autoGenerateMaterials = true;
    [ObservableProperty] private string _defaultQuality = "1080P";
    [ObservableProperty] private string _episodeNumberMode = "source";
    [ObservableProperty] private string _categoryInclude = "";
    [ObservableProperty] private string _categoryExclude = "";
    [ObservableProperty] private string _authorExclude = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private string _selectedCountText = "已选 0 项";
    [ObservableProperty] private string _queueStatsText = "待下载：0 | 下载中：0 | 已完成：0 | 失败：0";

    public event Action<string>? LogRequested;
    public event Action<IReadOnlyList<string>>? ImportToQueueRequested;

    public void LoadState()
    {
        var state = DramaDownloadQueueStore.Load();
        DownloadWorkspace = state.WorkspacePath;
        AutoGenerateMaterials = state.AutoGenerateMaterials;
        DownloadConcurrent = state.DownloadConcurrent;
        EpisodeNumberMode = state.DownloadEpisodeNumberMode;
        DefaultQuality = state.DefaultQuality;
        QueueRows.Clear();
        foreach (var item in state.QueueItems)
            QueueRows.Add(new DramaQueueRowViewModel(item));
        RefreshQueueStats();

        var clientSettings = ClientSettingsStore.Load();
        ApplyClientSettings(clientSettings, preferSavedWorkspace: string.IsNullOrWhiteSpace(DownloadWorkspace));
    }

    public void ApplyClientSettings(ClientSettings settings, bool preferSavedWorkspace = false)
    {
        DefaultQuality = string.IsNullOrWhiteSpace(settings.DramaDownloadDefaultQuality)
            ? DefaultQuality
            : settings.DramaDownloadDefaultQuality;
        DownloadConcurrent = settings.DramaDownloadConcurrent > 0
            ? settings.DramaDownloadConcurrent
            : DownloadConcurrent;
        if (preferSavedWorkspace && !string.IsNullOrWhiteSpace(settings.LastDownloadWorkspace))
        {
            DownloadWorkspace = settings.LastDownloadWorkspace;
        }
        ShortDramaDramaServices.RefreshSettings(settings);
    }

    public void SaveState()
    {
        var state = new DramaDownloadQueueState
        {
            WorkspacePath = DownloadWorkspace,
            AutoGenerateMaterials = AutoGenerateMaterials,
            DownloadConcurrent = DownloadConcurrent,
            DownloadEpisodeNumberMode = EpisodeNumberMode,
            DefaultQuality = DefaultQuality,
            QueueItems = QueueRows.Select(r => r.Item).ToList(),
        };
        DramaDownloadQueueStore.Save(state);
    }

    partial void OnDownloadConcurrentChanged(int value) => SaveState();
    partial void OnAutoGenerateMaterialsChanged(bool value) => SaveState();
    partial void OnEpisodeNumberModeChanged(string value) => SaveState();
    partial void OnDefaultQualityChanged(string value) => SaveState();

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (IsSearching) return;
        IsSearching = true;
        try
        {
            SearchResults.Clear();
            LogRequested?.Invoke($"搜索短剧：{SearchKeyword}");
            var items = await ShortDramaDramaServices.SearchAsync(SearchKeyword.Trim(), 1, CancellationToken.None);
            if (ExactSearch)
            {
                var key = SearchKeyword.Trim();
                items = items.Where(i => string.Equals(i.Title, key, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            var index = 1;
            foreach (var item in items)
                SearchResults.Add(new DramaSearchRowViewModel(item) { RowIndex = index++ });
            UpdateSelectedCount();
            LogRequested?.Invoke($"搜索完成：{SearchResults.Count} 条结果");
        }
        catch (Exception ex)
        {
            LogRequested?.Invoke($"搜索失败：{ex.Message}");
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task LoadTodayAsync()
    {
        if (IsSearching) return;
        IsSearching = true;
        try
        {
            SearchResults.Clear();
            LogRequested?.Invoke("加载今日上新…");
            var items = await ShortDramaDramaServices.GetTodayAsync(CancellationToken.None);
            var index = 1;
            foreach (var item in items)
                SearchResults.Add(new DramaSearchRowViewModel(item) { RowIndex = index++ });
            UpdateSelectedCount();
            LogRequested?.Invoke($"今日上新：{SearchResults.Count} 条");
        }
        catch (Exception ex)
        {
            LogRequested?.Invoke($"加载今日上新失败：{ex.Message}");
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void DownloadSelected()
    {
        var selected = SearchResults.Where(r => r.Selected).Select(r => r.Item).ToList();
        if (selected.Count == 0)
        {
            LogRequested?.Invoke("请先勾选要下载的短剧");
            return;
        }
        if (string.IsNullOrWhiteSpace(DownloadWorkspace))
        {
            LogRequested?.Invoke("请先选择下载目录");
            return;
        }

        foreach (var item in selected)
        {
            var safeTitle = SanitizeDirName(item.Title);
            var projectDir = Path.Combine(DownloadWorkspace, safeTitle);
            QueueRows.Add(new DramaQueueRowViewModel(new DramaDownloadQueueItem
            {
                Title = item.Title,
                BookId = item.BookId,
                ProjectDir = projectDir,
                Episodes = EpisodeSelection,
                Quality = DefaultQuality,
                EpisodeNumberMode = EpisodeNumberMode,
                GenerateMaterials = AutoGenerateMaterials,
                Status = "待下载",
            }));
            item.Selected = false;
        }
        UpdateSelectedCount();
        SaveState();
        RefreshQueueStats();
        LogRequested?.Invoke($"已加入下载队列 {selected.Count} 个项目");
    }

    [RelayCommand]
    private async Task StartDownloadQueueAsync()
    {
        if (IsDownloading) return;
        if (QueueRows.Count == 0)
        {
            LogRequested?.Invoke("下载队列为空");
            return;
        }

        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();
        try
        {
            await _runner.RunQueueAsync(
                QueueRows.Select(r => r.Item).ToList(),
                DownloadConcurrent,
                (item, msg) => LogRequested?.Invoke(msg),
                _ => RefreshQueueRow(),
                _downloadCts.Token);
            SaveState();
            RefreshQueueStats();
        }
        catch (OperationCanceledException)
        {
            LogRequested?.Invoke("下载队列已停止");
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void StopDownloadQueue() => _downloadCts?.Cancel();

    [RelayCommand]
    private void ImportCompletedToQueue()
    {
        var dirs = QueueRows
            .Where(r => string.Equals(r.Item.Status, "已完成", StringComparison.Ordinal))
            .Select(r => r.Item.ProjectDir)
            .Where(dir => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (dirs.Length == 0)
        {
            LogRequested?.Invoke("没有已完成下载的项目可导入");
            return;
        }

        ImportToQueueRequested?.Invoke(dirs);
    }

    [RelayCommand]
    private void ClearCompletedQueue()
    {
        var done = QueueRows.Where(r => r.Item.Status == "已完成").ToList();
        foreach (var row in done) QueueRows.Remove(row);
        SaveState();
        RefreshQueueStats();
    }

    [RelayCommand]
    private void ClearAllQueue()
    {
        QueueRows.Clear();
        SaveState();
        RefreshQueueStats();
    }

    public void UpdateSelectedCount()
    {
        var n = SearchResults.Count(r => r.Selected);
        SelectedCountText = $"已选 {n} 项";
    }

    private void RefreshQueueRow()
    {
        foreach (var row in QueueRows) row.Refresh();
        RefreshQueueStats();
        SaveState();
    }

    private void RefreshQueueStats()
    {
        var pending = QueueRows.Count(r => r.Item.Status == "待下载");
        var running = QueueRows.Count(r => r.Item.Status == "下载中");
        var done = QueueRows.Count(r => r.Item.Status == "已完成");
        var failed = QueueRows.Count(r => r.Item.Status == "失败");
        QueueStatsText = $"待下载：{pending} | 下载中：{running} | 已完成：{done} | 失败：{failed}";
    }

    private static string SanitizeDirName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "untitled" : cleaned;
    }
}
