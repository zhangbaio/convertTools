using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Controls;
using TikTokPublisher.Ui.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class TikTokAccountAnalyticsView : UserControl
{
    private static readonly Color[] Palette =
    [
        Color.Parse("#2563EB"),
        Color.Parse("#DC2626"),
        Color.Parse("#059669"),
        Color.Parse("#D97706"),
        Color.Parse("#7C3AED"),
        Color.Parse("#0891B2"),
        Color.Parse("#BE185D"),
        Color.Parse("#4B5563"),
    ];

    private static readonly (int Days, string Label)[] DailyRanges =
    [
        (7, "7天"),
        (30, "30天"),
        (90, "90天"),
        (0, "全部"),
    ];

    private IReadOnlyList<AccountItemViewModel> _accounts = [];
    private Action<string>? _log;
    private bool _enabled;
    private TikTokAccountAnalyticsStore? _store;
    private readonly Dictionary<string, CancellationTokenSource> _runningSyncs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _accountUpdateButtons = new(StringComparer.Ordinal);
    private int _dailyRangeDays = 30;
    private string? _trendAccountId;
    private int _fullSyncCount;
    private DispatcherTimer? _morningTimer;
    private bool _viewReady;
    private const int MorningSyncHour = 8;
    private const string MorningSyncMarker = "morning_series_sync";

    public TikTokAccountAnalyticsView()
    {
        InitializeComponent();
        MonthlyChart.HoverTextChanged += text => DailyHoverText.Text = text ?? string.Empty;
        DailyChart.HoverTextChanged += text => DailyHoverText.Text = text ?? "";
        BuildDailyRangeButtons();
    }

    public void Bind(IReadOnlyList<AccountItemViewModel> accounts, Action<string>? log)
    {
        _accounts = accounts;
        _log = log;
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
            return;

        _enabled = enabled;
        if (!enabled)
        {
            StopMorningTimer();
            CancelRunningSyncs();
            return;
        }

        _store ??= new TikTokAccountAnalyticsStore();
        EnsureMorningTimer();
    }

    public void Activate()
    {
        if (!_enabled || _store is null || _viewReady) return;
        StatusText.Text = "正在加载统计";
        Dispatcher.UIThread.Post(LoadVisiblePage, DispatcherPriority.Background);
    }

    private void LoadVisiblePage()
    {
        if (_viewReady || _store is null) return;
        BuildRevenueCards();
        BuildCharts();
        _viewReady = true;
        StatusText.Text = "显示全部账号。剧集每日新增从第一次同步那天开始计算。";
    }

    private void RefreshOverview()
    {
        if (!_viewReady || _store is null) return;
        BuildRevenueCards();
        BuildCharts();
    }

    private void EnsureMorningTimer()
    {
        if (!_enabled || _morningTimer is not null) return;
        _morningTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _morningTimer.Tick += (_, _) => _ = CheckMorningSyncAsync();
        _morningTimer.Start();
        _ = CheckMorningSyncAsync();
    }

    private void StopMorningTimer()
    {
        if (_morningTimer is null)
            return;

        _morningTimer.Stop();
        _morningTimer = null;
    }

    private void CancelRunningSyncs()
    {
        foreach (var cts in _runningSyncs.Values.Distinct())
            cts.Cancel();
    }

    private async Task CheckMorningSyncAsync()
    {
        if (!_enabled || _store is null || _accounts.Count == 0 || _runningSyncs.Count > 0) return;
        var now = DateTime.Now;
        if (now.Hour < MorningSyncHour) return;
        var today = DateOnly.FromDateTime(now);
        if (_store.GetMarkerDate(MorningSyncMarker) == today) return;
        if (_accounts.All(account => _store.HasSeriesSnapshot(account.Id, today)))
        {
            _store.SetMarkerDate(MorningSyncMarker, today);
            return;
        }

        _store.SetMarkerDate(MorningSyncMarker, today);
        _log?.Invoke("08:00 自动更新全部账号的剧集数据");
        await SyncAccountsAsync(_accounts.ToList());
    }

    private void BuildDailyRangeButtons()
    {
        DailyRangePanel.Children.Clear();
        foreach (var range in DailyRanges)
        {
            var days = range.Days;
            var button = new Button
            {
                Content = range.Label,
                Padding = new Avalonia.Thickness(8, 2),
                FontSize = 12,
            };
            button.Click += (_, _) =>
            {
                _dailyRangeDays = days;
                BuildCharts();
            };
            DailyRangePanel.Children.Add(button);
        }
    }

    private async void OnRefreshClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await SyncAccountsAsync(_accounts.ToList());

    private async Task SyncAccountsAsync(IReadOnlyList<AccountItemViewModel> accounts)
    {
        if (!_enabled || _store is null || accounts.Count == 0) return;
        var pending = accounts.Where(account => !_runningSyncs.ContainsKey(account.Id)).ToList();
        if (pending.Count == 0) return;

        var single = pending.Count == 1 && accounts.Count == 1;
        var cts = new CancellationTokenSource();
        foreach (var account in pending)
            _runningSyncs[account.Id] = cts;
        if (!single)
            _fullSyncCount++;
        ApplySyncButtons();
        ErrorText.Text = "";
        var failures = new List<string>();
        try
        {
            foreach (var account in pending)
            {
                cts.Token.ThrowIfCancellationRequested();
                var displayName = account.DisplayName;
                await Dispatcher.UIThread.InvokeAsync(() => StatusText.Text = $"正在更新 {displayName}");
                try
                {
                    await TikTokAccountAnalyticsSyncService.SyncAsync(
                        account.Model,
                        _store,
                        message => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            StatusText.Text = message;
                            _log?.Invoke(message);
                        }),
                        cts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add(ex.Message);
                    var message = ex.Message;
                    await Dispatcher.UIThread.InvokeAsync(() => _log?.Invoke(message));
                }

                await Dispatcher.UIThread.InvokeAsync(RefreshOverview);
            }

            var finishedAt = DateTime.Now.ToString("HH:mm");
            var status = failures.Count == 0
                ? single ? $"{pending[0].DisplayName} 已更新 {finishedAt}" : $"全部账号已更新 {finishedAt}"
                : single ? $"{pending[0].DisplayName} 更新失败" : $"已更新，{failures.Count} 个账号更新有错误";
            var error = string.Join("\n", failures);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText.Text = status;
                ErrorText.Text = error;
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusText.Text = "已停止更新");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var account in pending)
                    _runningSyncs.Remove(account.Id);
                if (!single)
                    _fullSyncCount = Math.Max(0, _fullSyncCount - 1);
                ApplySyncButtons();
            });
            cts.Dispose();
        }
    }

    private void ApplySyncButtons()
    {
        RefreshButton.IsEnabled = _fullSyncCount == 0;
        StopButton.IsEnabled = _runningSyncs.Count > 0;
        foreach (var (accountId, button) in _accountUpdateButtons)
            button.IsEnabled = !_runningSyncs.ContainsKey(accountId);
    }

    private void OnStopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        CancelRunningSyncs();

    private void BuildRevenueCards()
    {
        RevenuePanel.Children.Clear();
        _accountUpdateButtons.Clear();
        RevenuePanel.Children.Add(new Border
        {
            Background = Brush.Parse("#F1F5F9"),
            Padding = new Thickness(8, 6),
            Child = CreateRevenueRow(["账号 / 公司", "总收入", "待结算", "已完成", "已上架", "今日新增"], header: true),
        });
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var account in _accounts)
        {
            var revenue = _store!.GetRevenue(account.Id);
            var listing = _store.GetListingCounts(account.Id, today);
            var title = string.IsNullOrWhiteSpace(revenue?.CompanyName) ? account.DisplayName : $"{account.DisplayName} · {revenue.CompanyName}";
            var row = CreateRevenueRow([
                title,
                FormatMoney(revenue?.TotalAmount, revenue?.Currency),
                FormatMoney(revenue?.PendingAmount, revenue?.Currency),
                FormatMoney(revenue?.CompletedAmount, revenue?.Currency),
                FormatCount(listing.Listed),
                FormatCount(listing.AddedToday),
            ]);
            var update = new Button { Content = "更新", VerticalAlignment = VerticalAlignment.Center };
            update.Click += (_, _) => _ = SyncAccountsAsync([account]);
            Grid.SetColumn(update, 6);
            row.Children.Add(update);
            _accountUpdateButtons[account.Id] = update;
            RevenuePanel.Children.Add(new Border
            {
                Background = Brushes.White,
                BorderBrush = Brush.Parse("#E2E8F0"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 4),
                Child = row,
            });
        }

        ApplySyncButtons();
    }

    private static Grid CreateRevenueRow(string[] values, bool header = false)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*,*,*,*,*,72"), ColumnSpacing = 8 };
        for (var index = 0; index < values.Length; index++)
        {
            var text = new TextBlock
            {
                Text = values[index],
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(text, text.Text);
            Grid.SetColumn(text, index);
            row.Children.Add(text);
        }
        return row;
    }

    private void BuildCharts()
    {
        if (_store is null) return;
        var loaded = _accounts.Select(account => (account, rows: _store.ListDaily(account.Id))).ToList();
        var lastFetched = loaded.SelectMany(item => item.rows).Select(row => row.FetchedAt).Max();
        LastFetchedText.Text = lastFetched is { } time ? $"播放数据最近拉取 · {time.ToLocalTime():yyyy-MM-dd HH:mm}" : "播放数据最近拉取 · 暂无记录";
        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = _dailyRangeDays <= 0 ? DateOnly.MinValue : today.AddDays(-(_dailyRangeDays - 1));
        var dates = loaded.SelectMany(item => item.rows.Select(row => row.Date))
            .Where(date => date >= start)
            .Distinct()
            .OrderBy(date => date)
            .ToList();
        var months = loaded.SelectMany(item => TikTokAccountAnalyticsLogic.SumByMonth(item.rows).Select(row => row.Month))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(month => month, StringComparer.Ordinal)
            .ToList();
        var daily = new List<AnalyticsChartSeries>();
        var monthly = new List<AnalyticsChartSeries>();
        for (var index = 0; index < loaded.Count; index++)
        {
            var (account, rows) = loaded[index];
            var color = Palette[index % Palette.Length];
            var byDate = rows.ToDictionary(row => row.Date);
            daily.Add(new AnalyticsChartSeries(
                account.DisplayName,
                color,
                dates.Select(date => byDate.TryGetValue(date, out var row)
                    ? new AnalyticsChartPoint(date.ToString("MM-dd"), row.ValidViews, $"{date:yyyy-MM-dd}  {FormatCount(row.ValidViews)}")
                    : new AnalyticsChartPoint(date.ToString("MM-dd"), double.NaN, "")).ToList()));
            var byMonth = TikTokAccountAnalyticsLogic.SumByMonth(rows).ToDictionary(item => item.Month, item => item.ValidViews);
            monthly.Add(new AnalyticsChartSeries(
                account.DisplayName,
                color,
                months.Select(month => byMonth.TryGetValue(month, out var views)
                    ? new AnalyticsChartPoint(month, views, $"{month}  {FormatCount(views)}")
                    : new AnalyticsChartPoint(month, double.NaN, "")).ToList()));
        }

        var accounts = loaded.Select((item, index) => new AnalyticsAccountSeries(item.account.Id, daily[index])).ToArray();
        var monthlyAccounts = loaded.Select((item, index) => new AnalyticsAccountSeries(item.account.Id, monthly[index])).ToArray();
        var selected = Array.FindIndex(accounts, item => item.Key == _trendAccountId);
        if (selected < 0)
            _trendAccountId = null;
        var compare = CompareToggle.IsChecked == true;
        ChartScopeText.Text = compare ? "全部账号 · 悬停查看各账号数据" : selected < 0 ? "全部账号" : daily[selected].Name;
        DailyHoverText.Text = "";
        DailyChart.Series = compare ? daily : selected < 0 ? [AnalyticsDashboardSummary.Aggregate(accounts, "全部账号")] : [daily[selected]];
        MonthlyChart.Series = compare ? monthly : selected < 0 ? [AnalyticsDashboardSummary.Aggregate(monthlyAccounts, "全部账号")] : [monthly[selected]];
        var listings = loaded.Select(item => _store.GetListingCounts(item.account.Id, today)).ToArray();
        DashboardSummary.Update(accounts,
            ("已上架", FormatCount(listings.Sum(item => (long)item.Listed))),
            ("今日新增上架", FormatCount(listings.Sum(item => (long)item.AddedToday))));
        AccountRanking.Update(accounts, compare ? null : _trendAccountId, key =>
        {
            _trendAccountId = key;
            var showAllAccounts = key is null;
            if (CompareToggle.IsChecked != showAllAccounts)
                CompareToggle.IsChecked = showAllAccounts;
            else
                BuildCharts();
        });
    }

    private void OnCompareToggleChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => BuildCharts();

    private void OnMonthlyToggleChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DailyChart is null || MonthlyChart is null) return;
        var monthly = MonthlyToggle.IsChecked == true;
        DailyChart.IsVisible = !monthly;
        MonthlyChart.IsVisible = monthly;
        DailyHoverText.Text = "";
    }

    private static string FormatCount(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatMoney(string? amount, string? currency)
    {
        if (string.IsNullOrWhiteSpace(amount)) return "-";
        var prefix = string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) ? "$" : "";
        return decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? prefix + number.ToString("#,##0.00", CultureInfo.InvariantCulture)
            : prefix + amount;
    }
}
