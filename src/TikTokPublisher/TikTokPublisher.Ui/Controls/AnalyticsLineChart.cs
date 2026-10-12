using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TikTokPublisher.Ui.Controls;

public sealed record AnalyticsChartPoint(string Label, double Value, string ToolTip);

public sealed record AnalyticsChartSeries(string Name, Color Color, IReadOnlyList<AnalyticsChartPoint> Points);

public sealed class AnalyticsLineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<AnalyticsChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<AnalyticsLineChart, IReadOnlyList<AnalyticsChartSeries>?>(nameof(Series));

    public IReadOnlyList<AnalyticsChartSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    static AnalyticsLineChart()
    {
        AffectsRender<AnalyticsLineChart>(SeriesProperty);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        context.FillRectangle(Brushes.White, bounds);
        var series = (Series ?? []).Where(item => item.Points.Any(point => !double.IsNaN(point.Value))).ToList();
        if (series.Count == 0)
        {
            DrawMessage(context, bounds, "暂无数据");
            return;
        }

        const double left = 58;
        const double top = 12;
        var right = Math.Max(left + 10, bounds.Width - 12);
        var bottom = Math.Max(top + 10, bounds.Height - 28);
        var max = series.SelectMany(item => item.Points).Where(point => !double.IsNaN(point.Value)).Select(point => point.Value).DefaultIfEmpty(1).Max();
        if (max <= 0) max = 1;
        var labels = series[0].Points.Select(point => point.Label).ToList();
        var slot = labels.Count <= 1 ? right - left : (right - left) / (labels.Count - 1);

        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#E5E7EB")), 1);
        var axisPen = new Pen(new SolidColorBrush(Color.Parse("#9CA3AF")), 1);
        for (var step = 0; step <= 4; step++)
        {
            var y = bottom - (bottom - top) * step / 4;
            context.DrawLine(gridPen, new Point(left, y), new Point(right, y));
            var text = FormatAxis(max * step / 4);
            DrawText(context, text, new Point(4, y - 8), 11, "#6B7280");
        }

        context.DrawLine(axisPen, new Point(left, bottom), new Point(right, bottom));
        var labelStep = Math.Max(1, labels.Count / 6);
        for (var index = 0; index < labels.Count; index += labelStep)
        {
            var x = left + slot * index;
            DrawText(context, labels[index], new Point(x - 18, bottom + 6), 11, "#6B7280");
        }

        foreach (var item in series)
        {
            var pen = new Pen(new SolidColorBrush(item.Color), 2);
            var brush = new SolidColorBrush(item.Color);
            Point? previous = null;
            for (var index = 0; index < item.Points.Count; index++)
            {
                var point = item.Points[index];
                if (double.IsNaN(point.Value))
                {
                    previous = null;
                    continue;
                }
                var x = left + slot * index;
                var y = bottom - (bottom - top) * (point.Value / max);
                var current = new Point(x, y);
                if (previous is not null)
                    context.DrawLine(pen, previous.Value, current);
                context.DrawEllipse(brush, null, current, 3, 3);
                previous = current;
            }
        }
    }

    private static void DrawMessage(DrawingContext context, Rect bounds, string message)
    {
        var formatted = new FormattedText(
            message,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei"),
            14,
            new SolidColorBrush(Color.Parse("#6B7280")));
        context.DrawText(formatted, new Point((bounds.Width - formatted.Width) / 2, (bounds.Height - formatted.Height) / 2));
    }

    private static void DrawText(DrawingContext context, string text, Point origin, double size, string color)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei"),
            size,
            new SolidColorBrush(Color.Parse(color)));
        context.DrawText(formatted, origin);
    }

    private static string FormatAxis(double value)
    {
        if (value >= 100_000_000) return $"{value / 100_000_000:0.#}亿";
        if (value >= 10_000) return $"{value / 10_000:0.#}万";
        return value.ToString("0");
    }
}
