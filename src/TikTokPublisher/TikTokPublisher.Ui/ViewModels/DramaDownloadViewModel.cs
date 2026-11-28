using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services;

namespace TikTokPublisher.Ui.ViewModels;

public static class DramaQueuePlatforms
{
    public const string TikTok = "tiktok";
    public const string Kuaishou = "kuaishou";
    public const string KuaishouPersonal = "kuaishou-personal";
    public const string KuaishouEnterprise = "kuaishou-enterprise";
    public const string Weixin = "weixin";
}

public sealed record TikTokQueueImportTarget(
    string AccountProfileId,
    string AccountProfileName,
    string WorkspaceRoot,
    string PlatformKey = "",
    string PlatformLabel = "")
{
    public string LoginEmail { get; init; } = "";
    internal string FallbackWorkspaceRoot { get; init; } = "";
    internal string WorkspaceIssue { get; init; } = "";
    internal string Label => string.IsNullOrWhiteSpace(PlatformLabel)
        ? AccountProfileName : $"{PlatformLabel} · {AccountProfileName}";
}

public sealed partial class DramaPlatformQueueAction : ObservableObject
{
    public DramaPlatformQueueAction(string key, string platformLabel, string label, IAsyncRelayCommand addCommand)
    {
        Key = key;
        PlatformLabel = platformLabel;
        Label = label;
        AddCommand = addCommand;
    }

    public string Key { get; }
    public string PlatformLabel { get; }
    public string Label { get; }
    public IAsyncRelayCommand AddCommand { get; }
}

public sealed record TikTokQueueImportRequest(
    TikTokQueueImportTarget Target,
    IReadOnlyList<string> ProjectDirs)
{
    internal bool IsBatch { get; init; }
}

internal sealed class QueueImportPartialException(IEnumerable<string> importedPaths)
    : InvalidOperationException("部分项目未能写入目标队列，请检查项目目录。")
{
    public HashSet<string> ImportedPaths { get; } = new(importedPaths, StringComparer.OrdinalIgnoreCase);
}

public sealed partial class DramaSearchRowViewModel : ViewModelBase
{
    public DramaSearchItem Item { get; private set; }

    public DramaSearchRowViewModel(DramaSearchItem item) => Item = item;

    public event Action? SelectionChanged;

    [ObservableProperty] private int _rowIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPosterImage))]
    [NotifyPropertyChangedFor(nameof(ShowPosterPlaceholder))]
    private Bitmap? _posterImage;

    [ObservableProperty] private string _posterStatus = "封面加载中";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExistenceBrush))]
    private string _existenceText = "核对中";

    private static readonly IBrush ExistencePresentBrush = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush ExistenceAbsentBrush = new SolidColorBrush(Color.Parse("#15803D"));
    private static readonly IBrush ExistenceUnknownBrush = new SolidColorBrush(Color.Parse("#6B7280"));

    public IBrush ExistenceBrush => DramaDownloadExistenceResult.ToneFor(ExistenceText) switch
    {
        DramaExistenceTone.Present => ExistencePresentBrush,
        DramaExistenceTone.Absent => ExistenceAbsentBrush,
        _ => ExistenceUnknownBrush,
    };

    public bool HasPosterImage => PosterImage is not null;
    public bool ShowPosterPlaceholder => PosterImage is null;

    private CancellationTokenSource? _posterCts;
    private string? _loadedPosterUrl;
    private string? _requestedPosterUrl;

    public bool Selected
    {
        get => Item.Selected;
        set
        {
            if (Item.Selected == value)
            {
                // 底层数据可能已被队列操作修改，仍需同步复选框。
                OnPropertyChanged();
                return;
            }
            Item.Selected = value;
            OnPropertyChanged();
            SelectionChanged?.Invoke();
        }
    }

    public string Title => Item.Title;
    public string Author => string.IsNullOrWhiteSpace(Item.Author) ? "-" : Item.Author;
    public int EpisodeTotal => Item.EpisodeTotal;
    public string FavoriteText => FormatCount(Item.FavoriteCount);
    public string PlayCountText => FormatCount(Item.PlayCount);
    public string Category => string.IsNullOrWhiteSpace(Item.Category) ? "-" : Item.Category;
    public string PublishTime => FormatPublishTime(Item.PublishTime);
    public string Intro => Item.Intro;
    public string IntroText => string.IsNullOrWhiteSpace(Intro) ? "暂无简介" : Intro.Trim();
    public string PosterUrl => Item.PosterUrl;

    public void UpdateItem(DramaSearchItem item)
    {
        var oldPosterUrl = (Item.PosterUrl ?? string.Empty).Trim();
        var newPosterUrl = (item.PosterUrl ?? string.Empty).Trim();
        var titleChanged = !string.Equals(Item.Title, item.Title, StringComparison.Ordinal);
        Item = item;
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Author));
        OnPropertyChanged(nameof(EpisodeTotal));
        OnPropertyChanged(nameof(FavoriteText));
        OnPropertyChanged(nameof(PlayCountText));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(PublishTime));
        OnPropertyChanged(nameof(Intro));
        OnPropertyChanged(nameof(IntroText));
        OnPropertyChanged(nameof(PosterUrl));
        if (titleChanged)
            ExistenceText = "核对中";

        if (!string.Equals(oldPosterUrl, newPosterUrl, StringComparison.Ordinal))
            ResetPosterState();
    }

    public void RequestPosterLoad()
    {
        var url = (Item.PosterUrl ?? string.Empty).Trim();
        if (HasPosterImage && string.Equals(_loadedPosterUrl, url, StringComparison.Ordinal))
        {
            return;
        }
        if (_posterCts is { IsCancellationRequested: false } &&
            string.Equals(_requestedPosterUrl, url, StringComparison.Ordinal))
        {
            return;
        }

        _posterCts?.Cancel();
        _posterCts?.Dispose();
        var request = new CancellationTokenSource();
        _posterCts = request;
        _requestedPosterUrl = url;
        _ = LoadPosterAsync(url, request);
    }

    private static string FormatCount(int value)
    {
        if (value <= 0)
            return "-";
        if (value >= 100_000_000)
            return (value / 100_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "亿";
        if (value >= 10_000)
            return (value / 10_000d).ToString("0.#", CultureInfo.InvariantCulture) + "万";
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatPublishTime(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
            return "-";
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) &&
            !DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed))
            return text;
        return parsed.TimeOfDay == TimeSpan.Zero
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : parsed.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public void DisposePoster()
    {
        _posterCts?.Cancel();
        _posterCts?.Dispose();
        _posterCts = null;
        _requestedPosterUrl = null;
        _loadedPosterUrl = null;
        PosterImage?.Dispose();
        PosterImage = null;
        PosterStatus = "封面加载中";
    }

    private void ResetPosterState()
    {
        DisposePoster();
    }

    private async Task LoadPosterAsync(string url, CancellationTokenSource request)
    {
        var cancellationToken = request.Token;
        try
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                await SetPosterStatusAsync("暂无封面", cancellationToken);
                return;
            }

            await SetPosterStatusAsync("封面加载中", cancellationToken);
            var path = await DramaPosterCache.TryGetLocalPathAsync(url, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                await SetPosterStatusAsync("暂无封面", cancellationToken);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                PosterImage?.Dispose();
                PosterImage = new Bitmap(path);
                _loadedPosterUrl = url;
                PosterStatus = "";
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await SetPosterStatusAsync("暂无封面", cancellationToken);
        }
        finally
        {
            if (ReferenceEquals(_posterCts, request))
            {
                _posterCts = null;
                _requestedPosterUrl = null;
                request.Dispose();
            }
        }
    }

    private async Task SetPosterStatusAsync(string status, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                PosterStatus = status;
            }
        });
    }
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

public sealed class DramaChainChoice(string id, string label)
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

public sealed partial class DramaDownloadViewModel : ViewModelBase
{
    private readonly string? _stateDatabasePath;
    private readonly DramaDownloadRunner _runner = new();
    private readonly List<DramaSearchItem> _allSearchResults = new();
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _newReleaseCts;
    private long _searchGeneration;
    private bool _isLoadingState;
    private CancellationTokenSource? _existenceCancellation;
    private string _existenceSignature = "";
    private bool _rankReloadPending;
    private bool _rankHasMore = true;
    private int _rankNextOffset;
    private int _rankGeneration;
    private bool _newReleaseActive;
    private bool _dramaBoxNewActive;
    private bool _dramaBoxNewHasMore;
    private int _dramaBoxNewPage = 1;
    private bool _newReleaseIsAi;
    private bool _newReleaseHasMore;
    private int _newReleaseOffset;
    private int _newReleaseGeneration;
    private string _newReleaseSession = "";
    private string? _newReleaseChain;
    private string _accountMarket = TikTokAccountProfile.DramaMarketLocal;
    private bool _suppressChainSave;

    public DramaDownloadViewModel(string? stateDatabasePath = null)
    {
        _stateDatabasePath = stateDatabasePath;
        _suppressChainSave = true;
        SelectedChain = ChainOptions.First(item => item.Id == VisibleDramaSource.DefaultChain);
        _suppressChainSave = false;
    }

    public ObservableCollection<DramaSearchRowViewModel> SearchResults { get; } = new();
    public ObservableCollection<DramaQueueRowViewModel> QueueRows { get; } = new();

    public IReadOnlyList<string> SearchCategoryOptions { get; } = ["漫剧", "短剧", "合集", "默认", "短剧ID"];
    public IReadOnlyList<string> RankCategoryOptions { get; } = ["全部", "真人剧", "漫剧", "AI剧"];
    public IReadOnlyList<string> RankBoardOptions => ShortDramaDramaServices.RankBoardsFor(RankCategory);
    public IReadOnlyList<string> SearchViewModeOptions { get; } = ["列表视图", "封面视图"];
    public IReadOnlyList<string> EpisodeRangeOptions { get; } = ["全部", "前1集", "前3集", "前5集", "自定义"];

    public IReadOnlyList<DramaChainChoice> ChainOptions { get; } =
    [
        new("hghigh", "红果短剧助手"),
        new("pikachu", "皮卡丘"),
        new("playlet", "红果短剧下载器"),
        new("hongguoapp", "红果APP"),
        new("dramabox", "DramaBox"),
        new("foreign", "海外短剧下载器"),
    ];

    [ObservableProperty] private string _downloadWorkspace = "";
    [ObservableProperty] private string _activeChainText = "当前链路：本土剧";
    [ObservableProperty] private string _activeMarketText = "本土剧";
    [ObservableProperty] private DramaChainChoice? _selectedChain;
    [ObservableProperty] private string _searchKeyword = "";
    [ObservableProperty] private string _searchCategory = "漫剧";
    [ObservableProperty] private int _searchPage = 1;
    [ObservableProperty] private bool _exactSearch;
    [ObservableProperty] private int _queryDays = 1;
    [ObservableProperty] private int _minEpisodeFilter;
    [ObservableProperty] private int _maxEpisodeFilter;
    [ObservableProperty] private string _episodeRangeMode = "全部";
    [ObservableProperty] private string _episodeCustomRange = "";
    [ObservableProperty] private string _searchViewMode = "列表视图";
    [ObservableProperty] private int _downloadConcurrent = 5;
    [ObservableProperty] private bool _autoGenerateMaterials = true;
    [ObservableProperty] private string _defaultQuality = "1080P";
    [ObservableProperty] private string _episodeNumberMode = "source";
    [ObservableProperty] private string _categoryInclude = "";
    [ObservableProperty] private string _categoryExclude = "";
    [ObservableProperty] private string _authorExclude = DramaDownloadQueueState.DefaultAuthorExclude;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isQueueBatchActive;
    [ObservableProperty] private string _queueBatchStatus = "";
    [ObservableProperty] private string _selectedCountText = "已选 0 项";
    [ObservableProperty] private string _searchPageText = "第 1 页";
    [ObservableProperty] private string _rankCategory = "漫剧";
    [ObservableProperty] private string _rankBoard = "热播榜";
    [ObservableProperty] private int _rankPage = 1;
    [ObservableProperty] private string _rankPageText = "第 1 页";
    [ObservableProperty] private string _queueStatsText = "待下载：0 | 下载中：0 | 完成：0 | 失败：0";
    private string _singleQueueTargetText = "目标账号：未选择";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddToQueueButtonText))]
    private string _queuePlatformLabel = "TIKTOK";

    public string AddToQueueButtonText => $"加入{QueuePlatformLabel}队列";
    public ObservableCollection<DramaPlatformQueueAction> PlatformQueueActions { get; } = [];
    public bool UsePlatformQueueActions => PlatformQueueActions.Count > 0;
    public bool ShowSingleQueueAction => !UsePlatformQueueActions;
    public string TikTokQueueTargetText => UsePlatformQueueActions ? "加入队列时选择账号" : _singleQueueTargetText;

    public void ConfigureQueuePlatform(string label)
    {
        QueuePlatformLabel = string.IsNullOrWhiteSpace(label) ? "TIKTOK" : label.Trim();
    }

    public void ConfigurePlatformQueues(IEnumerable<(string Key, string Label)> platforms)
    {
        PlatformQueueActions.Clear();
        foreach (var (key, label) in platforms)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(label)) continue;
            PlatformQueueActions.Add(new DramaPlatformQueueAction(
                key.Trim(), label.Trim(), $"加入{label.Trim()}队列", AddSelectedToMultipleTikTokQueuesCommand));
        }
        OnPropertyChanged(nameof(UsePlatformQueueActions));
        OnPropertyChanged(nameof(ShowSingleQueueAction));
        OnPropertyChanged(nameof(TikTokQueueTargetText));
    }

    public bool IsListView => !string.Equals(SearchViewMode, "封面视图", StringComparison.Ordinal);
    public bool IsPosterView => !IsListView;

    public event Action<string>? LogRequested;
    public event Func<TikTokQueueImportRequest, Task>? ImportToQueueRequested;
    public event Func<TikTokQueueImportTarget?>? TikTokQueueTargetRequested;
    public event Func<string, TikTokQueueImportTarget?>? PlatformQueueTargetRequested;
    public event Func<IReadOnlyList<TikTokQueueImportTarget>>? TikTokQueueTargetsRequested;
    public Func<IReadOnlyList<TikTokQueueImportTarget>, TikTokQueueImportTarget?, int, CancellationToken,
        Task<IReadOnlyList<TikTokQueueImportTarget>?>>? PickTikTokQueueTargetsAsync { get; set; }
    public Func<string, bool, Task<bool?>>? ConfirmQueueBatchAsync { get; set; }
    public Func<string, bool, Task<bool>>? ShowQueueBatchResultAsync { get; set; }
    public event Func<string, TikTokAccountProfile?>? AccountProfileRequested;
    public event Func<IReadOnlyList<TikTokAccountProfile>>? ExistenceAccountsRequested;

    public void UpdateTikTokQueueTarget(TikTokQueueImportTarget? target)
    {
        var targetText = target is null
            ? $"目标账号：未配置{QueuePlatformLabel}上传工作目录"
            : $"目标账号：{target.AccountProfileName} · {target.WorkspaceRoot}";
        SetProperty(ref _singleQueueTargetText, targetText, nameof(TikTokQueueTargetText));
        ScheduleExistenceCheck();
    }

    public void LoadState()
    {
        _isLoadingState = true;
        try
        {
            var state = DramaDownloadQueueStore.Load(_stateDatabasePath);
            DownloadWorkspace = state.WorkspacePath;
            AutoGenerateMaterials = state.AutoGenerateMaterials;
            DownloadConcurrent = state.DownloadConcurrent;
            EpisodeNumberMode = state.DownloadEpisodeNumberMode;
            DefaultQuality = ClientSettingsStore.NormalizeDramaDownloadQuality(state.DefaultQuality);
            CategoryInclude = state.CategoryInclude;
            CategoryExclude = state.CategoryExclude;
            AuthorExclude = state.AuthorExclude;

            QueueRows.Clear();
            foreach (var item in state.QueueItems)
                QueueRows.Add(new DramaQueueRowViewModel(item));
            RefreshQueueStats();

            var clientSettings = ClientSettingsStore.Load(_stateDatabasePath);
            ApplyClientSettings(clientSettings, preferSavedWorkspace: string.IsNullOrWhiteSpace(DownloadWorkspace));
        }
        finally
        {
            _isLoadingState = false;
        }

        // 加载期间属性变化不逐项落库；完整初始化后一次性保存迁移结果。
        SaveState();
    }

    public void ApplyClientSettings(ClientSettings settings, bool preferSavedWorkspace = false)
    {
        DefaultQuality = ClientSettingsStore.NormalizeDramaDownloadQuality(
            string.IsNullOrWhiteSpace(settings.DramaDownloadDefaultQuality)
                ? DefaultQuality
                : settings.DramaDownloadDefaultQuality);
        DownloadConcurrent = settings.DramaDownloadConcurrent > 0
            ? settings.DramaDownloadConcurrent
            : DownloadConcurrent;
        if (preferSavedWorkspace && !string.IsNullOrWhiteSpace(settings.LastDownloadWorkspace))
        {
            DownloadWorkspace = settings.LastDownloadWorkspace;
        }

        RefreshActiveChain(settings);
    }

    public void UseAccountMarket(string? market)
    {
        _accountMarket = TikTokAccountProfile.NormalizeDramaMarket(market);
        RefreshActiveChain();
    }

    private void RefreshActiveChain(ClientSettings? settings = null)
    {
        settings ??= ClientSettingsStore.Load(_stateDatabasePath);
        var overseas = TikTokAccountProfile.NormalizeDramaMarket(_accountMarket) == TikTokAccountProfile.DramaMarketOverseas;
        ActiveMarketText = overseas ? "海外剧" : "本土剧";
        ActiveChainText = VisibleDramaSource.Describe(settings, _accountMarket);
        var chain = VisibleDramaSource.ChainFor(settings, _accountMarket);
        var choice = ChainOptions.FirstOrDefault(item => string.Equals(item.Id, chain, StringComparison.OrdinalIgnoreCase))
                     ?? ChainOptions.First(item => item.Id == VisibleDramaSource.DefaultChain);
        if (!ReferenceEquals(SelectedChain, choice))
        {
            _suppressChainSave = true;
            SelectedChain = choice;
            _suppressChainSave = false;
        }

        ShortDramaDramaServices.RefreshSettings(VisibleDramaSource.SettingsFor(settings, _accountMarket));
    }

    partial void OnSelectedChainChanged(DramaChainChoice? value)
    {
        if (_suppressChainSave || _isLoadingState || value is null)
            return;
        PersistSelectedChain(value.Id);
    }

    private void PersistSelectedChain(string chainId)
    {
        var settings = ClientSettingsStore.Load(_stateDatabasePath);
        var overseas = TikTokAccountProfile.NormalizeDramaMarket(_accountMarket) == TikTokAccountProfile.DramaMarketOverseas;
        var normalized = overseas
            ? VisibleDramaSource.NormalizeOverseas(chainId)
            : VisibleDramaSource.Normalize(chainId);
        var current = overseas ? settings.OverseasDramaSourceChain : settings.DramaSourceChain;
        if (string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase))
        {
            RefreshActiveChain(settings);
            return;
        }

        if (overseas)
            settings.OverseasDramaSourceChain = normalized;
        else
            settings.DramaSourceChain = normalized;
        ClientSettingsStore.Save(settings, _stateDatabasePath);
        RefreshActiveChain(settings);
        ChainPersisted?.Invoke(normalized, overseas);
        LogRequested?.Invoke($"下载链路已保存：{ActiveMarketText} · {VisibleDramaSource.Label(normalized)}");
    }

    public event Action<string, bool>? ChainPersisted;

    private IDisposable HoldActiveChain()
    {
        var settings = ClientSettingsStore.Load(_stateDatabasePath);
        ActiveChainText = VisibleDramaSource.Describe(settings, _accountMarket);
        return ShortDramaDramaServices.HoldSettings(VisibleDramaSource.SettingsFor(settings, _accountMarket));
    }

    public void SaveState()
    {
        if (_isLoadingState) return;

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
        DramaDownloadQueueStore.Save(state, _stateDatabasePath);
    }

    partial void OnDownloadConcurrentChanged(int value) => SaveState();
    partial void OnAutoGenerateMaterialsChanged(bool value) => SaveState();
    partial void OnEpisodeNumberModeChanged(string value) => SaveState();
    partial void OnDefaultQualityChanged(string value) => SaveState();
    partial void OnSearchViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(IsPosterView));
        if (IsPosterView)
        {
            RequestPosterLoads();
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (IsSearching) return;
        using var chainHold = HoldActiveChain();
        var keyword = SearchKeyword.Trim();
        if (string.IsNullOrWhiteSpace(keyword) && !HasSearchFilter())
        {
            LogRequested?.Invoke("请输入搜索关键词，或填写分类筛选后搜索");
            return;
        }

        var dramaBoxSearch = ShortDramaDramaServices.IsDramaBoxSourceSelected() && !string.IsNullOrWhiteSpace(keyword);
        await LoadSearchResultsAsync(
            dramaBoxSearch
                ? $"DramaBox 搜索 · 英语 · 第 {SearchPage} 页"
                : string.IsNullOrWhiteSpace(keyword) ? "分类筛选上新" : $"第 {SearchPage} 页",
            async ct =>
            {
                if (string.IsNullOrWhiteSpace(keyword))
                    return await LoadMergedNewReleaseAsync(ct);
                return await ShortDramaDramaServices.SearchAsync(keyword, SearchPage, ct, SearchCategory);
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
        using var chainHold = HoldActiveChain();
        SearchPage = 1;
        if (ShortDramaDramaServices.IsHongguoAppSourceSelected() || ShortDramaDramaServices.IsPlayletSourceSelected())
        {
            await LoadHongguoNewReleaseAsync(isAi: false, append: false);
            return;
        }

        if (ShortDramaDramaServices.IsHighSourceSelected())
        {
            await LoadProgressiveHighNewReleaseAsync(
                $"漫剧上新 · {Math.Clamp(QueryDays, 1, 30)} 天",
                isAi: false,
                sourceMode: "mj_today");
            return;
        }

        await LoadSearchResultsAsync(
            $"漫剧上新 · {Math.Clamp(QueryDays, 1, 30)} 天",
            ct => ShortDramaDramaServices.GetMangaTodayAsync(Math.Clamp(QueryDays, 1, 30), ct),
            sourceMode: "mj_today");
    }

    [RelayCommand]
    private async Task LoadAiTodayAsync()
    {
        using var chainHold = HoldActiveChain();
        SearchPage = 1;
        if (ShortDramaDramaServices.IsHongguoAppSourceSelected() || ShortDramaDramaServices.IsPlayletSourceSelected())
        {
            await LoadHongguoNewReleaseAsync(isAi: true, append: false);
            return;
        }

        if (ShortDramaDramaServices.IsDramaBoxSourceSelected())
        {
            await LoadDramaBoxNewAsync(append: false);
            return;
        }

        if (ShortDramaDramaServices.IsHighSourceSelected())
        {
            await LoadProgressiveHighNewReleaseAsync(
                $"AI短剧上新 · {Math.Clamp(QueryDays, 1, 30)} 天",
                isAi: true,
                sourceMode: "aiju_today");
            return;
        }

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
        if (_newReleaseActive)
        {
            await LoadHongguoNewReleaseAsync(_newReleaseIsAi, append: true);
            return;
        }

        SearchPage += 1;
        await SearchAsync();
    }

    partial void OnRankCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(RankBoardOptions));
        if (RankBoardOptions.Contains(RankBoard, StringComparer.Ordinal))
            return;
        var first = RankBoardOptions.FirstOrDefault();
        if (!string.IsNullOrEmpty(first))
            RankBoard = first;
    }

    [RelayCommand]
    private Task RefreshRankAsync() => LoadRankCoreAsync(append: false);

    public void NotifyRankScroll(double offsetY, double viewportY, double extentY)
    {
    }

    public void NotifyHongguoNewReleaseScroll(double offsetY, double viewportY, double extentY)
    {
        if (_dramaBoxNewActive && _dramaBoxNewHasMore && !IsSearching && extentY > viewportY + 4 && offsetY + viewportY >= extentY - 80)
        {
            _ = LoadDramaBoxNewAsync(append: true);
            return;
        }

        if (!_newReleaseActive || !_newReleaseHasMore || IsSearching)
            return;
        if (extentY <= viewportY + 4)
            return;
        if (offsetY + viewportY < extentY - 80)
            return;
        _ = LoadHongguoNewReleaseAsync(_newReleaseIsAi, append: true);
    }

    private async Task LoadHongguoNewReleaseAsync(bool isAi, bool append)
    {
        if (IsSearching)
            return;
        if (append && (!_newReleaseActive || !_newReleaseHasMore))
            return;

        var settings = ClientSettingsStore.Load(_stateDatabasePath);
        ActiveChainText = VisibleDramaSource.Describe(settings, _accountMarket);
        settings = VisibleDramaSource.SettingsFor(settings, _accountMarket);
        if (append && !string.Equals(_newReleaseChain, settings.DramaSourceChain, StringComparison.OrdinalIgnoreCase))
        {
            _newReleaseHasMore = false;
            SearchPageText = "下载链路已切换，请重新点击上新";
            return;
        }
        using var chainHold = ShortDramaDramaServices.HoldSettings(settings);
        IsSearching = true;
        var generation = append ? _newReleaseGeneration : ++_newReleaseGeneration;
        var label = isAi ? "AI短剧上新 · 最新上架 · 7天内" : "漫剧上新 · 最新上架 · 7天内";
        var sourceMode = isAi ? "aiju_today" : "mj_today";
        if (!append)
        {
            CancelBackgroundNewRelease();
            _newReleaseActive = true;
            _dramaBoxNewActive = false;
            _newReleaseIsAi = isAi;
            _newReleaseChain = settings.DramaSourceChain;
            _newReleaseHasMore = true;
            _newReleaseOffset = 0;
            _newReleaseSession = "";
            _allSearchResults.Clear();
            ApplyFilteredSearchResults(label, updateSearchPageText: false);
        }

        SearchPageText = append ? $"{label} · 正在加载下一批" : $"{label} · 加载中...";
        try
        {
            var received = 0;
            var seen = _allSearchResults.Select(item => item.BookId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // A batch can contain only duplicates or dramas excluded by the current filters.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var offset = _newReleaseOffset;
                var page = await ShortDramaDramaServices.GetHongguoNewReleasePageAsync(
                    isAi, offset, _newReleaseSession, CancellationToken.None);
                if (generation != _newReleaseGeneration)
                    return;

                received += page.Items.Count;
                var added = new List<DramaSearchItem>();
                foreach (var item in page.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.BookId) || !seen.Add(item.BookId))
                        continue;
                    item.Selected = false;
                    item.SourceMode = sourceMode;
                    _allSearchResults.Add(item);
                    added.Add(item);
                }

                _newReleaseHasMore = page.HasMore && page.NextOffset > offset;
                _newReleaseOffset = page.NextOffset;
                if (!string.IsNullOrWhiteSpace(page.SessionId))
                    _newReleaseSession = page.SessionId;
                if (!_newReleaseHasMore || ApplySearchFilters(added).Count > 0)
                    break;
            }

            ApplyFilteredSearchResults(label, updateSearchPageText: false);
            SearchPageText = _newReleaseHasMore
                ? $"{label} · 已加载 {SearchResults.Count} 条，下滑或点击下页继续加载"
                : $"{label} · 已到最后 · {SearchResults.Count} 条";
            LogRequested?.Invoke($"{label}：本次返回 {received} 条，当前显示 {SearchResults.Count} 条");
        }
        catch (Exception ex)
        {
            if (generation != _newReleaseGeneration)
                return;
            SearchPageText = append ? $"{label} · 下一批加载失败，点击下页重试" : $"{label} · 加载失败";
            LogRequested?.Invoke($"{label}失败：{ex.Message}");
        }
        finally
        {
            if (generation == _newReleaseGeneration)
                IsSearching = false;
        }
    }

    private async Task LoadDramaBoxNewAsync(bool append)
    {
        using var chainHold = HoldActiveChain();
        if (IsSearching)
            return;
        if (append && (!_dramaBoxNewActive || !_dramaBoxNewHasMore))
            return;

        IsSearching = true;
        const string label = "AI短剧上新 · DramaBox New · 英语";
        var generation = append ? _newReleaseGeneration : ++_newReleaseGeneration;
        if (!append)
        {
            _dramaBoxNewActive = true;
            _newReleaseActive = false;
            _dramaBoxNewHasMore = true;
            _dramaBoxNewPage = 1;
            _allSearchResults.Clear();
            ApplyFilteredSearchResults(label, updateSearchPageText: false);
        }

        var pageNo = append ? _dramaBoxNewPage : 1;
        SearchPageText = append ? $"{label} · 正在加载下一批" : $"{label} · 加载中...";
        try
        {
            var page = await ShortDramaDramaServices.GetDramaBoxNewPageAsync(pageNo, CancellationToken.None);
            if (generation != _newReleaseGeneration)
                return;

            var added = 0;
            foreach (var item in page.Items)
            {
                if (_allSearchResults.Any(existing =>
                        string.Equals(existing.BookId, item.BookId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                item.Selected = false;
                item.SourceMode = "aiju_today";
                _allSearchResults.Add(item);
                added++;
            }

            _dramaBoxNewHasMore = page.HasMore && added > 0 && page.NextPage > pageNo;
            _dramaBoxNewPage = page.NextPage > pageNo ? page.NextPage : pageNo + 1;
            ApplyFilteredSearchResults(label, updateSearchPageText: false);
            SearchPageText = _dramaBoxNewHasMore
                ? $"{label} · 已加载 {SearchResults.Count} 条，继续下滑加载"
                : $"{label} · 已到最后 · {SearchResults.Count} 条";
            LogRequested?.Invoke($"{label}：本批 {page.Items.Count} 条，累计 {SearchResults.Count} 条");
            RequestPosterLoads();
        }
        catch (Exception ex)
        {
            if (generation != _newReleaseGeneration)
                return;
            SearchPageText = append ? $"{label} · 下一批加载失败" : $"{label} · 加载失败";
            LogRequested?.Invoke($"{label}失败：{ex.Message}");
        }
        finally
        {
            if (generation == _newReleaseGeneration)
                IsSearching = false;
        }
    }

    private async Task LoadRankCoreAsync(bool append)
    {
        using var chainHold = HoldActiveChain();
        if (IsSearching)
        {
            if (!append)
                _rankReloadPending = true;
            return;
        }

        if (append && !_rankHasMore)
            return;

        var generation = append ? _rankGeneration : ++_rankGeneration;
        if (!append)
        {
            _newReleaseActive = false;
            _rankReloadPending = false;
            _rankHasMore = true;
            _rankNextOffset = 0;
            RankPage = 1;
        }

        var category = RankCategory;
        var board = RankBoard;
        var block = ShortDramaDramaServices.RankBlockReason(category, board);
        if (block is not null)
        {
            _rankHasMore = false;
            _allSearchResults.Clear();
            ApplyFilteredSearchResults(block, updateSearchPageText: false);
            RankPageText = block;
            LogRequested?.Invoke(block);
            return;
        }

        var offset = append ? _rankNextOffset : 0;
        var label = $"榜单 · {category} · {board}";
        IsSearching = true;
        RankPageText = append ? $"{label} · 正在加载下一页" : $"{label} · 加载中...";
        var continueDrain = false;
        try
        {
            var page = await ShortDramaDramaServices.GetRankPageAsync(category, board, offset, CancellationToken.None);
            if (generation != _rankGeneration)
                return;

            if (!append)
                _allSearchResults.Clear();
            var added = 0;
            foreach (var item in page.Items)
            {
                if (_allSearchResults.Any(existing =>
                        string.Equals(existing.BookId, item.BookId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                item.Selected = false;
                _allSearchResults.Add(item);
                added++;
            }

            var nextOffset = page.NextOffset > offset ? page.NextOffset : offset + Math.Max(page.Items.Count, 1);
            _rankHasMore = page.HasMore && added > 0 && nextOffset > offset;
            _rankNextOffset = nextOffset;
            ApplyFilteredSearchResults(label, updateSearchPageText: false);
            RankPageText = _rankHasMore
                ? $"{label} · 已加载 {SearchResults.Count} 条"
                : $"{label} · 已到最后 · {SearchResults.Count} 条";
            LogRequested?.Invoke($"{label}：本页 {page.Items.Count} 条，累计 {SearchResults.Count} 条");
            continueDrain = _rankHasMore;
        }
        catch (Exception ex)
        {
            if (generation != _rankGeneration)
                return;
            RankPageText = append ? $"{label} · 下一页加载失败" : $"{label} · 加载失败";
            LogRequested?.Invoke($"{label}失败：{ex.Message}");
        }
        finally
        {
            if (generation == _rankGeneration)
                IsSearching = false;
        }

        if (generation != _rankGeneration)
            return;
        if (_rankReloadPending)
        {
            _rankReloadPending = false;
            await LoadRankCoreAsync(append: false);
            return;
        }

        if (continueDrain)
            await LoadRankCoreAsync(append: true);
    }

    public void ApplyEpisodeFilter()
    {
        var minEpisodes = Math.Max(0, MinEpisodeFilter);
        var maxEpisodes = Math.Max(0, MaxEpisodeFilter);
        if (maxEpisodes > 0 && minEpisodes > maxEpisodes)
        {
            LogRequested?.Invoke($"最小集数 {minEpisodes} 不能大于最大集数 {maxEpisodes}");
            return;
        }

        SaveState();
        var rangeText = DescribeEpisodeFilter(minEpisodes, maxEpisodes);
        ApplyFilteredSearchResults($"已筛选 · {rangeText}");
        RankPageText = $"已筛选 · {rangeText} · {SearchResults.Count} 条";
        LogRequested?.Invoke($"集数筛选已应用：{rangeText}，剩余 {SearchResults.Count} 条");
    }

    private static string DescribeEpisodeFilter(int minEpisodes, int maxEpisodes)
    {
        if (minEpisodes > 0 && maxEpisodes > 0)
            return $"{minEpisodes}-{maxEpisodes} 集";
        if (minEpisodes > 0)
            return $"不少于 {minEpisodes} 集";
        if (maxEpisodes > 0)
            return $"不超过 {maxEpisodes} 集";
        return "不限集数";
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
        var platformLabel = QueuePlatformLabel;
        var selected = SelectedSearchItems();
        if (selected.Count == 0)
        {
            LogRequested?.Invoke($"请先勾选要加入 {platformLabel} 队列的短剧");
            return;
        }

        selected = FilterAuthorExcludedItems(selected, $"{platformLabel} 队列");
        if (selected.Count == 0) return;

        var target = CaptureTikTokQueueTarget();
        if (target is null)
        {
            return;
        }
        var uploadWorkspace = target.WorkspaceRoot;

        if (!await Task.Run(() => Directory.Exists(uploadWorkspace)))
        {
            LogRequested?.Invoke($"{platformLabel}工作目录不存在：{uploadWorkspace}");
            return;
        }

        var episodes = ResolveEpisodeSelection();
        if (!IsValidEpisodeSelection(episodes))
        {
            LogRequested?.Invoke("集数范围格式不正确，请输入类似 68、1-5 或 1,3,5 的格式");
            return;
        }

        var profile = AccountProfileRequested?.Invoke(target.AccountProfileId);
        var overLimitSettings = OverLimitEpisodePolicy.WithAccountBounds(ClientSettingsStore.Load(), profile);
        var dirs = new List<string>();
        foreach (var item in selected)
        {
            var episodeError = ResolveQueueEpisodeLimitError(item, target, overLimitSettings);
            if (episodeError.Length > 0)
            {
                LogRequested?.Invoke($"未加入队列：《{item.Title}》 · {episodeError}");
                continue;
            }
            var itemEpisodes = ResolveOverLimitEpisodes(item, episodes, overLimitSettings);
            var queueEntryDramaType = ResolveQueueEntryDramaType(item);
            var projectDir = await Task.Run(() => ShortDramaDramaServices.BootstrapAsync(
                    uploadWorkspace,
                    item,
                    itemEpisodes,
                    DefaultQuality,
                    DownloadConcurrent,
                    EpisodeNumberMode,
                    queueEntryDramaType,
                    CancellationToken.None))
                .ConfigureAwait(true);
            MarkOverLimitProject(projectDir, item, overLimitSettings);
            dirs.Add(projectDir);
            LogRequested?.Invoke($"已准备{platformLabel}队列项目：{item.Title}");
        }

        var importHandler = ImportToQueueRequested;
        if (importHandler is null)
            return;
        try
        {
            await importHandler(new TikTokQueueImportRequest(target, dirs));
        }
        catch (Exception ex)
        {
            LogRequested?.Invoke($"加入{platformLabel}队列失败：{ex.Message}");
            return;
        }
        foreach (var item in selected)
            ClearSearchItemSelection(item);
        UpdateSelectedCount();
        LogRequested?.Invoke($"已加入 {dirs.Count} 个剧目到「{target.AccountProfileName}」{platformLabel}队列");
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task AddSelectedToMultipleTikTokQueuesAsync(string? platformKey, CancellationToken cancellationToken)
    {
        if (IsQueueBatchActive) return;
        var action = PlatformQueueActions.FirstOrDefault(item => item.Key == platformKey);
        if (platformKey is not null && action is null) return;
        var queueLabel = action is not null ? $"{action.PlatformLabel} 队列"
            : UsePlatformQueueActions ? "队列" : $"{QueuePlatformLabel} 队列";
        var selected = SelectedSearchItems();
        if (selected.Count == 0)
        {
            LogRequested?.Invoke($"请先勾选要加入{queueLabel}的短剧");
            return;
        }

        selected = FilterAuthorExcludedItems(selected, queueLabel);
        if (selected.Count == 0) return;

        var episodes = ResolveEpisodeSelection();
        if (!IsValidEpisodeSelection(episodes))
        {
            LogRequested?.Invoke("集数范围格式不正确，请输入类似 68、1-5 或 1,3,5 的格式");
            return;
        }

        var targets = TikTokQueueTargetsRequested?.Invoke() ?? [];
        if (action is not null)
            targets = targets.Where(item => item.PlatformKey == action.Key).ToArray();
        if (targets.Count == 0)
        {
            LogRequested?.Invoke($"没有可选的{queueLabel}账号");
            return;
        }

        var picker = PickTikTokQueueTargetsAsync;
        var confirm = ConfirmQueueBatchAsync;
        var import = ImportToQueueRequested;
        if (picker is null || import is null) return;

        IsQueueBatchActive = true;
        QueueBatchStatus = "正在读取账号工作目录…";
        try
        {
            var currentTarget = action is null ? TikTokQueueTargetRequested?.Invoke() : PlatformQueueTargetRequested?.Invoke(action.Key);
            var picked = await picker(targets, currentTarget, selected.Count, cancellationToken);
            if (picked is null || picked.Count == 0 || confirm is null) return;
            cancellationToken.ThrowIfCancellationRequested();

            var workspaceIssues = await Task.Run(() => picked.Select(target => (Target: target, Issue: GetQueueWorkspaceIssue(target)))
                .Where(entry => entry.Issue.Length > 0).ToArray(), cancellationToken);
            var invalidAccounts = workspaceIssues.Select(entry => QueueTargetKey(entry.Target)).ToHashSet(StringComparer.Ordinal);
            var validTargets = picked.Where(target => !invalidAccounts.Contains(QueueTargetKey(target))).ToArray();
            if (validTargets.Length == 0)
            {
                var issues = string.Join("\n", workspaceIssues.Select(entry => $"{entry.Target.Label} · {entry.Issue}"));
                var showResult = ShowQueueBatchResultAsync;
                if (showResult is not null)
                    await showResult($"没有可入队的账号。\n\n{issues}", false);
                return;
            }

            if (validTargets.GroupBy(target => QueueWorkspaceKey(target.WorkspaceRoot), StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Skip(1).Any()))
            {
                LogRequested?.Invoke("所选账号的工作目录有冲突，请为各账号设置独立目录后再入队。");
                return;
            }

            QueueBatchStatus = $"正在核对 0 / {validTargets.Length} 个账号…";
            var conflicts = await CollectQueueConflictsAsync(validTargets, selected, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var duplicateCount = validTargets.Sum(target => conflicts.BlockedCount(target, selected));
            var plannedCount = picked.Count * (long)selected.Count;
            var unavailableCount = workspaceIssues.Length * (long)selected.Count;
            var readyCount = plannedCount - unavailableCount - duplicateCount;
            var preview = $"{selected.Count} 部剧 × {picked.Count} 个账号，共 {plannedCount} 条待入队任务。\n"
                + $"可直接入队 {readyCount} 条 · 已存在 {duplicateCount} 条 · 账号目录异常 {unavailableCount} 条";
            if (conflicts.Lines.Count > 0)
                preview += "\n\n" + string.Join("\n", conflicts.Lines);
            if (workspaceIssues.Length > 0)
                preview += "\n\n" + string.Join("\n", workspaceIssues.Select(entry => $"{entry.Target.Label} · {entry.Issue}"));

            var skipExisting = await confirm(preview, duplicateCount > 0);
            if (skipExisting is null) return;
            cancellationToken.ThrowIfCancellationRequested();

            var pending = validTargets.Select(target => new QueueImportAccount(
                target,
                selected.Where(item => skipExisting != true || !conflicts.IsBlocked(target, item))
                    .Select(item => new QueueImportItem(item)).ToArray()))
                .Where(account => account.Items.Count > 0)
                .ToArray();
            var skippedCount = (skipExisting == true ? duplicateCount : 0L) + unavailableCount;
            var options = new QueueImportOptions(episodes, DefaultQuality, DownloadConcurrent, EpisodeNumberMode,
                ClientSettingsStore.Load(_stateDatabasePath));
            var addedCount = 0L;

            while (true)
            {
                var failures = new List<QueueImportAccount>();
                var errors = new List<string>();
                var processed = 0;
                foreach (var account in pending)
                {
                    // 当前账号写完后再停止，保留成功记录以及剩余任务的准确数量。
                    if (cancellationToken.IsCancellationRequested) break;
                    QueueBatchStatus = $"正在入队 {processed + 1} / {pending.Length} 个账号 · {account.Target.Label}";

                    var failedItems = await ImportQueueAccountAsync(account, options, import, errors);
                    addedCount += account.Items.Count - failedItems.Count;
                    if (failedItems.Count > 0)
                        failures.Add(new QueueImportAccount(account.Target, failedItems));
                    processed++;
                }

                var remaining = pending.Skip(processed).Sum(account => (long)account.Items.Count);
                var failedCount = failures.Sum(account => (long)account.Items.Count);
                var summary = $"已入队 {addedCount} 条 · 跳过 {skippedCount} 条 · 失败 {failedCount} 条";
                if (remaining > 0)
                    summary += $" · 停止后未处理 {remaining} 条";
                QueueBatchStatus = summary;
                LogRequested?.Invoke(summary);

                var showResult = ShowQueueBatchResultAsync;
                var allowRetry = failedCount > 0 && !cancellationToken.IsCancellationRequested;
                var details = errors.Count == 0 ? summary : summary + "\n\n" + string.Join("\n", errors);
                if (showResult is null || !await showResult(details, allowRetry) || !allowRetry)
                {
                    if (failedCount == 0 && remaining == 0)
                    {
                        foreach (var item in selected)
                            ClearSearchItemSelection(item);
                        UpdateSelectedCount();
                    }
                    return;
                }
                pending = failures.ToArray();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogRequested?.Invoke("已取消批量入队。");
        }
        catch (Exception ex)
        {
            LogRequested?.Invoke($"批量入队失败：{ex.Message}");
        }
        finally
        {
            IsQueueBatchActive = false;
        }
    }

    private async Task<IReadOnlyList<QueueImportItem>> ImportQueueAccountAsync(
        QueueImportAccount account,
        QueueImportOptions options,
        Func<TikTokQueueImportRequest, Task> import,
        List<string> errors)
    {
        TikTokAccountProfile? profile = null;
        if ((account.Target.PlatformKey is "" or DramaQueuePlatforms.TikTok) && AccountProfileRequested is { } findAccount)
        {
            profile = findAccount(account.Target.AccountProfileId);
            var workspace = await Task.Run(() => profile?.ResolveWorkspacePath() ?? "");
            if (workspace.Length == 0 ||
                !string.Equals(Path.TrimEndingDirectorySeparator(workspace),
                    QueueWorkspaceKey(account.Target.WorkspaceRoot), StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{account.Target.Label} · 账号或工作目录已变更，请重新选择账号");
                return account.Items;
            }
        }

        var issue = await Task.Run(() => GetQueueWorkspaceIssue(account.Target));
        if (issue.Length > 0)
        {
            errors.Add($"{account.Target.Label} · {issue}");
            return account.Items;
        }

        var failed = new List<QueueImportItem>();
        var prepared = new List<QueueImportItem>();
        var settings = OverLimitEpisodePolicy.WithAccountBounds(options.Settings, profile);
        foreach (var item in account.Items)
        {
            try
            {
                var episodeError = ResolveQueueEpisodeLimitError(item.Drama, account.Target, settings);
                if (episodeError.Length > 0)
                {
                    failed.Add(item);
                    errors.Add($"{account.Target.Label} · 《{item.Drama.Title}》 · {episodeError}");
                    continue;
                }
                var itemEpisodes = ResolveOverLimitEpisodes(item.Drama, options.Episodes, settings);
                var dramaType = ResolveQueueEntryDramaType(item.Drama);
                var projectDirectory = await Task.Run(async () =>
                {
                    if (item.ProjectDirectory is { } path && WorkspaceProjectScanner.IsValidProjectDirectory(path))
                        return path;

                    var directory = await ShortDramaDramaServices.BootstrapAsync(
                        account.Target.WorkspaceRoot, item.Drama, itemEpisodes, options.Quality,
                        options.DownloadConcurrent, options.EpisodeNumberMode, dramaType, CancellationToken.None);
                    MarkOverLimitProject(directory, item.Drama, settings);
                    return directory;
                });
                prepared.Add(item with { ProjectDirectory = projectDirectory });
            }
            catch (Exception ex)
            {
                failed.Add(item);
                errors.Add($"{account.Target.Label} · 《{item.Drama.Title}》 · {ex.Message}");
            }
        }

        if (prepared.Count == 0) return failed;

        try
        {
            await import(new TikTokQueueImportRequest(account.Target, prepared.Select(item => item.ProjectDirectory!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()) { IsBatch = true });
        }
        catch (QueueImportPartialException ex)
        {
            failed.AddRange(prepared.Where(item => !ex.ImportedPaths.Contains(item.ProjectDirectory!)));
            errors.Add($"{account.Target.Label} · {ex.Message}");
        }
        catch (Exception ex)
        {
            failed.AddRange(prepared);
            errors.Add($"{account.Target.Label} · {ex.Message}");
        }
        return failed;
    }

    internal static string GetQueueWorkspaceIssue(TikTokQueueImportTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.WorkspaceRoot))
            return "未配置上传工作目录";
        if (!Directory.Exists(target.WorkspaceRoot))
            return "工作目录不存在或无法访问";
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(target.WorkspaceRoot).GetEnumerator();
            entries.MoveNext();
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "工作目录无法访问";
        }
    }

    internal static TikTokQueueImportTarget CheckQueueAccountWorkspace(TikTokQueueImportTarget target)
    {
        var issue = GetQueueWorkspaceIssue(target);
        if (issue.Length > 0 && !Directory.Exists(target.WorkspaceRoot) &&
            !string.IsNullOrWhiteSpace(target.FallbackWorkspaceRoot))
        {
            var fallback = target with { WorkspaceRoot = target.FallbackWorkspaceRoot };
            if (GetQueueWorkspaceIssue(fallback).Length == 0)
                return fallback with { WorkspaceIssue = "" };
        }
        return target with { WorkspaceIssue = issue };
    }

    internal static string QueueWorkspaceKey(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private sealed record QueueImportItem(DramaSearchItem Drama, string? ProjectDirectory = null);
    private sealed record QueueImportAccount(TikTokQueueImportTarget Target, IReadOnlyList<QueueImportItem> Items);
    private sealed record QueueImportOptions(string Episodes, string Quality, int DownloadConcurrent,
        string EpisodeNumberMode, ClientSettings Settings);

    private async Task<QueueConflictReport> CollectQueueConflictsAsync(
        IReadOnlyList<TikTokQueueImportTarget> accounts,
        IReadOnlyList<DramaSearchItem> items,
        CancellationToken cancellationToken)
    {
        var titles = items.Select(item => item.Title).Where(title => !string.IsNullOrWhiteSpace(title)).ToArray();
        var report = new QueueConflictReport();
        var checkedCount = 0;
        foreach (var batch in accounts.Chunk(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tasks = batch.Select(account =>
            {
                var isTikTok = account.PlatformKey is "" or DramaQueuePlatforms.TikTok;
                var profile = isTikTok ? AccountProfileRequested?.Invoke(account.AccountProfileId) : null;
                return Task.Run(() => CheckQueueAccountAsync(account, isTikTok, profile, titles, cancellationToken), cancellationToken);
            });
            var results = await Task.WhenAll(tasks);
            foreach (var result in results)
            {
                report.BlockedTitles[QueueTargetKey(result.Account)] = result.BlockedTitles;
                if (result.Details.Length > 0)
                    report.Lines.Add($"{result.Account.Label} · {result.Details}");
            }

            checkedCount += batch.Length;
            QueueBatchStatus = $"正在核对 {checkedCount} / {accounts.Count} 个账号…";
        }

        return report;
    }

    private static async Task<QueueAccountCheck> CheckQueueAccountAsync(
        TikTokQueueImportTarget account,
        bool isTikTok,
        TikTokAccountProfile? profile,
        IReadOnlyList<string> titles,
        CancellationToken cancellationToken)
    {
        var archiveKeys = new HashSet<string>(StringComparer.Ordinal);
        var managementKeys = new HashSet<string>(StringComparer.Ordinal);
        var workspaceKeys = new HashSet<string>(StringComparer.Ordinal);
        var details = new List<string>();
        if (isTikTok)
        {
            try
            {
                var existence = await DramaDownloadExistenceService.CheckAsync(titles, profile, cancellationToken).ConfigureAwait(false);
                archiveKeys.UnionWith(existence.ArchiveKeys);
                managementKeys.UnionWith(existence.ManagementKeys);
                if (profile is not null && existence.ArchiveChecked)
                {
                    var archiveRoot = profile.ResolveArchiveRootPath(account.WorkspaceRoot);
                    foreach (var archived in TikTokArchivedProjectService.ListForNewTitleCheck(account.WorkspaceRoot, archiveRoot))
                    {
                        AddTitleKey(archiveKeys, archived.NewTitle);
                        AddTitleKey(archiveKeys, archived.DisplayName);
                        AddTitleKey(archiveKeys, archived.OriginalTitle);
                    }
                }
                if (!existence.ManagementChecked && !string.IsNullOrWhiteSpace(existence.ManagementMessage))
                    details.Add($"管理系统未核对：{existence.ManagementMessage}");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                details.Add($"归档或管理系统未核对：{ex.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var projectTitles = isTikTok
                ? WorkspaceQueueService.InspectProjects(account.WorkspaceRoot)
                    .Select(project => (project.NewTitle, project.OriginalTitle, project.DisplayName))
                : WorkspaceProjectScanner.Scan(account.WorkspaceRoot)
                    .Select(project => (project.NewTitle, project.OriginalTitle, project.DisplayName));
            foreach (var (newTitle, originalTitle, displayName) in projectTitles)
            {
                AddTitleKey(workspaceKeys, newTitle);
                AddTitleKey(workspaceKeys, originalTitle);
                AddTitleKey(workspaceKeys, displayName);
            }
        }
        catch (Exception ex)
        {
            details.Add($"工作目录未核对：{ex.Message}");
        }

        var normalizedTitles = titles.Select(title => (Title: title, Key: AiRewriteHistoryService.NormalizeTitleKey(title)))
            .Where(title => title.Key.Length > 0).ToArray();
        var blocked = normalizedTitles.Where(title =>
            archiveKeys.Contains(title.Key) || managementKeys.Contains(title.Key) || workspaceKeys.Contains(title.Key))
            .Select(title => title.Title).ToHashSet(StringComparer.Ordinal);
        if (blocked.Count > 0)
        {
            var archiveCount = normalizedTitles.Count(title => archiveKeys.Contains(title.Key));
            var managementCount = normalizedTitles.Count(title => managementKeys.Contains(title.Key));
            var workspaceCount = normalizedTitles.Count(title => workspaceKeys.Contains(title.Key));
            details.Insert(0, $"{blocked.Count} 部已存在（归档 {archiveCount}、管理系统 {managementCount}、工作目录 {workspaceCount}）");
        }
        return new QueueAccountCheck(account, blocked, string.Join("，", details));
    }

    private static void AddTitleKey(ISet<string> keys, string? title)
    {
        var key = AiRewriteHistoryService.NormalizeTitleKey(title);
        if (key.Length > 0)
            keys.Add(key);
    }

    private sealed record QueueAccountCheck(TikTokQueueImportTarget Account, HashSet<string> BlockedTitles, string Details);

    private sealed class QueueConflictReport
    {
        public List<string> Lines { get; } = [];
        public Dictionary<string, HashSet<string>> BlockedTitles { get; } = new(StringComparer.Ordinal);

        public bool IsBlocked(TikTokQueueImportTarget target, DramaSearchItem item) =>
            BlockedTitles[QueueTargetKey(target)].Contains(item.Title);

        public int BlockedCount(TikTokQueueImportTarget target, IReadOnlyList<DramaSearchItem> items) =>
            items.Count(item => IsBlocked(target, item));
    }

    [RelayCommand]
    private async Task StartDownloadQueueAsync()
    {
        if (!ClientFeaturePermissions.Allows("download"))
        {
            LogRequested?.Invoke("当前用户无下载功能权限。");
            return;
        }
        if (IsDownloading) return;
        using var chainHold = HoldActiveChain();
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
    private async Task ImportCompletedToQueueAsync()
    {
        var candidateDirs = QueueRows
            .Where(r => IsCompletedStatus(r.Item.Status))
            .Select(r => r.Item.ProjectDir)
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dirs = await Task.Run(() => candidateDirs.Where(Directory.Exists).ToArray())
            .ConfigureAwait(true);
        if (dirs.Length == 0)
        {
            LogRequested?.Invoke("没有已完成下载的项目可导入");
            return;
        }

        var target = CaptureTikTokQueueTarget();
        if (target is null) return;
        var importHandler = ImportToQueueRequested;
        if (importHandler is not null)
            await importHandler(new TikTokQueueImportRequest(target, dirs));
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
        string sourceMode,
        Action<string>? setPageText = null)
    {
        if (IsSearching) return;
        using var chainHold = HoldActiveChain();
        CancelBackgroundNewRelease();
        _newReleaseActive = false;
        _dramaBoxNewActive = false;
        IsSearching = true;
        SetPageText($"{label} · 加载中...");
        try
        {
            var items = await loader(CancellationToken.None);
            _existenceSignature = "";
            _allSearchResults.Clear();
            foreach (var item in items)
            {
                item.Selected = false;
                item.SourceMode = sourceMode;
                _allSearchResults.Add(item);
            }

            ApplyFilteredSearchResults(label, updateSearchPageText: setPageText is null);
            SetPageText($"{label} · {SearchResults.Count} 条");
            LogRequested?.Invoke($"{label}：{SearchResults.Count} 条");
        }
        catch (Exception ex)
        {
            SetPageText($"{label} · 加载失败");
            LogRequested?.Invoke($"{label}失败：{ex.Message}");
        }
        finally
        {
            IsSearching = false;
        }

        void SetPageText(string text)
        {
            if (setPageText is null)
                SearchPageText = text;
            else
                setPageText(text);
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

    private void ApplyFilteredSearchResults(string? label = null, bool updateSearchPageText = true)
    {
        var items = ApplySearchFilters(_allSearchResults)
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .GroupBy(item => item.BookId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var reusable = SearchResults
            .Where(row => !string.IsNullOrWhiteSpace(row.Item.BookId))
            .GroupBy(row => row.Item.BookId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var desiredRows = new List<DramaSearchRowViewModel>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (reusable.TryGetValue(item.BookId, out var row))
            {
                row.UpdateItem(item);
            }
            else
            {
                row = new DramaSearchRowViewModel(item);
                row.SelectionChanged += UpdateSelectedCount;
            }
            row.RowIndex = index + 1;
            desiredRows.Add(row);
        }

        var desiredSet = desiredRows.ToHashSet();
        for (var index = SearchResults.Count - 1; index >= 0; index--)
        {
            var row = SearchResults[index];
            if (desiredSet.Contains(row))
                continue;
            row.SelectionChanged -= UpdateSelectedCount;
            row.DisposePoster();
            SearchResults.RemoveAt(index);
        }

        for (var index = 0; index < desiredRows.Count; index++)
        {
            var row = desiredRows[index];
            var currentIndex = SearchResults.IndexOf(row);
            if (currentIndex < 0)
                SearchResults.Insert(index, row);
            else if (currentIndex != index)
                SearchResults.Move(currentIndex, index);
        }

        if (updateSearchPageText)
            SearchPageText = $"{(string.IsNullOrWhiteSpace(label) ? $"第 {SearchPage} 页" : label)} · 共 {SearchResults.Count} 条";
        UpdateSelectedCount();
        if (IsPosterView)
        {
            RequestPosterLoads();
        }
        ScheduleExistenceCheck();
    }

    private void ScheduleExistenceCheck()
    {
        var titles = SearchResults.Select(row => row.Item.Title).ToArray();
        if (titles.Length == 0)
        {
            _existenceCancellation?.Cancel();
            _existenceCancellation = null;
            _existenceSignature = "";
            return;
        }

        var accounts = (ExistenceAccountsRequested?.Invoke() ?? []).Select(account => new TikTokAccountProfile
        {
            Id = account.Id,
            Name = account.Name,
            TiktokAccountNickname = account.TiktokAccountNickname,
            TiktokLoginEmail = account.TiktokLoginEmail,
            TiktokLastLoginEmail = account.TiktokLastLoginEmail,
            TiktokUploadProfilePath = account.TiktokUploadProfilePath,
            LastWorkspace = account.LastWorkspace,
            TiktokArchiveRootDir = account.TiktokArchiveRootDir,
        }).ToArray();
        var accountSignature = string.Join('\n', accounts.Select(account =>
            string.Join('\u001f', account.Id, account.DisplayName, account.ResolveTikTokAccountName(),
                account.TiktokUploadProfilePath, account.LastWorkspace, account.TiktokArchiveRootDir)));
        var signature = string.Join('\n', titles) + "\u001e" + accountSignature;
        if (string.Equals(signature, _existenceSignature, StringComparison.Ordinal) &&
            (_existenceCancellation is not null || SearchResults.All(row => row.ExistenceText != "核对中")))
            return;

        _existenceSignature = signature;
        _existenceCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _existenceCancellation = cancellation;
        foreach (var row in SearchResults)
            row.ExistenceText = "核对中";
        _ = ApplyExistenceAsync(titles, accounts, cancellation);
    }

    private async Task ApplyExistenceAsync(
        IReadOnlyList<string> titles, IReadOnlyList<TikTokAccountProfile> accounts, CancellationTokenSource cancellation)
    {
        try
        {
            var result = await DramaDownloadExistenceService.CheckAllAsync(titles, accounts, cancellation.Token)
                .ConfigureAwait(true);
            if (!ReferenceEquals(_existenceCancellation, cancellation))
                return;
            foreach (var row in SearchResults)
                row.ExistenceText = result.LabelFor(row.Item.Title);
            foreach (var (name, check) in result.Accounts)
            {
                if (!check.ArchiveChecked && check.ArchiveMessage.Length > 0)
                    LogRequested?.Invoke($"「{name}」归档未核对 · {check.ArchiveMessage}");
                if (!check.ManagementChecked && check.ManagementMessage.Length > 0)
                    LogRequested?.Invoke($"「{name}」管理系统未核对 · {check.ManagementMessage}");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_existenceCancellation, cancellation))
                return;
            foreach (var row in SearchResults)
                row.ExistenceText = "未核对完整";
            LogRequested?.Invoke($"是否已存在未核对：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_existenceCancellation, cancellation))
                _existenceCancellation = null;
            cancellation.Dispose();
        }
    }

    private void RequestPosterLoads()
    {
        foreach (var row in SearchResults)
        {
            row.RequestPosterLoad();
        }
    }

    private IReadOnlyList<DramaSearchItem> ApplySearchFilters(IEnumerable<DramaSearchItem> source)
    {
        var keyword = SearchKeyword.Trim();
        var includeCategories = SplitKeywords(CategoryInclude);
        var excludeCategories = SplitKeywords(CategoryExclude);
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

        var overLimitSettings = ClientSettingsStore.Load();
        var added = 0;
        foreach (var item in selected)
        {
            var itemEpisodes = ResolveOverLimitEpisodes(item, episodes, overLimitSettings);
            var queueEntryDramaType = ResolveQueueEntryDramaType(item);
            var projectDir = await ShortDramaDramaServices.BootstrapAsync(
                DownloadWorkspace,
                item,
                itemEpisodes,
                DefaultQuality,
                DownloadConcurrent,
                EpisodeNumberMode,
                queueEntryDramaType,
                CancellationToken.None);
            MarkOverLimitProject(projectDir, item, overLimitSettings);

            var queueItem = new DramaDownloadQueueItem
            {
                Title = item.Title,
                BookId = item.BookId,
                ProjectDir = projectDir,
                Episodes = itemEpisodes,
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
                EpisodeTotal = OverLimitEpisodePolicy.ShouldTruncate(item.EpisodeTotal, overLimitSettings)
                    ? OverLimitEpisodePolicy.ResolveKeepCount(overLimitSettings)
                    : item.EpisodeTotal,
                FavoriteCount = item.FavoriteCount,
                PlayCount = item.PlayCount,
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
                LogRequested?.Invoke($"已更新下载队列：{item.Title}（集数 {itemEpisodes}）");
            }

            ClearSearchItemSelection(item);
            added++;
        }

        UpdateSelectedCount();
        SaveState();
        RefreshQueueStats();
        return added;
    }

    private void ClearSearchItemSelection(DramaSearchItem item)
    {
        foreach (var row in SearchResults.Where(row => ReferenceEquals(row.Item, item)))
            row.Selected = false;
        item.Selected = false;
    }

    private List<DramaSearchItem> SelectedSearchItems() =>
        SearchResults.Where(r => r.Selected).Select(r => r.Item).ToList();

    private static string QueueTargetKey(TikTokQueueImportTarget target) =>
        string.IsNullOrEmpty(target.PlatformKey)
            ? target.AccountProfileId
            : target.PlatformKey + "\u001f" + target.AccountProfileId;

    private TikTokQueueImportTarget? CaptureTikTokQueueTarget()
    {
        var target = TikTokQueueTargetRequested?.Invoke();
        UpdateTikTokQueueTarget(target);
        if (target is not null) return target;
        LogRequested?.Invoke($"请先在{QueuePlatformLabel}上传页为目标账号设置有效的工作目录");
        return null;
    }

    private async Task LoadProgressiveHighNewReleaseAsync(string label, bool isAi, string sourceMode)
    {
        using var chainHold = HoldActiveChain();
        if (IsSearching) return;
        CancelBackgroundNewRelease();
        _newReleaseActive = false;
        _dramaBoxNewActive = false;
        var generation = _searchGeneration;
        var request = new CancellationTokenSource();
        _newReleaseCts = request;
        IsSearching = true;
        var days = Math.Clamp(QueryDays, 1, 30);
        var dateWindowText = ShortDramaDramaServices.GetHighNewReleaseDateWindowDisplay(days);
        var datedLabel = $"{label} · {dateWindowText}";
        ReplaceLoadedSearchItems([], sourceMode, preserveSelection: false);
        ApplyFilteredSearchResults(datedLabel);
        SearchPageText = $"{datedLabel} · 加载中...";
        var pagesDisplayed = 0;
        var latestNetworkStatus = "正在获取第 1 页";
        var scheduledDetailIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processedDetailIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageQueue = Channel.CreateUnbounded<IReadOnlyList<DramaSearchItem>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        static bool HasRequiredMetrics(DramaSearchItem item) =>
            !string.IsNullOrWhiteSpace(item.PosterUrl) &&
            !string.IsNullOrWhiteSpace(item.Author) &&
            !string.IsNullOrWhiteSpace(item.Category) &&
            item.EpisodeTotal > 0 &&
            !string.IsNullOrWhiteSpace(item.PublishTime);

        void UpdateProgressText()
        {
            if (generation != _searchGeneration || request.IsCancellationRequested)
                return;
            var pageLabel = pagesDisplayed > 0 ? $"第 {pagesDisplayed} 页" : "等待第 1 页";
            SearchPageText =
                $"{datedLabel} · {pageLabel} · 已显示 {SearchResults.Count} 条 · " +
                $"指标 {processedDetailIds.Count}/{_allSearchResults.Count} · {latestNetworkStatus}";
        }

        var progress = new Progress<string>(message =>
        {
            if (generation == _searchGeneration)
            {
                latestNetworkStatus = message;
                UpdateProgressText();
            }
        });
        var firstPageShown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pagePipelineTask = Task.Run(async () =>
        {
            await foreach (var pageItems in pageQueue.Reader.ReadAllAsync(request.Token))
            {
                var enriched = await ShortDramaDramaServices.EnrichHighNewReleaseItemsAsync(
                    pageItems,
                    progress: null,
                    cancellationToken: request.Token,
                    detailResults: null);
                var filtered = ShortDramaDramaServices.FilterEnrichedHighNewReleaseItems(
                    enriched,
                    days);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation != _searchGeneration || request.IsCancellationRequested)
                        return;
                    AppendLoadedSearchItems(filtered, sourceMode);
                    pagesDisplayed++;
                    foreach (var item in filtered)
                    {
                        if (HasRequiredMetrics(item))
                            processedDetailIds.Add(item.BookId);
                        else
                            processedDetailIds.Remove(item.BookId);
                    }
                    ApplyFilteredSearchResults(datedLabel);
                    UpdateProgressText();
                    firstPageShown.TrySetResult(true);
                });
            }
        }, request.Token);
        var pageProgress = new InlineProgress<IReadOnlyList<DramaSearchItem>>(items =>
        {
            if (generation != _searchGeneration || request.IsCancellationRequested)
                return;
            var pageItems = items
                .Where(item => scheduledDetailIds.Add(item.BookId))
                .ToArray();
            foreach (var page in pageItems.Chunk(20))
                pageQueue.Writer.TryWrite(page.ToArray());
        });
        try
        {
            var listTask = Task.Run(
                async () => isAi
                    ? await ShortDramaDramaServices.GetAiTodayAsync(
                        days, enrich: false, progress, request.Token, pageProgress)
                    : await ShortDramaDramaServices.GetMangaTodayAsync(
                        days, enrich: false, progress, request.Token, pageProgress),
                request.Token);
            var firstCompletion = await Task.WhenAny(listTask, firstPageShown.Task);
            if (firstCompletion == listTask && !listTask.IsCompletedSuccessfully)
                await listTask;
            if (generation != _searchGeneration || request.IsCancellationRequested)
            {
                pageQueue.Writer.TryComplete();
                return;
            }
            _ = CompleteHighNewReleaseAsync(
                label,
                datedLabel,
                generation,
                request,
                listTask,
                pageQueue.Writer,
                pagePipelineTask,
                scheduledDetailIds);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            pageQueue.Writer.TryComplete();
            // A newer search replaced this request.
        }
        catch (Exception ex)
        {
            pageQueue.Writer.TryComplete(ex);
            if (generation == _searchGeneration)
            {
                SearchPageText = $"{label} · 加载失败";
                LogRequested?.Invoke($"{label}失败：{ex.Message}");
            }
            ReleaseNewReleaseRequest(request);
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task CompleteHighNewReleaseAsync(
        string label,
        string datedLabel,
        long generation,
        CancellationTokenSource request,
        Task<IReadOnlyList<DramaSearchItem>> listTask,
        ChannelWriter<IReadOnlyList<DramaSearchItem>> pageWriter,
        Task pagePipelineTask,
        HashSet<string> scheduledDetailIds)
    {
        var pipelineAwaited = false;
        try
        {
            var completeList = await listTask;
            var remaining = completeList
                .Where(item => scheduledDetailIds.Add(item.BookId))
                .ToArray();
            foreach (var page in remaining.Chunk(20))
                pageWriter.TryWrite(page.ToArray());
            pageWriter.TryComplete();
            await pagePipelineTask;
            pipelineAwaited = true;
            if (generation != _searchGeneration || request.IsCancellationRequested)
                return;
            ApplyFilteredSearchResults(datedLabel);
            SearchPageText = $"{datedLabel} · 共 {SearchResults.Count} 条";
            LogRequested?.Invoke($"{datedLabel}：{SearchResults.Count} 条");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // A newer search replaced this background enrichment.
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
            {
                SearchPageText = $"{label} · 已显示 {SearchResults.Count} 条 · 详情补充失败";
                LogRequested?.Invoke($"{label}详情补充失败：{ex.Message}");
            }
        }
        finally
        {
            pageWriter.TryComplete();
            if (!pipelineAwaited)
            {
                try { await pagePipelineTask; }
                catch { /* the primary error/cancellation is reported above */ }
            }
            ReleaseNewReleaseRequest(request);
        }
    }

    private void CancelBackgroundNewRelease()
    {
        Interlocked.Increment(ref _searchGeneration);
        var previous = _newReleaseCts;
        _newReleaseCts = null;
        if (previous is null)
            return;
        previous.Cancel();
        previous.Dispose();
    }

    private void ReleaseNewReleaseRequest(CancellationTokenSource request)
    {
        if (ReferenceEquals(_newReleaseCts, request))
            _newReleaseCts = null;
        request.Dispose();
    }

    private void ReplaceLoadedSearchItems(
        IReadOnlyList<DramaSearchItem> items,
        string sourceMode,
        bool preserveSelection)
    {
        var selected = preserveSelection
            ? SearchResults.Where(row => row.Selected).Select(row => row.Item.BookId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        _allSearchResults.Clear();
        foreach (var item in items)
        {
            item.Selected = selected.Contains(item.BookId);
            item.SourceMode = sourceMode;
            _allSearchResults.Add(item);
        }
    }

    private void AppendLoadedSearchItems(IReadOnlyList<DramaSearchItem> items, string sourceMode)
    {
        var byId = _allSearchResults
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .ToDictionary(item => item.BookId, StringComparer.OrdinalIgnoreCase);
        foreach (var source in items)
        {
            source.SourceMode = sourceMode;
            if (byId.TryGetValue(source.BookId, out var target))
            {
                CopySearchItemDetails(target, source);
                continue;
            }

            _allSearchResults.Add(source);
            byId[source.BookId] = source;
        }
    }

    private void MergeEnrichedSearchItems(IReadOnlyList<DramaSearchItem> items)
    {
        if (items.Count == 0)
            return;
        var byId = _allSearchResults
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .ToDictionary(item => item.BookId, StringComparer.OrdinalIgnoreCase);
        foreach (var source in items)
        {
            if (!byId.TryGetValue(source.BookId, out var target))
                continue;
            CopySearchItemDetails(target, source);
        }
    }

    private static void CopySearchItemDetails(DramaSearchItem target, DramaSearchItem source)
    {
        target.Author = source.Author;
        target.Category = source.Category;
        target.EpisodeTotal = source.EpisodeTotal;
        target.PublishTime = source.PublishTime;
        if (!string.IsNullOrWhiteSpace(source.Intro))
            target.Intro = source.Intro;
        target.PosterUrl = source.PosterUrl;
        target.FavoriteCount = source.FavoriteCount;
        target.PlayCount = source.PlayCount;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private List<DramaSearchItem> FilterAuthorExcludedItems(List<DramaSearchItem> selectedItems, string queueLabel)
    {
        var excluded = SplitKeywords(AuthorExclude);
        if (excluded.Count == 0) return selectedItems;

        var kept = new List<DramaSearchItem>();
        var skipped = new List<DramaSearchItem>();
        foreach (var item in selectedItems)
        {
            if (UploadTitleImportService.ShouldApplyAuthorExclusion(item.BookId)
                && excluded.Any(token => ContainsToken(item.Author, token)))
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

    private static string ResolveQueueEpisodeLimitError(
        DramaSearchItem item, TikTokQueueImportTarget target, ClientSettings settings)
    {
        if (target.PlatformKey is not ("" or DramaQueuePlatforms.TikTok) || item.EpisodeTotal <= 0)
            return "";
        var (minimum, maximum) = OverLimitEpisodePolicy.ResolveEpisodeBounds(settings);
        return UploadTitleImportService.ResolveEpisodeLimitError(item, settings, minimum, maximum);
    }

    private string ResolveOverLimitEpisodes(DramaSearchItem item, string selection, ClientSettings settings)
    {
        var resolved = OverLimitEpisodePolicy.ResolveDownloadSelection(selection, item.EpisodeTotal, settings);
        if (!string.Equals(resolved, selection, StringComparison.OrdinalIgnoreCase) &&
            OverLimitEpisodePolicy.ShouldTruncate(item.EpisodeTotal, settings))
        {
            LogRequested?.Invoke(
                $"超长剧只取前 {OverLimitEpisodePolicy.ResolveKeepCount(settings)} 集：{item.Title} 原 {item.EpisodeTotal} 集");
        }

        return resolved;
    }

    private static void MarkOverLimitProject(string projectDir, DramaSearchItem item, ClientSettings settings)
    {
        if (!OverLimitEpisodePolicy.ShouldTruncate(item.EpisodeTotal, settings))
            return;
        OverLimitEpisodePolicy.MarkTruncated(projectDir, item.EpisodeTotal, settings);
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
        SplitKeywords(CategoryExclude).Count > 0;

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
        target.PlayCount = source.PlayCount;
        target.PublishTime = source.PublishTime;
        target.PosterUrl = source.PosterUrl;
    }
}
