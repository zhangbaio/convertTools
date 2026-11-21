using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TikTokPublisher.Ui.Controls;

public sealed record AnalyticsAccountSeries(string Key, AnalyticsChartSeries Series);

public sealed class AnalyticsDashboardSummary : StackPanel
{
    public AnalyticsDashboardSummary()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 12;
    }

    public void Update(IReadOnlyList<AnalyticsAccountSeries> accounts, params (string Label, string Value)[] extra)
    {
        Children.Clear();
        var points = Aggregate(accounts, "全部账号").Points.Where(point => double.IsFinite(point.Value)).ToArray();
        AddMetric(points.LastOrDefault() is { } latest ? $"最近有数据日 · {latest.Label}" : "最近有数据日",
            points.Length > 0 ? Format(points[^1].Value) : "—");
        AddMetric("区间累计播放", points.Length > 0 ? Format(points.Sum(point => point.Value)) : "—");
        foreach (var (label, value) in extra)
            AddMetric(label, value);
    }

    private void AddMetric(string label, string value)
    {
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock { Text = label, Foreground = Brush.Parse("#475569"), FontSize = 12 });
        content.Children.Add(new TextBlock { Text = value, Foreground = Brush.Parse("#0F172A"), FontSize = 24, FontWeight = FontWeight.Bold });
        Children.Add(new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            BorderBrush = Brush.Parse("#E2E8F0"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 9),
            MinWidth = 138,
            Child = content,
        });
    }

    public static string Format(double value) => value switch
    {
        >= 100_000_000 => (value / 100_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "亿",
        >= 10_000 => (value / 10_000).ToString("0.##", CultureInfo.InvariantCulture) + "万",
        _ => value.ToString("N0", CultureInfo.InvariantCulture),
    };

    public static AnalyticsChartSeries Aggregate(IReadOnlyList<AnalyticsAccountSeries> accounts, string name)
    {
        var series = accounts.Select(account => account.Series).ToArray();
        var axis = series.FirstOrDefault()?.Points ?? [];
        return new AnalyticsChartSeries(name, Color.Parse("#2563EB"), axis.Select((point, index) =>
        {
            var values = series.Select(item => item.Points[index].Value).Where(double.IsFinite).ToArray();
            var total = values.Length == 0 ? double.NaN : values.Sum();
            return new AnalyticsChartPoint(point.Label, total, $"{point.Label} · {total:N0}");
        }).ToArray());
    }
}

public sealed class AnalyticsAccountRanking : StackPanel
{
    public AnalyticsAccountRanking() => Spacing = 6;

    public void Update(IReadOnlyList<AnalyticsAccountSeries> accounts, string? selectedKey, Action<string?> select)
    {
        Children.Clear();
        var reset = new Button { Content = "全部账号", IsEnabled = !string.IsNullOrEmpty(selectedKey) };
        reset.Click += (_, _) => select(null);
        Children.Add(reset);

        var ranked = accounts.Select(account => new
        {
            Account = account,
            Values = account.Series.Points.Where(point => double.IsFinite(point.Value)).Select(point => point.Value).ToArray(),
        }).Where(item => item.Values.Length > 0)
          .Select(item => (item.Account, Total: item.Values.Sum()))
          .OrderByDescending(item => item.Total).ToArray();

        if (ranked.Length == 0)
        {
            Children.Add(new TextBlock { Text = "区间内暂无播放数据", Foreground = Brush.Parse("#64748B") });
            return;
        }

        var max = Math.Max(1, ranked[0].Total);
        foreach (var (account, total) in ranked)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new TextBlock { Text = account.Series.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            var value = new TextBlock { Text = AnalyticsDashboardSummary.Format(total), FontWeight = FontWeight.SemiBold };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            var content = new StackPanel { Spacing = 3 };
            content.Children.Add(row);
            content.Children.Add(new ProgressBar { Value = 100 * total / max, Height = 4, Foreground = new SolidColorBrush(account.Series.Color) });
            var button = new Button
            {
                Content = content,
                Background = Brush.Parse(account.Key == selectedKey ? "#DBEAFE" : "#F8FAFC"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 4),
            };
            button.Click += (_, _) => select(account.Key);
            Children.Add(button);
        }
    }
}
