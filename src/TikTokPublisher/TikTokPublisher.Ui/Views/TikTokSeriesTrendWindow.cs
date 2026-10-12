using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Controls;

namespace TikTokPublisher.Ui.Views;

public sealed class TikTokSeriesTrendWindow : Window
{
    private readonly IReadOnlyList<TikTokSeriesSnapshot> _history;
    private readonly AnalyticsLineChart _chart = new();
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private SeriesGrowthMetric _metric = SeriesGrowthMetric.Views;

    public TikTokSeriesTrendWindow(string seriesTitle, IReadOnlyList<TikTokSeriesSnapshot> history)
    {
        _history = history;
        Title = $"{seriesTitle} · 播放走势";
        Width = 880;
        Height = 520;
        MinWidth = 640;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#F8FAFC"));
        Content = BuildContent(seriesTitle);
        ShowMetric();
    }

    private Control BuildContent(string seriesTitle)
    {
        var root = new DockPanel { Margin = new Avalonia.Thickness(16) };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        _title.Text = seriesTitle;
        header.Children.Add(_title);
        var metrics = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        Grid.SetColumn(metrics, 1);
        header.Children.Add(metrics);
        foreach (var (metric, label) in new (SeriesGrowthMetric Metric, string Label)[]
        {
            (SeriesGrowthMetric.Views, "有效播放量"),
            (SeriesGrowthMetric.Favorites, "收藏"),
            (SeriesGrowthMetric.Comments, "评论"),
            (SeriesGrowthMetric.Likes, "点赞"),
        })
        {
            var chosen = metric;
            var button = new Button { Content = label, Padding = new Avalonia.Thickness(8, 2), FontSize = 12, Tag = chosen };
            button.Click += (_, _) =>
            {
                _metric = chosen;
                ShowMetric();
            };
            metrics.Children.Add(button);
        }

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(12),
            Child = _chart,
        };
        root.Children.Add(card);
        return root;
    }

    private void ShowMetric()
    {
        var name = _metric switch
        {
            SeriesGrowthMetric.Favorites => "收藏",
            SeriesGrowthMetric.Comments => "评论",
            SeriesGrowthMetric.Likes => "点赞",
            _ => "有效播放量",
        };
        if (_history.Select(item => item.SnapshotDate).Distinct().Count() < 2)
        {
            _title.Text = $"{_history.LastOrDefault()?.Title ?? Title} · {name}：再更新一天后才能看每日增长";
            _chart.Series = [];
            return;
        }

        var growth = TikTokAccountAnalyticsLogic.BuildGrowth(_history, ValueOf(_metric));
        var seriesTitle = _history.LastOrDefault()?.Title ?? "剧集";
        _title.Text = $"{seriesTitle} · {name}每日新增";
        _chart.Series =
        [
            new AnalyticsChartSeries(
                seriesTitle,
                Color.Parse("#2563EB"),
                growth.Select(point => new AnalyticsChartPoint(
                    point.SpanDays == 1 ? point.ToDate.ToString("MM-dd") : $"{point.FromDate:MM-dd}~{point.ToDate:MM-dd}",
                    point.Growth,
                    Tip(point))).ToList())
        ];
    }

    private string Tip(TikTokSeriesGrowthPoint point)
    {
        var value = _metric == SeriesGrowthMetric.Views ? FormatSignedPlay(point.Growth) : FormatSignedCount(point.Growth);
        return point.SpanDays == 1
            ? $"{point.ToDate:yyyy-MM-dd} 新增 {value}"
            : $"{point.FromDate:yyyy-MM-dd} 至 {point.ToDate:yyyy-MM-dd} 跨 {point.SpanDays} 天新增 {value}";
    }

    private static Func<TikTokSeriesSnapshot, long> ValueOf(SeriesGrowthMetric metric) => metric switch
    {
        SeriesGrowthMetric.Favorites => snapshot => snapshot.Favorites,
        SeriesGrowthMetric.Comments => snapshot => snapshot.Comments,
        SeriesGrowthMetric.Likes => snapshot => snapshot.Likes,
        _ => snapshot => snapshot.ValidViews,
    };

    private static string FormatSignedPlay(long value)
    {
        var abs = Math.Abs(value);
        var text = abs > 10_000
            ? (abs / 10_000d).ToString("0.##", CultureInfo.InvariantCulture) + "万"
            : abs.ToString(CultureInfo.InvariantCulture);
        return value >= 0 ? "+" + text : "-" + text;
    }

    private static string FormatSignedCount(long value)
    {
        var text = Math.Abs(value).ToString("N0", CultureInfo.InvariantCulture);
        return value >= 0 ? "+" + text : "-" + text;
    }

    private enum SeriesGrowthMetric
    {
        Views,
        Favorites,
        Comments,
        Likes,
    }
}
