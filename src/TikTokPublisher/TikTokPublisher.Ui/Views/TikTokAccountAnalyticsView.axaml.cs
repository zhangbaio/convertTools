using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private string? _selectedAccountId;
    private string? _selectedSeriesId;
    private bool _buildingTabs;
    private int _fullSyncCount;
    private SeriesGrowthMetric _growthMetric = SeriesGrowthMetric.Views;
    private DispatcherTimer? _morningTimer;
    private readonly Dictionary<string, TikTokSeriesIdentityIndex> _identityByAccount = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _identityGeneration = new(StringComparer.Ordinal);
    private readonly HashSet<string> _identityLoading = new(StringComparer.Ordinal);
    private readonly HashSet<string> _materializedSeries = new(StringComparer.Ordinal);
    private bool _viewReady;
    private static readonly double[] SeriesDefaultWidths = [240, 96, 160, 64, 156, 108, 112, 80, 72, 80, 176];
    private static readonly double[] SeriesMinWidths = [120, 72, 80, 48, 110, 80, 88, 56, 48, 56, 150];
    private readonly double[] _seriesColumnWidths = SeriesDefaultWidths.ToArray();
    private readonly List<Grid> _seriesHeaderGrids = [];
    private readonly List<WeakReference<Grid>> _seriesRowGrids = [];
    private int _seriesResizeColumn = -1;
    private Grid? _seriesResizeGrid;
    private double _seriesResizeStartX;
    private double _seriesResizeStartWidth;

    public event Action<string>? SearchOriginalRequested;
    private const int MorningSyncHour = 8;
    private const string MorningSyncMarker = "morning_series_sync";

    public TikTokAccountAnalyticsView()
    {
        InitializeComponent();
        DailyChart.HoverTextChanged += text => DailyHoverText.Text = text ?? "";
        BuildDailyRangeButtons();
        BuildGrowthMetricButtons();
        SeriesTabs.SelectionChanged += OnSeriesTabsSelectionChanged;
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
        BuildSeriesShells();
        _viewReady = true;
        StatusText.Text = "显示全部账号。剧集每日新增从第一次同步那天开始计算。";
        if (!string.IsNullOrWhiteSpace(_selectedAccountId))
            MaterializeSeries(_selectedAccountId);
    }

    private void RefreshAccount(AccountItemViewModel account)
    {
        if (!_viewReady || _store is null) return;
        InvalidateIdentity(account.Id);
        BuildRevenueCards();
        BuildCharts();
        MaterializeSeries(account.Id, force: true);
    }

    private void InvalidateIdentity(string accountId)
    {
        _identityByAccount.Remove(accountId);
        _identityLoading.Remove(accountId);
        _identityGeneration[accountId] = _identityGeneration.GetValueOrDefault(accountId) + 1;
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

    private void BuildGrowthMetricButtons()
    {
        GrowthMetricPanel.Children.Clear();
        foreach (var (metric, label) in new (SeriesGrowthMetric Metric, string Label)[]
        {
            (SeriesGrowthMetric.Views, "有效播放量"),
            (SeriesGrowthMetric.Favorites, "收藏"),
            (SeriesGrowthMetric.Comments, "评论"),
            (SeriesGrowthMetric.Likes, "点赞"),
        })
        {
            var chosen = metric;
            var button = new Button
            {
                Content = label,
                Padding = new Avalonia.Thickness(8, 2),
                FontSize = 12,
                Tag = chosen,
            };
            button.Click += (_, _) =>
            {
                _growthMetric = chosen;
                ShowGrowth();
            };
            GrowthMetricPanel.Children.Add(button);
        }
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
                var synced = false;
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
                    synced = true;
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

                if (synced)
                    await Dispatcher.UIThread.InvokeAsync(() => RefreshAccount(account));
            }

            var finishedAt = DateTime.Now.ToString("HH:mm");
            var status = failures.Count == 0
                ? single ? $"{pending[0].DisplayName} 已更新 {finishedAt}" : $"全部账号已更新 {finishedAt}"
                : single ? $"{pending[0].DisplayName} 更新失败" : $"已更新，{failures.Count} 个账号被跳过";
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
        for (var index = 0; index < _accounts.Count; index++)
        {
            var account = _accounts[index];
            var color = Palette[index % Palette.Length];
            var revenue = _store!.GetRevenue(account.Id);
            var root = new StackPanel { Spacing = 8 };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            header.Children.Add(new TextBlock
            {
                Text = account.DisplayName,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var update = new Button
            {
                Content = "更新",
                MinWidth = 64,
                Padding = new Avalonia.Thickness(12, 0),
                FontSize = 12,
                IsEnabled = !_runningSyncs.ContainsKey(account.Id),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            update.Click += (_, _) => _ = SyncAccountsAsync([account]);
            Grid.SetColumn(update, 1);
            header.Children.Add(update);
            _accountUpdateButtons[account.Id] = update;
            root.Children.Add(header);
            var numbers = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                ColumnSpacing = 16,
            };
            numbers.Children.Add(MetricBlock("总收入", FormatMoney(revenue?.TotalAmount, revenue?.Currency), 0));
            numbers.Children.Add(MetricBlock("待结算", FormatMoney(revenue?.PendingAmount, revenue?.Currency), 1));
            numbers.Children.Add(MetricBlock("已完成", FormatMoney(revenue?.CompletedAmount, revenue?.Currency), 2));
            root.Children.Add(numbers);
            var listing = _store.GetListingCounts(account.Id, DateOnly.FromDateTime(DateTime.Now));
            root.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(revenue?.CompanyName) ? " " : revenue.CompanyName,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            root.Children.Add(new TextBlock
            {
                Text = $"已上架 {listing.Listed}    今日新增 {listing.AddedToday}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#374151")),
            });
            RevenuePanel.Children.Add(new Border
            {
                Background = Brushes.White,
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Avalonia.Thickness(16, 12),
                Margin = new Avalonia.Thickness(0, 0, 12, 12),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = root,
            });
        }

        ApplyRevenueColumns(RevenuePanel.Bounds.Width);
        ApplySyncButtons();
    }

    private void OnRevenuePanelSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1)
            return;
        ApplyRevenueColumns(e.NewSize.Width);
    }

    private void ApplyRevenueColumns(double width)
    {
        if (double.IsNaN(width) || width <= 1)
            return;
        const double minCard = 540;
        const double gap = 12;
        var columns = (int)Math.Floor((width + gap) / (minCard + gap));
        var limit = Math.Max(1, _accounts.Count);
        columns = Math.Clamp(columns, 1, Math.Min(3, limit));
        if (RevenuePanel.Columns != columns)
            RevenuePanel.Columns = columns;
    }

    private static StackPanel MetricBlock(string label, string value, int column)
    {
        var panel = new StackPanel { Spacing = 2 };
        Grid.SetColumn(panel, column);
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#6B7280")) });
        panel.Children.Add(new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeight.Bold });
        return panel;
    }

    private void BuildCharts()
    {
        if (_store is null) return;
        var loaded = _accounts.Select(account => (account, rows: _store.ListDaily(account.Id))).ToList();
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
                    ? new AnalyticsChartPoint(date.ToString("MM-dd"), row.ValidViews, $"{date:yyyy-MM-dd}  {FormatPlayCount(row.ValidViews)}")
                    : new AnalyticsChartPoint(date.ToString("MM-dd"), double.NaN, "")).ToList()));
            var byMonth = TikTokAccountAnalyticsLogic.SumByMonth(rows).ToDictionary(item => item.Month, item => item.ValidViews);
            monthly.Add(new AnalyticsChartSeries(
                account.DisplayName,
                color,
                months.Select(month => byMonth.TryGetValue(month, out var views)
                    ? new AnalyticsChartPoint(month, views, $"{month}  {FormatPlayCount(views)}")
                    : new AnalyticsChartPoint(month, double.NaN, "")).ToList()));
        }

        DailyChart.Series = daily;
        MonthlyChart.Series = monthly;
        FillLegend(DailyLegend, daily);
        FillLegend(MonthlyLegend, monthly);
        ScrollDailyChartToLatest();
    }

    private void ScrollDailyChartToLatest()
    {
        void Apply()
        {
            var max = Math.Max(0, DailyChartScroll.Extent.Width - DailyChartScroll.Viewport.Width);
            DailyChartScroll.Offset = new Vector(max, DailyChartScroll.Offset.Y);
        }

        EventHandler? afterLayout = null;
        afterLayout = (_, _) =>
        {
            DailyChartScroll.LayoutUpdated -= afterLayout;
            Apply();
        };
        DailyChartScroll.LayoutUpdated += afterLayout;
        Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);
    }

    private static void FillLegend(StackPanel panel, IReadOnlyList<AnalyticsChartSeries> series)
    {
        panel.Children.Clear();
        foreach (var item in series)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new Avalonia.Controls.Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new Avalonia.CornerRadius(5),
                Background = new SolidColorBrush(item.Color),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#374151")),
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(row);
        }
    }

    private void OnSeriesTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_buildingTabs || !ReferenceEquals(e.Source, SeriesTabs)) return;
        if (SeriesTabs.SelectedItem is not TabItem tab || tab.Tag is not string accountId) return;
        _selectedAccountId = accountId;
        MaterializeSeries(accountId);
    }

    private void BuildSeriesShells()
    {
        if (_store is null) return;
        _buildingTabs = true;
        _materializedSeries.Clear();
        _seriesHeaderGrids.Clear();
        _seriesRowGrids.Clear();
        SeriesTabs.Items.Clear();
        foreach (var account in _accounts)
        {
            SeriesTabs.Items.Add(new TabItem
            {
                Header = account.DisplayName,
                Tag = account.Id,
                Content = SeriesPlaceholder("正在加载剧集"),
            });
        }

        if (string.IsNullOrWhiteSpace(_selectedAccountId) || _accounts.All(account => account.Id != _selectedAccountId))
            _selectedAccountId = _accounts.FirstOrDefault()?.Id;
        var match = SeriesTabs.Items.OfType<TabItem>().FirstOrDefault(item => Equals(item.Tag, _selectedAccountId));
        if (match is not null)
            SeriesTabs.SelectedItem = match;
        _buildingTabs = false;
    }

    private void MaterializeSeries(string accountId, bool force = false)
    {
        if (_store is null) return;
        if (!force && _materializedSeries.Contains(accountId)) return;
        var account = _accounts.FirstOrDefault(item => item.Id == accountId);
        var tab = SeriesTabs.Items.OfType<TabItem>().FirstOrDefault(item => Equals(item.Tag, accountId));
        if (account is null || tab is null) return;

        var identity = _identityByAccount.GetValueOrDefault(accountId) ?? TikTokSeriesIdentityIndex.Empty;
        if (!_identityByAccount.ContainsKey(accountId))
            StartIdentityLoad([account]);
        var items = _store.ListLatestSeriesListing(account.Id)
            .Select(item =>
            {
                var matched = identity.Find(item.Latest.Title);
                return new SeriesListItem(account.Id, account.DisplayName, item.Latest, item.ListedOn, matched);
            })
            .ToList();
        var list = new ListBox
        {
            Background = Brushes.Transparent,
            Padding = new Avalonia.Thickness(0),
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<SeriesListItem>((item, _) =>
                item is null ? new Border() : BuildSeriesRow(item)),
        };
        list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Avalonia.Thickness(0)),
                new Setter(Layoutable.MarginProperty, new Avalonia.Thickness(0)),
                new Setter(Layoutable.MinHeightProperty, 0d),
            },
        });
        var selected = items.FirstOrDefault(item => item.Series.SeriesId == _selectedSeriesId);
        if (selected is not null)
            list.SelectedItem = selected;
        list.SelectionChanged += (_, args) =>
        {
            if (!ReferenceEquals(args.Source, list)) return;
            if (list.SelectedItem is not SeriesListItem current) return;
            _selectedAccountId = current.AccountId;
            _selectedSeriesId = current.Series.SeriesId;
            ShowGrowth();
        };

        var panel = new DockPanel();
        var headerInset = new Border { Width = 0 };
        var headerHost = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headerHost.Children.Add(BuildSeriesHeader());
        Grid.SetColumn(headerInset, 1);
        headerHost.Children.Add(headerInset);
        DockPanel.SetDock(headerHost, Dock.Top);
        panel.Children.Add(headerHost);
        list.LayoutUpdated += (_, _) => SyncHeaderInset(list, headerInset);
        panel.Children.Add(items.Count == 0
            ? SeriesPlaceholder("这个账号还没有剧集明细，点「更新」后再看。")
            : list);
        tab.Content = panel;
        _materializedSeries.Add(accountId);
    }

    private static TextBlock SeriesPlaceholder(string text) => new()
    {
        Text = text,
        Margin = new Avalonia.Thickness(8, 12, 8, 0),
        Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
    };

    private Border BuildSeriesHeader()
    {
        var grid = new Grid { ColumnDefinitions = CreateSeriesColumns() };
        for (var column = 0; column < SeriesHeaders.Length; column++)
        {
            var label = new TextBlock
            {
                Text = SeriesHeaders[column],
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(label, SeriesHeaders[column]);
            var content = new Grid();
            content.Children.Add(label);
            if (column < SeriesHeaders.Length - 1)
                content.Children.Add(CreateColumnResizer(column));
            grid.Children.Add(Cell(column, content, header: true));
        }

        _seriesHeaderGrids.Add(grid);
        ApplySeriesColumnWidths(grid);
        return TableEdge(grid, includeTop: true);
    }

    private Border CreateColumnResizer(int column)
    {
        var grip = new Border
        {
            Width = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            Tag = column,
            ZIndex = 2,
        };
        grip.PointerPressed += OnSeriesColumnResizePressed;
        grip.PointerMoved += OnSeriesColumnResizeMoved;
        grip.PointerReleased += OnSeriesColumnResizeReleased;
        return grip;
    }

    private void OnSeriesColumnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.Tag is not int column) return;
        _seriesResizeGrid = HeaderGridOf(control);
        if (_seriesResizeGrid is null) return;
        _seriesResizeColumn = column;
        _seriesResizeStartX = e.GetPosition(_seriesResizeGrid).X;
        _seriesResizeStartWidth = _seriesColumnWidths[column];
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void OnSeriesColumnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_seriesResizeColumn < 0 || _seriesResizeGrid is null) return;
        var width = Math.Max(SeriesMinWidths[_seriesResizeColumn], _seriesResizeStartWidth + e.GetPosition(_seriesResizeGrid).X - _seriesResizeStartX);
        if (Math.Abs(width - _seriesColumnWidths[_seriesResizeColumn]) < 0.5) return;
        _seriesColumnWidths[_seriesResizeColumn] = width;
        ApplyAllSeriesColumnWidths();
        e.Handled = true;
    }

    private void OnSeriesColumnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_seriesResizeColumn < 0) return;
        e.Pointer.Capture(null);
        _seriesResizeColumn = -1;
        _seriesResizeGrid = null;
        e.Handled = true;
    }

    private static Grid? HeaderGridOf(Control control)
    {
        var current = control.Parent;
        while (current is not null)
        {
            if (current is Grid grid && grid.ColumnDefinitions.Count == SeriesHeaders.Length)
                return grid;
            current = current.Parent;
        }

        return null;
    }

    private void ApplyAllSeriesColumnWidths()
    {
        foreach (var grid in _seriesHeaderGrids)
            ApplySeriesColumnWidths(grid);
        for (var index = _seriesRowGrids.Count - 1; index >= 0; index--)
        {
            if (_seriesRowGrids[index].TryGetTarget(out var grid))
                ApplySeriesColumnWidths(grid);
            else
                _seriesRowGrids.RemoveAt(index);
        }
    }

    private void ApplySeriesColumnWidths(Grid grid)
    {
        if (grid.ColumnDefinitions.Count != _seriesColumnWidths.Length) return;
        var total = _seriesColumnWidths.Sum();
        grid.Width = total;
        if (grid.Parent is Border border)
            border.Width = total;
        for (var index = 0; index < _seriesColumnWidths.Length; index++)
            grid.ColumnDefinitions[index].Width = new GridLength(_seriesColumnWidths[index]);
    }

    private static void SyncHeaderInset(ListBox list, Border spacer)
    {
        var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var inset = viewer is null ? 0 : list.Bounds.Width - viewer.Viewport.Width;
        spacer.Width = inset > 1 ? inset : 0;
    }

    private Control BuildSeriesRow(SeriesListItem item)
    {
        var grid = new Grid { ColumnDefinitions = CreateSeriesColumns() };
        var title = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleText = string.IsNullOrWhiteSpace(item.Series.Title) ? "—" : item.Series.Title;
        var titleBlock = new TextBlock
        {
            Text = titleText,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        SetCellTip(titleBlock, titleText);
        title.Children.Add(titleBlock);
        if (item.Series.PublishedOn is not null)
        {
            title.Children.Add(new TextBlock
            {
                Text = item.Series.PublishedOn.Value.ToString("yyyy-MM-dd"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
            });
        }

        grid.Children.Add(Cell(0, title));
        grid.Children.Add(Cell(1, MetricText(item.ListedOn?.ToString("yyyy-MM-dd") ?? "未知")));
        grid.Children.Add(Cell(2, MetricText(string.IsNullOrWhiteSpace(item.Identity.OriginalTitle) ? "—" : item.Identity.OriginalTitle)));
        grid.Children.Add(Cell(3, MetricText(string.IsNullOrWhiteSpace(item.Identity.EpisodeCount) ? "—" : item.Identity.EpisodeCount)));
        grid.Children.Add(Cell(4, MetricText(FormatJoined(item.Identity.QueuedAt))));
        grid.Children.Add(Cell(5, MetricText(FormatPlayCount(item.Series.ValidViews))));
        grid.Children.Add(Cell(6, MetricText(FormatWatch(item.Series.AverageWatchSeconds))));
        grid.Children.Add(Cell(7, MetricText(FormatCount(item.Series.Favorites))));
        grid.Children.Add(Cell(8, MetricText(FormatCount(item.Series.Comments))));
        grid.Children.Add(Cell(9, MetricText(FormatCount(item.Series.Likes))));
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var trend = ActionButton("播放走势", "#DBEAFE", "#60A5FA", "#1D4ED8");
        trend.Click += (_, _) => OpenTrend(item);
        var search = ActionButton("搜索原剧", "#DCFCE7", "#4ADE80", "#15803D");
        search.IsEnabled = item.Identity.HasOriginalTitle;
        search.Click += (_, _) => SearchOriginalRequested?.Invoke(item.Identity.OriginalTitle);
        actions.Children.Add(trend);
        actions.Children.Add(search);
        grid.Children.Add(Cell(10, actions));
        _seriesRowGrids.Add(new WeakReference<Grid>(grid));
        ApplySeriesColumnWidths(grid);
        return TableEdge(grid, includeTop: false);
    }

    private static readonly string[] SeriesHeaders =
    [
        "剧集", "上架日期", "原剧名", "集数", "加入时间", "有效播放量", "人均播放时长", "收藏", "评论", "点赞", "操作",
    ];

    private static Border TableEdge(Control child, bool includeTop) => new()
    {
        BorderBrush = TableLine,
        BorderThickness = includeTop ? new Avalonia.Thickness(1, 1, 0, 0) : new Avalonia.Thickness(1, 0, 0, 0),
        Child = child,
    };

    private static Border Cell(int column, Control child, bool header = false)
    {
        var border = new Border
        {
            BorderBrush = TableLine,
            BorderThickness = new Avalonia.Thickness(0, 0, 1, 1),
            Padding = new Avalonia.Thickness(8, 6),
            Background = header ? new SolidColorBrush(Color.Parse("#F9FAFB")) : Brushes.Transparent,
            Child = child,
        };
        Grid.SetColumn(border, column);
        return border;
    }

    private static Button ActionButton(string text, string background, string border, string foreground) => new()
    {
        Content = text,
        FontSize = 12,
        MinWidth = 72,
        MinHeight = 28,
        Padding = new Avalonia.Thickness(8, 0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse(background)),
        BorderBrush = new SolidColorBrush(Color.Parse(border)),
        Foreground = new SolidColorBrush(Color.Parse(foreground)),
    };

    private static readonly IBrush TableLine = new SolidColorBrush(Color.Parse("#D1D5DB"));

    private void OpenTrend(SeriesListItem item)
    {
        if (_store is null) return;
        var history = _store.ListSeriesHistory(item.AccountId, item.Series.SeriesId);
        var title = string.IsNullOrWhiteSpace(item.Series.Title) ? item.Series.SeriesId : item.Series.Title;
        var window = new TikTokSeriesTrendWindow(title, history);
        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }

    private void StartIdentityLoad(IReadOnlyList<AccountItemViewModel> missing)
    {
        var pending = new List<(string Id, TikTokAccountProfile Model, int Generation)>();
        foreach (var account in missing)
        {
            if (_identityByAccount.ContainsKey(account.Id) || !_identityLoading.Add(account.Id))
                continue;
            var generation = _identityGeneration.GetValueOrDefault(account.Id);
            pending.Add((account.Id, account.Model, generation));
        }

        if (pending.Count == 0) return;
        _ = Task.Run(() =>
        {
            var loaded = new List<(string Id, TikTokSeriesIdentityIndex Index, int Generation)>(pending.Count);
            foreach (var item in pending)
            {
                TikTokSeriesIdentityIndex index;
                try
                {
                    index = TikTokSeriesIdentityIndex.Load(item.Model);
                }
                catch (Exception ex)
                {
                    index = TikTokSeriesIdentityIndex.Empty;
                    var message = $"读取原剧名失败：{ex.Message}";
                    Dispatcher.UIThread.Post(() => _log?.Invoke(message));
                }

                loaded.Add((item.Id, index, item.Generation));
            }

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (id, index, generation) in loaded)
                {
                    _identityLoading.Remove(id);
                    if (_identityGeneration.GetValueOrDefault(id) != generation)
                        continue;
                    _identityByAccount[id] = index;
                    if (_materializedSeries.Contains(id))
                        MaterializeSeries(id, force: true);
                }
            });
        });
    }

    private static string FormatJoined(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var time) ||
               DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out time)
            ? time.ToString("yyyy-MM-dd HH:mm")
            : value;
    }

    private static TextBlock MetricText(string value)
    {
        var block = new TextBlock
        {
            Text = value,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        SetCellTip(block, value);
        return block;
    }

    private static void SetCellTip(TextBlock block, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value != "—")
            ToolTip.SetTip(block, value);
    }

    private static ColumnDefinitions CreateSeriesColumns()
    {
        var columns = new ColumnDefinitions();
        for (var index = 0; index < SeriesHeaders.Length; index++)
            columns.Add(new ColumnDefinition(1, GridUnitType.Pixel));
        return columns;
    }

    private void ShowGrowth()
    {
        if (_buildingTabs || _store is null)
            return;
        HighlightGrowthMetric();
        if (string.IsNullOrWhiteSpace(_selectedAccountId) || string.IsNullOrWhiteSpace(_selectedSeriesId))
        {
            GrowthTitle.Text = "点一部剧，查看每日增长";
            GrowthChart.Series = [];
            return;
        }

        var account = _accounts.FirstOrDefault(item => item.Id == _selectedAccountId);
        var history = _store.ListSeriesHistory(_selectedAccountId, _selectedSeriesId);
        var days = history
            .GroupBy(item => item.SnapshotDate)
            .Select(group => group.OrderByDescending(GrowthValue(_growthMetric)).First())
            .OrderBy(item => item.SnapshotDate)
            .ToList();
        var title = days.LastOrDefault()?.Title ?? history.LastOrDefault()?.Title ?? "剧集";
        var metricName = GrowthMetricName(_growthMetric);
        var prefix = $"{account?.DisplayName} · {title} · {metricName}";
        if (days.Count == 0)
        {
            GrowthTitle.Text = $"{prefix}：还没有每日数据";
            GrowthChart.Series = [];
            return;
        }

        var color = AccountColor(_selectedAccountId);
        GrowthTitle.Text = prefix;
        GrowthChart.Series =
        [
            new AnalyticsChartSeries(
                title,
                color,
                days.Select(snapshot =>
                {
                    var value = GrowthValue(_growthMetric)(snapshot);
                    var shown = _growthMetric == SeriesGrowthMetric.Views
                        ? FormatPlayCount(value)
                        : FormatCount(value);
                    return new AnalyticsChartPoint(
                        snapshot.SnapshotDate.ToString("MM-dd"),
                        value,
                        $"{snapshot.SnapshotDate:yyyy-MM-dd}  {shown}");
                }).ToList())
        ];
    }

    private void HighlightGrowthMetric()
    {
        foreach (var child in GrowthMetricPanel.Children)
        {
            if (child is not Button button) continue;
            var selected = button.Tag is SeriesGrowthMetric metric && metric == _growthMetric;
            button.FontWeight = selected ? FontWeight.Bold : FontWeight.Normal;
        }
    }

    private static Func<TikTokSeriesSnapshot, long> GrowthValue(SeriesGrowthMetric metric) => metric switch
    {
        SeriesGrowthMetric.Favorites => snapshot => snapshot.Favorites,
        SeriesGrowthMetric.Comments => snapshot => snapshot.Comments,
        SeriesGrowthMetric.Likes => snapshot => snapshot.Likes,
        _ => snapshot => snapshot.ValidViews,
    };

    private static string GrowthMetricName(SeriesGrowthMetric metric) => metric switch
    {
        SeriesGrowthMetric.Favorites => "收藏",
        SeriesGrowthMetric.Comments => "评论",
        SeriesGrowthMetric.Likes => "点赞",
        _ => "有效播放量",
    };

    private Color AccountColor(string accountId)
    {
        var index = 0;
        foreach (var account in _accounts)
        {
            if (account.Id == accountId)
                return Palette[index % Palette.Length];
            index++;
        }

        return Palette[0];
    }

    private static string FormatPlayCount(double value)
    {
        var abs = Math.Abs(value);
        if (abs > 10_000)
            return (value / 10_000d).ToString("0.##", CultureInfo.InvariantCulture) + "万";
        return Math.Truncate(value).ToString("0", CultureInfo.InvariantCulture);
    }

    private static string FormatCount(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatWatch(int seconds)
    {
        var sign = seconds < 0 ? "-" : "";
        seconds = Math.Abs(seconds);
        if (seconds <= 0) return "0s";
        var minutes = seconds / 60;
        var remain = seconds % 60;
        return minutes > 0 ? $"{sign}{minutes}m{remain:00}s" : $"{sign}{remain}s";
    }

    private static string FormatMoney(string? amount, string? currency)
    {
        if (string.IsNullOrWhiteSpace(amount)) return "-";
        var prefix = string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) ? "$" : "";
        return decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? prefix + number.ToString("#,##0.00", CultureInfo.InvariantCulture)
            : prefix + amount;
    }

    private enum SeriesGrowthMetric
    {
        Views,
        Favorites,
        Comments,
        Likes,
    }

    private sealed class SeriesListItem(
        string accountId,
        string accountName,
        TikTokSeriesSnapshot series,
        DateOnly? listedOn,
        TikTokSeriesIdentity identity)
    {
        public string AccountId { get; } = accountId;
        public string AccountName { get; } = accountName;
        public TikTokSeriesSnapshot Series { get; } = series;
        public DateOnly? ListedOn { get; } = listedOn;
        public TikTokSeriesIdentity Identity { get; } = identity;
    }
}
