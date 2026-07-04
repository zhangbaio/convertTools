using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class DramaSearchRowViewModel : ViewModelBase
{
    public DramaSearchItem Item { get; }

    public DramaSearchRowViewModel(DramaSearchItem item) => Item = item;

    public event Action? SelectionChanged;

    public int RowIndex { get; set; }

    public bool Selected
    {
        get => Item.Selected;
        set
        {
            if (Item.Selected == value) return;
            Item.Selected = value;
            OnPropertyChanged();
            SelectionChanged?.Invoke();
        }
    }

    public string Title => Item.Title;
    public string Author => string.IsNullOrWhiteSpace(Item.Author) ? "-" : Item.Author;
    public int EpisodeTotal => Item.EpisodeTotal;
    public string FavoriteText => Item.FavoriteCount > 0 ? Item.FavoriteCount.ToString() : "-";
    public string Category => string.IsNullOrWhiteSpace(Item.Category) ? "-" : Item.Category;
    public string PublishTime => string.IsNullOrWhiteSpace(Item.PublishTime) ? "-" : Item.PublishTime;
    public string Intro => Item.Intro;
    public string PosterUrl => Item.PosterUrl;
}

public sealed partial class DramaQueueRowViewModel : ViewModelBase
{
    public DramaDownloadQueueItem Item { get; }

    public DramaQueueRowViewModel(DramaDownloadQueueItem item) => Item = item;

    public void Refresh() => OnPropertyChanged(string.Empty);

    public string Title => Item.Title;
    public int EpisodeCount => ParseEpisodeCount(Item);
    public string Status => Item.Status;
    public string Progress => Item.Progress;
    public string Speed => Item.Speed;
    public string Detail => string.IsNullOrWhiteSpace(Item.StatusDetail) ? Item.LastError : Item.StatusDetail;
    public string ProjectDir => Item.ProjectDir;

    private static int ParseEpisodeCount(DramaDownloadQueueItem item)
    {
        if (string.Equals(item.Episodes, "all", StringComparison.OrdinalIgnoreCase))
            return Math.Max(0, item.EpisodeTotal);
        return item.Episodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }
}

public sealed partial class DramaDownloadViewModel : ViewModelBase
{
    private readonly DramaDownloadRunner _runner = new();
    private readonly List<DramaSearchItem> _allSearchResults = new();
    private CancellationTokenSource? _downloadCts;

    public ObservableCollection<DramaSearchRowViewModel> SearchResults { get; } = new();
    public ObservableCollection<DramaQueueRowViewModel> QueueRows { get; } = new();

    public IReadOnlyList<string> SearchViewModeOptions { get; } = ["列表视图", "封面视图"];
    public IReadOnlyList<string> EpisodeRangeOptions { get; } = ["全部", "前1集", "前3集", "前5集", "自定义"];

    [ObservableProperty] private string _downloadWorkspace = "";
    [ObservableProperty] private string _searchKeyword = "";
    [ObservableProperty] private int _searchPage = 1;
    [ObservableProperty] private bool _exactSearch;
    [ObservableProperty] private int _queryDays = 1;
    [ObservableProperty] private int _minEpisodeFilter;
    [ObservableProperty] private int _maxEpisodeFilter;
    [ObservableProperty] private string _episodeRangeMode = "全部";
    [ObservableProperty] private string _episodeCustomRange = "";
    [ObservableProperty] private string _searchViewMode = "列表视图";
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
    [ObservableProperty] private string _searchPageText = "第 1 页";
    [ObservableProperty] private string _queueStatsText = "待下载：0 | 下载中：0 | 完成：0 | 失败：0";

    public bool IsListView => !string.Equals(SearchViewMode, "封面视图", StringComparison.Ordinal);
    public bool IsPosterView => !IsListView;

    public event Action<string>? LogRequested;
    public event Action<IReadOnlyList<string>>? ImportToQueueRequested;
    public event Func<string>? UploadWorkspaceRequested;

    public void LoadState()
    {
        var state = DramaDownloadQueueStore.Load();
        DownloadWorkspace = state.WorkspacePath;
        AutoGenerateMaterials = state.AutoGenerateMaterials;
        DownloadConcurrent = state.DownloadConcurrent;
        EpisodeNumberMode = state.DownloadEpisodeNumberMode;
        DefaultQuality = state.DefaultQuality;
        CategoryInclude = state.CategoryInclude;
        CategoryExclude = state.CategoryExclude;
        AuthorExclude = state.AuthorExclude;

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
            CategoryInclude = CategoryInclude,
            CategoryExclude = CategoryExclude,
            AuthorExclude = AuthorExclude,
            QueueItems = QueueRows.Select(r => r.Item).ToList(),
        };
        DramaDownloadQueueStore.Save(state);
    }

    partial void OnDownloadConcurrentChanged(int value) => SaveState();
    partial void OnAutoGenerateMaterialsChanged(bool value) => SaveState();
    partial void OnEpisodeNumberModeChanged(string value) => SaveState();
    partial void OnDefaultQualityChanged(string value) => SaveState();
    partial void OnSearchViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(IsPosterView));
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (IsSearching) return;
        var keyword = SearchKeyword.Trim();
        if (string.IsNullOrWhiteSpace(keyword) && !HasSearchFilter())
        {
            LogRequested?.Invoke("请输入搜索关键词，或填写分类/作者筛选后搜索");
            return;
        }

        await LoadSearchResultsAsync(
            string.IsNullOrWhiteSpace(keyword) ? "分类筛选上新" : $"第 {SearchPage} 页",
            async ct =>
            {
                if (string.IsNullOrWhiteSpace(keyword))
                    return await LoadMergedNewReleaseAsync(ct);
                return await ShortDramaDramaServices.SearchAsync(keyword, SearchPage, ct);
            },
            sourceMode: "");
    }

    [RelayCommand]
    private async Task LoadTodayAsync()
    {
        SearchPage = 1;
        await LoadSearchResultsAsync("今日上新", ShortDramaDramaServices.GetTodayAsync, sourceMode: "today");
    }

    [RelayCommand]
    private async Task LoadMangaTodayAsync()
    {
        SearchPage = 1;
        await LoadSearchResultsAsync(
            $"漫剧上新 · {Math.Clamp(QueryDays, 1, 30)} 天",
            ct => ShortDramaDramaServices.GetMangaTodayAsync(Math.Clamp(QueryDays, 1, 30), ct),
            sourceMode: "mj_today");
    }

    [RelayCommand]
    private async Task LoadAiTodayAsync()
    {
        SearchPage = 1;
        await LoadSearchResultsAsync(
            $"AI短剧上新 · {Math.Clamp(QueryDays, 1, 30)} 天",
            ct => ShortDramaDramaServices.GetAiTodayAsync(Math.Clamp(QueryDays, 1, 30), ct),
            sourceMode: "aiju_today");
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        SearchPage = 1;
        await LoadSearchResultsAsync(
            $"历史上新 · {Math.Clamp(QueryDays, 1, 30)} 天",
            ct => ShortDramaDramaServices.GetHistoryAsync(Math.Clamp(QueryDays, 1, 30), ct),
            sourceMode: "history");
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (SearchPage <= 1) return;
        SearchPage -= 1;
        await SearchAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        SearchPage += 1;
        await SearchAsync();
    }

    [RelayCommand]
    private void ApplyFilters()
    {
        SaveState();
        ApplyFilteredSearchResults("已筛选");
    }

    [RelayCommand]
    private void SelectAllResults()
    {
        foreach (var row in SearchResults)
            row.Selected = true;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in SearchResults)
            row.Selected = false;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var selected = SelectedSearchItems();
        if (selected.Count == 0)
        {
            LogRequested?.Invoke("请先勾选要下载的短剧");
            return;
        }

        var added = await AddItemsToDownloadQueueAsync(selected, generateMaterials: false);
        if (added <= 0) return;
        await StartDownloadQueueAsync();
    }

    [RelayCommand]
    private async Task AddSelectedToTikTokQueueAsync()
    {
        var selected = SelectedSearchItems();
        if (selected.Count == 0)
        {
            LogRequested?.Invoke("请先勾选要加入 TikTok 队列的短剧");
            return;
        }

        selected = FilterAuthorExcludedItems(selected, "TikTok 队列");
        if (selected.Count == 0) return;

        var uploadWorkspace = UploadWorkspaceRequested?.Invoke()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(uploadWorkspace))
        {
            LogRequested?.Invoke("请先在 TikTok 上传页面选择工作目录");
            return;
        }

        if (!Directory.Exists(uploadWorkspace))
        {
            LogRequested?.Invoke($"TikTok 上传工作目录不存在：{uploadWorkspace}");
            return;
        }

        var episodes = ResolveEpisodeSelection();
        if (!IsValidEpisodeSelection(episodes))
        {
            LogRequested?.Invoke("集数范围格式不正确，请输入类似 68、1-5 或 1,3,5 的格式");
            return;
        }

        var dirs = new List<string>();
        foreach (var item in selected)
        {
            var queueEntryDramaType = ResolveQueueEntryDramaType(item);
            var projectDir = await ShortDramaDramaServices.BootstrapAsync(
                uploadWorkspace,
                item,
                episodes,
                DefaultQuality,
                DownloadConcurrent,
                EpisodeNumberMode,
                queueEntryDramaType,
                CancellationToken.None);
            dirs.Add(projectDir);
            item.Selected = false;
            LogRequested?.Invoke($"已准备 TikTok 队列项目：{item.Title}");
        }

        UpdateSelectedCount();
        ImportToQueueRequested?.Invoke(dirs);
        LogRequested?.Invoke($"已加入 {dirs.Count} 个剧目到 TikTok 队列");
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
                _ => RefreshQueueRows(),
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
            .Where(r => IsCompletedStatus(r.Item.Status))
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
        var done = QueueRows.Where(r => IsCompletedStatus(r.Item.Status)).ToList();
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

    [RelayCommand]
    private void OpenTodayDownloadFolder()
    {
        var root = ResolveExistingFolder(DownloadWorkspace, "下载目录");
        if (root is null) return;

        var todayFolder = ResolveExistingTodayFolder(root);
        if (todayFolder is not null)
        {
            OpenFolder(todayFolder, "今日文件夹");
            return;
        }

        if (OpenFolder(root, "下载目录"))
            LogRequested?.Invoke("未找到单独的今日文件夹，已打开下载目录");
    }

    public void OpenQueueProjectFolder(DramaQueueRowViewModel? row)
    {
        if (row is null)
        {
            LogRequested?.Invoke("请先点击下载队列里的剧名");
            return;
        }

        OpenFolder(row.ProjectDir, "剧集目录");
    }

    public void UpdateSelectedCount()
    {
        var n = SearchResults.Count(r => r.Selected);
        SelectedCountText = $"已选 {n} 项";
    }

    private async Task LoadSearchResultsAsync(
        string label,
        Func<CancellationToken, Task<IReadOnlyList<DramaSearchItem>>> loader,
        string sourceMode)
    {
        if (IsSearching) return;
        IsSearching = true;
        SearchPageText = $"{label} · 加载中...";
        try
        {
            var items = await loader(CancellationToken.None);
            _allSearchResults.Clear();
            foreach (var item in items)
            {
                item.Selected = false;
                item.SourceMode = sourceMode;
                _allSearchResults.Add(item);
            }

            ApplyFilteredSearchResults(label);
            LogRequested?.Invoke($"{label}：{SearchResults.Count} 条");
        }
        catch (Exception ex)
        {
            SearchPageText = $"{label} · 加载失败";
            LogRequested?.Invoke($"{label}失败：{ex.Message}");
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task<IReadOnlyList<DramaSearchItem>> LoadMergedNewReleaseAsync(CancellationToken ct)
    {
        var merged = new List<DramaSearchItem>();
        merged.AddRange(await ShortDramaDramaServices.GetTodayAsync(ct));
        merged.AddRange(await ShortDramaDramaServices.GetMangaTodayAsync(Math.Clamp(QueryDays, 1, 30), ct));
        merged.AddRange(await ShortDramaDramaServices.GetAiTodayAsync(Math.Clamp(QueryDays, 1, 30), ct));
        return merged
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .GroupBy(item => item.BookId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private void ApplyFilteredSearchResults(string? label = null)
    {
        var items = ApplySearchFilters(_allSearchResults);
        SearchResults.Clear();
        var index = 1;
        foreach (var item in items)
        {
            var row = new DramaSearchRowViewModel(item) { RowIndex = index++ };
            row.SelectionChanged += UpdateSelectedCount;
            SearchResults.Add(row);
        }

        SearchPageText = $"{(string.IsNullOrWhiteSpace(label) ? $"第 {SearchPage} 页" : label)} · 共 {SearchResults.Count} 条";
        UpdateSelectedCount();
    }

    private IReadOnlyList<DramaSearchItem> ApplySearchFilters(IEnumerable<DramaSearchItem> source)
    {
        var keyword = SearchKeyword.Trim();
        var includeCategories = SplitKeywords(CategoryInclude);
        var excludeCategories = SplitKeywords(CategoryExclude);
        var excludeAuthors = SplitKeywords(AuthorExclude);
        var minEpisodes = Math.Max(0, MinEpisodeFilter);
        var maxEpisodes = Math.Max(0, MaxEpisodeFilter);

        return source.Where(item =>
            {
                if (ExactSearch && !string.IsNullOrWhiteSpace(keyword) &&
                    !string.Equals(item.Title.Trim(), keyword, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (minEpisodes > 0 && item.EpisodeTotal < minEpisodes)
                    return false;

                if (maxEpisodes > 0 && item.EpisodeTotal > maxEpisodes)
                    return false;

                if (includeCategories.Count > 0 && !includeCategories.Any(token => ContainsToken(item.Category, token) || ContainsToken(item.Title, token)))
                    return false;

                if (excludeCategories.Any(token => ContainsToken(item.Category, token) || ContainsToken(item.Title, token)))
                    return false;

                if (excludeAuthors.Any(token => ContainsToken(item.Author, token)))
                    return false;

                return true;
            })
            .ToArray();
    }

    private async Task<int> AddItemsToDownloadQueueAsync(IReadOnlyList<DramaSearchItem> selected, bool generateMaterials)
    {
        if (string.IsNullOrWhiteSpace(DownloadWorkspace))
        {
            LogRequested?.Invoke("请先选择下载目录");
            return 0;
        }

        if (!Directory.Exists(DownloadWorkspace))
        {
            LogRequested?.Invoke($"下载目录不存在：{DownloadWorkspace}");
            return 0;
        }

        var episodes = ResolveEpisodeSelection();
        if (!IsValidEpisodeSelection(episodes))
        {
            LogRequested?.Invoke("集数范围格式不正确，请输入类似 68、1-5 或 1,3,5 的格式");
            return 0;
        }

        var added = 0;
        foreach (var item in selected)
        {
            var queueEntryDramaType = ResolveQueueEntryDramaType(item);
            var projectDir = await ShortDramaDramaServices.BootstrapAsync(
                DownloadWorkspace,
                item,
                episodes,
                DefaultQuality,
                DownloadConcurrent,
                EpisodeNumberMode,
                queueEntryDramaType,
                CancellationToken.None);

            var queueItem = new DramaDownloadQueueItem
            {
                Title = item.Title,
                BookId = item.BookId,
                ProjectDir = projectDir,
                Episodes = episodes,
                Quality = DefaultQuality,
                EpisodeNumberMode = EpisodeNumberMode,
                GenerateMaterials = generateMaterials,
                Status = "待下载",
                Progress = "0%",
                Speed = "0 KB/s",
                QueueEntrySource = "download_queue",
                QueueEntryDramaType = queueEntryDramaType,
                SourceMode = item.SourceMode,
                Author = item.Author,
                Category = item.Category,
                EpisodeTotal = item.EpisodeTotal,
                FavoriteCount = item.FavoriteCount,
                PublishTime = item.PublishTime,
                PosterUrl = item.PosterUrl,
            };

            var existing = QueueRows.FirstOrDefault(row =>
                string.Equals(Path.GetFullPath(row.Item.ProjectDir), Path.GetFullPath(projectDir), StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                QueueRows.Add(new DramaQueueRowViewModel(queueItem));
                LogRequested?.Invoke($"已加入下载队列：{item.Title}");
            }
            else
            {
                CopyQueueItem(queueItem, existing.Item);
                existing.Refresh();
                LogRequested?.Invoke($"已更新下载队列：{item.Title}（集数 {episodes}）");
            }

            item.Selected = false;
            added++;
        }

        UpdateSelectedCount();
        SaveState();
        RefreshQueueStats();
        return added;
    }

    private List<DramaSearchItem> SelectedSearchItems() =>
        SearchResults.Where(r => r.Selected).Select(r => r.Item).ToList();

    private List<DramaSearchItem> FilterAuthorExcludedItems(List<DramaSearchItem> selectedItems, string queueLabel)
    {
        var excluded = SplitKeywords(AuthorExclude);
        if (excluded.Count == 0) return selectedItems;

        var kept = new List<DramaSearchItem>();
        var skipped = new List<DramaSearchItem>();
        foreach (var item in selectedItems)
        {
            if (excluded.Any(token => ContainsToken(item.Author, token)))
                skipped.Add(item);
            else
                kept.Add(item);
        }

        if (skipped.Count > 0)
        {
            var preview = string.Join("、", skipped.Take(5).Select(item => $"{item.Title}({item.Author})"));
            var suffix = skipped.Count > 5 ? $" 等共 {skipped.Count} 个" : "";
            LogRequested?.Invoke($"已按作者排除跳过：{preview}{suffix}");
        }

        if (kept.Count == 0)
            LogRequested?.Invoke($"所选剧集均命中作者排除，未加入{queueLabel}");
        return kept;
    }

    private string ResolveEpisodeSelection()
    {
        return EpisodeRangeMode switch
        {
            "前1集" => "1",
            "前3集" => "1-3",
            "前5集" => "1-5",
            "自定义" => EpisodeCustomRange.Trim(),
            _ => string.IsNullOrWhiteSpace(EpisodeCustomRange) ? "all" : EpisodeCustomRange.Trim(),
        };
    }

    private static bool IsValidEpisodeSelection(string selection)
    {
        if (string.IsNullOrWhiteSpace(selection) ||
            string.Equals(selection.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var part in selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-', StringComparison.Ordinal))
            {
                var parts = part.Split('-', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out var start) || !int.TryParse(parts[1], out var end) || start <= 0 || end <= 0)
                    return false;
            }
            else if (!int.TryParse(part, out var value) || value <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private string ResolveQueueEntryDramaType(DramaSearchItem item)
    {
        var source = (item.SourceMode ?? "").Trim().ToLowerInvariant();
        if (source == "mj_today") return "mj";
        if (source == "aiju_today") return "aiju";
        if (ContainsToken(item.Category, "漫剧")) return "mj";
        if (ContainsToken(item.Category, "AI") || ContainsToken(item.Title, "AI")) return "aiju";
        return "";
    }

    private bool HasSearchFilter() =>
        SplitKeywords(CategoryInclude).Count > 0 ||
        SplitKeywords(CategoryExclude).Count > 0 ||
        SplitKeywords(AuthorExclude).Count > 0;

    private void RefreshQueueRows()
    {
        foreach (var row in QueueRows) row.Refresh();
        RefreshQueueStats();
        SaveState();
    }

    private void RefreshQueueStats()
    {
        var pending = QueueRows.Count(r => r.Item.Status == "待下载");
        var running = QueueRows.Count(r => r.Item.Status is "下载中" or "解析链接中" or "校验文件" or "已下载" or "生成派生产物中");
        var done = QueueRows.Count(r => IsCompletedStatus(r.Item.Status));
        var failed = QueueRows.Count(r => r.Item.Status is "失败" or "素材校验失败");
        QueueStatsText = $"待下载：{pending} | 下载中：{running} | 完成：{done} | 失败：{failed}";
    }

    private static bool IsCompletedStatus(string status) => status is "完成" or "已完成";

    private static IReadOnlyList<string> SplitKeywords(string value) =>
        value.Split(['\r', '\n', ',', '，', ';', '；', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool ContainsToken(string? value, string token) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.IsNullOrWhiteSpace(token) &&
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private string? ResolveExistingFolder(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            LogRequested?.Invoke($"未找到{label}");
            return null;
        }

        try
        {
            var folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
            if (File.Exists(folder))
                folder = Path.GetDirectoryName(folder) ?? folder;

            if (!Directory.Exists(folder))
            {
                LogRequested?.Invoke($"{label}不存在：{folder}");
                return null;
            }

            return folder;
        }
        catch (Exception ex)
        {
            LogRequested?.Invoke($"解析{label}失败：{ex.Message}");
            return null;
        }
    }

    private bool OpenFolder(string? path, string label)
    {
        var folder = ResolveExistingFolder(path, label);
        if (folder is null) return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
            LogRequested?.Invoke($"已打开{label}：{folder}");
            return true;
        }
        catch (Exception ex)
        {
            LogRequested?.Invoke($"打开{label}失败：{ex.Message}");
            return false;
        }
    }

    private static string? ResolveExistingTodayFolder(string root)
    {
        var today = DateTime.Today;
        var names = new[]
        {
            today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            today.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            today.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture),
            today.ToString("yyyy_MM_dd", CultureInfo.InvariantCulture),
            today.ToString("MM-dd", CultureInfo.InvariantCulture),
            $"{today.Month}月{today.Day}日",
            $"{today.Year}年{today.Month}月{today.Day}日",
        };

        foreach (var name in names)
        {
            var candidate = Path.Combine(root, name);
            if (Directory.Exists(candidate))
                return candidate;
        }

        try
        {
            var todayDirs = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Where(dir => !string.Equals(Path.GetFileName(dir), "workflow", StringComparison.OrdinalIgnoreCase))
                .Select(dir => new DirectoryInfo(dir))
                .Where(info => info.Exists && (info.CreationTime.Date == today || info.LastWriteTime.Date == today))
                .OrderByDescending(info => info.LastWriteTime)
                .Take(2)
                .ToArray();

            return todayDirs.Length == 1 ? todayDirs[0].FullName : null;
        }
        catch
        {
            return null;
        }
    }

    private static void CopyQueueItem(DramaDownloadQueueItem source, DramaDownloadQueueItem target)
    {
        target.Title = source.Title;
        target.BookId = source.BookId;
        target.ProjectDir = source.ProjectDir;
        target.Episodes = source.Episodes;
        target.Quality = source.Quality;
        target.EpisodeNumberMode = source.EpisodeNumberMode;
        target.Status = source.Status;
        target.Progress = source.Progress;
        target.Speed = source.Speed;
        target.StatusDetail = source.StatusDetail;
        target.GenerateMaterials = source.GenerateMaterials;
        target.LastError = source.LastError;
        target.CompletedAt = source.CompletedAt;
        target.UpdatedAt = source.UpdatedAt;
        target.QueueEntrySource = source.QueueEntrySource;
        target.QueueEntryDramaType = source.QueueEntryDramaType;
        target.SourceMode = source.SourceMode;
        target.Author = source.Author;
        target.Category = source.Category;
        target.EpisodeTotal = source.EpisodeTotal;
        target.FavoriteCount = source.FavoriteCount;
        target.PublishTime = source.PublishTime;
        target.PosterUrl = source.PosterUrl;
    }
}
