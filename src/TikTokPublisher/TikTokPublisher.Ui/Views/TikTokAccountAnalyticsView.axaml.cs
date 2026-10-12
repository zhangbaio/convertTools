using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using TikTokPublisher.Core.Models;
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

    private IReadOnlyList<AccountItemViewModel> _accounts = [];
    private Action<string>? _log;
    private TikTokAccountAnalyticsStore? _store;
    private CancellationTokenSource? _syncCancellation;
    private string? _selectedAccountId;
    private string? _selectedSeriesId;

    public TikTokAccountAnalyticsView()
    {
        InitializeComponent();
        SeriesList.SelectionChanged += (_, _) => ShowSelectedSeries();
    }

    public void Bind(IReadOnlyList<AccountItemViewModel> accounts, Action<string>? log)
    {
        _accounts = accounts;
        _log = log;
        _store = new TikTokAccountAnalyticsStore();
        Reload();
    }

    public void Reload()
    {
        if (_store is null) return;
        RevenuePanel.Children.Clear();
        var loaded = _accounts.Select(account => (account, rows: _store.ListDaily(account.Id))).ToList();
        var dates = loaded.SelectMany(item => item.rows.Select(row => row.Date)).Distinct().OrderBy(date => date).ToList();
        var months = loaded.SelectMany(item => TikTokAccountAnalyticsLogic.SumByMonth(item.rows).Select(row => row.Month))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(month => month, StringComparer.Ordinal)
            .ToList();
        var daily = new List<AnalyticsChartSeries>();
        var monthly = new List<AnalyticsChartSeries>();
        var index = 0;
        foreach (var (account, rows) in loaded)
        {
            var color = Palette[index % Palette.Length];
            index++;
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
            RevenuePanel.Children.Add(BuildRevenueCard(account, color));
        }

        DailyChart.Series = daily;
        MonthlyChart.Series = monthly;
        ReloadSeriesList();
    }

    private async void OnRefreshClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_store is null || _syncCancellation is not null) return;
        _syncCancellation = new CancellationTokenSource();
        RefreshButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        ErrorText.Text = "";
        var failures = new List<string>();
        try
        {
            foreach (var account in _accounts.ToList())
            {
                _syncCancellation.Token.ThrowIfCancellationRequested();
                StatusText.Text = $"正在更新 {account.DisplayName}";
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
                        _syncCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add(ex.Message);
                    _log?.Invoke(ex.Message);
                }

                Reload();
            }

            StatusText.Text = failures.Count == 0
                ? $"全部账号已更新 {DateTime.Now:HH:mm}"
                : $"已更新，{failures.Count} 个账号被跳过";
            ErrorText.Text = string.Join("\n", failures);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已停止更新";
        }
        finally
        {
            _syncCancellation.Dispose();
            _syncCancellation = null;
            RefreshButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            Reload();
        }
    }

    private void OnStopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _syncCancellation?.Cancel();

    private Control BuildRevenueCard(AccountItemViewModel account, Color color)
    {
        var revenue = _store!.GetRevenue(account.Id);
        var panel = new StackPanel { Spacing = 2, MinWidth = 180 };
        panel.Children.Add(new TextBlock { Text = account.DisplayName, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(color) });
        panel.Children.Add(new TextBlock { Text = "总收入  " + FormatMoney(revenue?.TotalAmount, revenue?.Currency), FontSize = 13 });
        panel.Children.Add(new TextBlock { Text = "已完成  " + FormatMoney(revenue?.CompletedAmount, revenue?.Currency), FontSize = 13 });
        if (!string.IsNullOrWhiteSpace(revenue?.CompanyName))
            panel.Children.Add(new TextBlock { Text = revenue.CompanyName, FontSize = 12, Foreground = Brushes.Gray, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis });
        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(12, 8),
            Child = panel,
        };
    }

    private void ReloadSeriesList()
    {
        if (_store is null) return;
        var selected = SeriesList.SelectedItem as SeriesRow;
        SeriesList.Items.Clear();
        foreach (var account in _accounts)
        {
            foreach (var (series, previousDate, previousViews) in _store.ListLatestSeriesWithPrevious(account.Id))
            {
                string growthText;
                if (previousDate is null || previousViews is null)
                    growthText = "-";
                else
                {
                    var span = series.SnapshotDate.DayNumber - previousDate.Value.DayNumber;
                    var growth = series.ValidViews - previousViews.Value;
                    growthText = span <= 1 ? FormatSigned(growth) : $"{FormatSigned(growth)}（{span}天）";
                }
                SeriesList.Items.Add(new SeriesRow(
                    account.Id,
                    account.DisplayName,
                    series,
                    $"{account.DisplayName}    {series.Title}    {FormatCount(series.ValidViews)}    新增 {growthText}"));
            }
        }

        if (selected is not null)
        {
            var match = SeriesList.Items.OfType<SeriesRow>()
                .FirstOrDefault(item => item.AccountId == selected.AccountId && item.Series.SeriesId == selected.Series.SeriesId);
            if (match is not null) SeriesList.SelectedItem = match;
        }
    }

    private void ShowSelectedSeries()
    {
        if (_store is null || SeriesList.SelectedItem is not SeriesRow row)
        {
            GrowthTitle.Text = "点一部剧查看每天新增播放";
            GrowthChart.Series = [];
            return;
        }

        _selectedAccountId = row.AccountId;
        _selectedSeriesId = row.Series.SeriesId;
        var history = _store.ListSeriesHistory(row.AccountId, row.Series.SeriesId);
        var growth = TikTokAccountAnalyticsLogic.BuildGrowth(history);
        GrowthTitle.Text = $"{row.AccountName} · {row.Series.Title}";
        GrowthChart.Series =
        [
            new AnalyticsChartSeries(
                row.Series.Title,
                Palette[0],
                growth.Select(point => new AnalyticsChartPoint(
                    point.SpanDays == 1 ? point.ToDate.ToString("MM-dd") : $"{point.FromDate:MM-dd}~{point.ToDate:MM-dd}",
                    point.Growth,
                    point.SpanDays == 1
                        ? $"{point.ToDate:yyyy-MM-dd} 新增 {FormatSigned(point.Growth)}"
                        : $"{point.FromDate:yyyy-MM-dd} 至 {point.ToDate:yyyy-MM-dd} 跨 {point.SpanDays} 天新增 {FormatSigned(point.Growth)}")).ToList())
        ];
    }

    private static string FormatCount(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatSigned(long value) => value >= 0 ? "+" + FormatCount(value) : FormatCount(value);

    private static string FormatMoney(string? amount, string? currency)
    {
        if (string.IsNullOrWhiteSpace(amount)) return "-";
        var prefix = string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) ? "$" : "";
        return decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? prefix + number.ToString("#,##0.00", CultureInfo.InvariantCulture)
            : prefix + amount;
    }

    private sealed record SeriesRow(string AccountId, string AccountName, TikTokSeriesSnapshot Series, string Text)
    {
        public override string ToString() => Text;
    }
}
