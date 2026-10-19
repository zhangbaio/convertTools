using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Models;
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
        Background = new SolidColorBrush(Color.Parse("#102A43"));
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
            Background = new SolidColorBrush(Color.Parse("#173E5E")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2C5878")),
            BorderThickness = new Avalonia.Thickness(1),
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
        var days = _history
            .GroupBy(item => item.SnapshotDate)
            .Select(group => group.OrderByDescending(ValueOf(_metric)).First())
            .OrderBy(item => item.SnapshotDate)
            .ToList();
        var seriesTitle = days.LastOrDefault()?.Title ?? _history.LastOrDefault()?.Title ?? "剧集";
        if (days.Count == 0)
        {
            _title.Text = $"{seriesTitle} · {name}：还没有每日数据";
            _chart.Series = [];
            return;
        }

        _title.Text = $"{seriesTitle} · {name}";
        _chart.Series =
        [
            new AnalyticsChartSeries(
                seriesTitle,
                Color.Parse("#36A9F5"),
                days.Select(snapshot =>
                {
                    var value = ValueOf(_metric)(snapshot);
                    var shown = _metric == SeriesGrowthMetric.Views ? FormatPlay(value) : value.ToString("N0", CultureInfo.InvariantCulture);
                    return new AnalyticsChartPoint(
                        snapshot.SnapshotDate.ToString("MM-dd"),
                        value,
                        $"{snapshot.SnapshotDate:yyyy-MM-dd}  {shown}");
                }).ToList())
        ];
    }

    private static string FormatPlay(long value)
    {
        var abs = Math.Abs(value);
        var text = abs > 10_000
            ? (value / 10_000d).ToString("0.##", CultureInfo.InvariantCulture) + "万"
            : value.ToString(CultureInfo.InvariantCulture);
        return text;
    }

    private static Func<TikTokSeriesSnapshot, long> ValueOf(SeriesGrowthMetric metric) => metric switch
    {
        SeriesGrowthMetric.Favorites => snapshot => snapshot.Favorites,
        SeriesGrowthMetric.Comments => snapshot => snapshot.Comments,
        SeriesGrowthMetric.Likes => snapshot => snapshot.Likes,
        _ => snapshot => snapshot.ValidViews,
    };

    private enum SeriesGrowthMetric
    {
        Views,
        Favorites,
        Comments,
        Likes,
    }
}
