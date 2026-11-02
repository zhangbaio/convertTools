using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Analytics.Services;
using PlatformPublisher.Analytics.Storage;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Analytics;
using TikTokPublisher.Ui.Controls;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class AnalyticsViewModel : ObservableObject
{
    private static readonly string[] Palette = ["#2563EB", "#DC2626", "#059669", "#D97706", "#7C3AED", "#0891B2", "#BE185D", "#4B5563"];
    private readonly AnalyticsRepository _repository;
    private readonly AnalyticsCollectionCoordinator _coordinator;
    private readonly KuaishouWorksPlayCollector _collector;
    private Func<IReadOnlyList<AnalyticsAccount>> _accountProvider = () => [];
    private Func<AnalyticsAccount, bool> _publishRunning = _ => false;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _dailyTimer;
    private IReadOnlyList<AnalyticsAccount> _accounts = [];
    private IReadOnlyList<DailyAnalyticsRecord> _records = [];
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);

    public AnalyticsViewModel(AnalyticsRepository repository, AnalyticsCollectionCoordinator coordinator, KuaishouWorksPlayCollector collector)
    {
        _repository = repository;
        _coordinator = coordinator;
        _collector = collector;
    }

    public ObservableCollection<KuaishouPlayCardViewModel> Cards { get; } = [];
    public ObservableCollection<KuaishouPlayLegendViewModel> Legend { get; } = [];
    public ObservableCollection<KuaishouPlayTabViewModel> Tabs { get; } = [];

    [ObservableProperty] private IReadOnlyList<AnalyticsChartSeries> _chartSeries = [];
    [ObservableProperty] private int _rangeDays = 30;
    [ObservableProperty] private string _statusMessage = "尚未加载播放量。";
    [ObservableProperty] private bool _isBusy;
    public string RangeTitle => RangeDays <= 7 ? "每日播放量 · 近7天" : "每日播放量 · 近30天";

    public void Bind(Func<IReadOnlyList<AnalyticsAccount>> accountProvider, Func<AnalyticsAccount, bool> publishRunning)
    {
        _accountProvider = accountProvider;
        _publishRunning = publishRunning;
        _dailyTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _dailyTimer.Tick -= OnDailyTick;
        _dailyTimer.Tick += OnDailyTick;
        _dailyTimer.Start();
        _ = RefreshIfDueAsync();
    }

    public Task ActivateAsync()
    {
        LoadCached();
        if (!IsBusy)
        {
            StatusMessage = _accounts.Count == 0
                ? "还没有快手分账账号。"
                : "已显示本地保存的最近 30 天播放量。";
            _ = RefreshIfDueAsync();
        }
        return Task.CompletedTask;
    }

    private void OnDailyTick(object? sender, EventArgs e) => _ = RefreshIfDueAsync();

    private async Task RefreshIfDueAsync()
    {
        if (IsBusy || !CollectionDue()) return;
        await CollectAsync(null);
    }

    private bool CollectionDue()
    {
        var now = DateTime.Now;
        var window = KuaishouPlayWindow.Dates(DateOnly.FromDateTime(now));
        var accounts = Accounts();
        if (accounts.Count == 0) return false;
        var records = _repository.ListDaily(window[0], window[^1]);
        foreach (var account in accounts)
        {
            var saved = records
                .Where(record => record.AccountId.Equals(account.Id, StringComparison.OrdinalIgnoreCase) &&
                                 record.Platform == account.Platform &&
                                 record.Status == AnalyticsRecordStatus.Success &&
                                 record.SeriesViewsTotal.HasValue)
                .Select(record => record.MetricDate);
            if (KuaishouPlayWindow.NeedsCollection(window, saved, now.Hour)) return true;
        }
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task UpdateAll() => CollectAsync(null);

    [RelayCommand(CanExecute = nameof(CanUpdateAccount))]
    private Task UpdateAccount(string? key) => CollectAsync(key);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void ShowSevenDays() => SetRange(7);

    [RelayCommand]
    private void ShowThirtyDays() => SetRange(30);

    [RelayCommand]
    private void SelectTab(string? key)
    {
        _selectedKey = key ?? string.Empty;
        Rebuild();
    }

    [RelayCommand]
    private void ToggleLegend(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!_hidden.Add(key)) _hidden.Remove(key);
        Rebuild();
    }

    private string _selectedKey = string.Empty;

    private bool CanUpdate() => !IsBusy;
    private bool CanUpdateAccount(string? key) => !IsBusy && !string.IsNullOrWhiteSpace(key);
    private bool CanStop() => IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        UpdateAllCommand.NotifyCanExecuteChanged();
        UpdateAccountCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    partial void OnRangeDaysChanged(int value) => OnPropertyChanged(nameof(RangeTitle));

    private void SetRange(int days)
    {
        RangeDays = days;
        Rebuild();
    }

    private async Task CollectAsync(string? onlyKey)
    {
        if (IsBusy) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var failures = new List<string>();
        var written = 0;
        try
        {
            var accounts = Accounts().Where(account => onlyKey is null || KuaishouPlayBoard.Key(account) == onlyKey).ToArray();
            if (accounts.Length == 0)
            {
                StatusMessage = "没有可更新的快手分账账号。";
                return;
            }
            var today = DateOnly.FromDateTime(DateTime.Now);
            foreach (var account in accounts)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var label = KuaishouPlayBoard.Label(account);
                if (_publishRunning(account))
                {
                    failures.Add(label + "：该账号正在上传，请稍后再更新播放量。");
                    StatusMessage = failures[^1];
                    continue;
                }
                StatusMessage = "正在采集 " + label;
                try
                {
                    var window = KuaishouPlayWindow.Dates(today);
                    var plays = await _coordinator.RunExclusiveAsync(
                        KuaishouPlayBoard.Key(account),
                        ct => _collector.CollectAsync(account, window[0], window[^1], ct),
                        _cts.Token);
                    written += KuaishouPlayStore.Save(_repository, account, plays, today);
                    LoadCached();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(label + "：" + ex.Message);
                    StatusMessage = failures[^1];
                }
            }
            LoadCached();
            StatusMessage = failures.Count == 0
                ? $"更新完成，写入 {written} 天播放量。"
                : $"更新结束，写入 {written} 天。{string.Join(" ", failures)}";
        }
        catch (OperationCanceledException)
        {
            LoadCached();
            StatusMessage = "已停止。已经写入的播放量仍保留。";
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void LoadCached()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var dates = KuaishouPlayWindow.Dates(today);
        _accounts = Accounts();
        var ids = _accounts.Select(account => account.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _records = _repository.ListDaily(dates[0], dates[^1])
            .Where(record => ids.Contains(record.AccountId) &&
                             record.Platform is PublishPlatform.KuaishouPersonalRevenue or PublishPlatform.KuaishouEnterpriseRevenue)
            .ToArray();
        if (!string.IsNullOrEmpty(_selectedKey) && _accounts.All(account => KuaishouPlayBoard.Key(account) != _selectedKey))
            _selectedKey = string.Empty;
        _hidden.RemoveWhere(key => _accounts.All(account => KuaishouPlayBoard.Key(account) != key));
        Rebuild();
    }

    private void Rebuild()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var board = KuaishouPlayBoard.Build(_accounts, _records, today, RangeDays, _selectedKey);
        Replace(Cards, board.Cards.Select(card => new KuaishouPlayCardViewModel(card, UpdateAccountCommand)));
        var visibleSeries = board.Series.Where(item => item.Points.Any(point => point.Views.HasValue)).ToArray();
        Replace(Legend, visibleSeries.Select((item, index) => new KuaishouPlayLegendViewModel(
            item.Key, item.Name, Palette[index % Palette.Length], !_hidden.Contains(item.Key), ToggleLegendCommand)));
        ChartSeries = visibleSeries
            .Where(item => !_hidden.Contains(item.Key))
            .Select(item => new AnalyticsChartSeries(
                item.Name,
                Color.Parse(Palette[LegendIndex(visibleSeries, item.Key) % Palette.Length]),
                item.Points.Select(point => new AnalyticsChartPoint(
                    point.Date.ToString("MM-dd"),
                    point.Views ?? double.NaN,
                    point.Date.ToString("MM-dd") + "  " + (point.Views is long views ? KuaishouPlayFormat.Count(views) : "未出数"))).ToArray()))
            .ToArray();
        var tabs = new List<KuaishouPlayTabViewModel>
        {
            new(string.Empty, "全部", string.IsNullOrEmpty(_selectedKey), SelectTabCommand),
        };
        tabs.AddRange(board.Cards.Select(card => new KuaishouPlayTabViewModel(card.Key, card.Name + " · " + card.Edition, card.Key == _selectedKey, SelectTabCommand)));
        Replace(Tabs, tabs);
    }

    private static int LegendIndex(IReadOnlyList<KuaishouPlaySeries> series, string key)
    {
        for (var index = 0; index < series.Count; index++)
            if (series[index].Key == key) return index;
        return 0;
    }

    private IReadOnlyList<AnalyticsAccount> Accounts() => _accountProvider()
        .Where(account => account.Platform is PublishPlatform.KuaishouPersonalRevenue or PublishPlatform.KuaishouEnterpriseRevenue)
        .ToArray();

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }
}

public sealed class KuaishouPlayCardViewModel
{
    public KuaishouPlayCardViewModel(KuaishouPlayCard card, IAsyncRelayCommand<string?> update)
    {
        Key = card.Key;
        Title = card.Name;
        Edition = card.Edition;
        TotalText = card.TotalText;
        LatestText = card.LatestText;
        EditionBackground = card.Edition == "企业" ? "#FFF7ED" : "#EFF6FF";
        EditionForeground = card.Edition == "企业" ? "#C2410C" : "#1D4ED8";
        UpdateCommand = update;
    }

    public string Key { get; }
    public string Title { get; }
    public string Edition { get; }
    public string TotalText { get; }
    public string LatestText { get; }
    public string EditionBackground { get; }
    public string EditionForeground { get; }
    public IAsyncRelayCommand<string?> UpdateCommand { get; }
}

public sealed class KuaishouPlayLegendViewModel
{
    public KuaishouPlayLegendViewModel(string key, string name, string color, bool visible, IRelayCommand<string?> toggle)
    {
        Key = key;
        Name = name;
        Color = color;
        Opacity = visible ? 1 : 0.35;
        ToggleCommand = toggle;
    }

    public string Key { get; }
    public string Name { get; }
    public string Color { get; }
    public double Opacity { get; }
    public IRelayCommand<string?> ToggleCommand { get; }
}

public sealed class KuaishouPlayTabViewModel
{
    public KuaishouPlayTabViewModel(string key, string name, bool selected, IRelayCommand<string?> select)
    {
        Key = key;
        Name = name;
        Foreground = selected ? "#2563EB" : "#374151";
        Weight = selected ? FontWeight.SemiBold : FontWeight.Normal;
        SelectCommand = select;
    }

    public string Key { get; }
    public string Name { get; }
    public string Foreground { get; }
    public FontWeight Weight { get; }
    public IRelayCommand<string?> SelectCommand { get; }
}
