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

    public static readonly StyledProperty<double> PointSpacingProperty =
        AvaloniaProperty.Register<AnalyticsLineChart, double>(nameof(PointSpacing));

    public IReadOnlyList<AnalyticsChartSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public double PointSpacing
    {
        get => GetValue(PointSpacingProperty);
        set => SetValue(PointSpacingProperty, value);
    }

    public event Action<string?>? HoverTextChanged;

    private int _hoverIndex = -1;
    private double _plotLeft;
    private double _plotTop;
    private double _plotBottom;
    private double _slot;
    private int _pointCount;

    static AnalyticsLineChart()
    {
        AffectsRender<AnalyticsLineChart>(SeriesProperty, PointSpacingProperty);
        AffectsMeasure<AnalyticsLineChart>(SeriesProperty, PointSpacingProperty);
        SeriesProperty.Changed.AddClassHandler<AnalyticsLineChart>((chart, _) => chart._hoverIndex = -1);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Series?.FirstOrDefault()?.Points.Count ?? 0;
        var spacing = PointSpacing;
        if (spacing <= 0 || count <= 1)
        {
            var plainWidth = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
            var plainHeight = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
            return new Size(plainWidth, plainHeight);
        }
        var width = 70 + (count - 1) * spacing + 24;
        var offeredWidth = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 180 : availableSize.Height;
        return new Size(Math.Max(offeredWidth, width), height);
    }

    public override void Render(DrawingContext context)
    {
        var area = new Rect(0, 0, Math.Max(0, Bounds.Width), Math.Max(0, Bounds.Height));
        context.FillRectangle(new SolidColorBrush(Color.Parse("#12324D")), area);
        var series = (Series ?? []).Where(item => item.Points.Any(point => !double.IsNaN(point.Value))).ToList();
        if (series.Count == 0)
        {
            _pointCount = 0;
            DrawMessage(context, area, "暂无数据");
            return;
        }

        const double left = 58;
        const double top = 12;
        var right = Math.Max(left + 10, area.Width - 12);
        var bottom = Math.Max(top + 10, area.Height - 28);
        var max = series.SelectMany(item => item.Points).Where(point => !double.IsNaN(point.Value)).Select(point => point.Value).DefaultIfEmpty(1).Max();
        if (max <= 0) max = 1;
        var labels = series[0].Points.Select(point => point.Label).ToList();
        var slot = PointSpacing > 0
            ? PointSpacing
            : labels.Count <= 1 ? right - left : (right - left) / Math.Max(1, labels.Count - 1);
        if (PointSpacing > 0)
            right = left + slot * Math.Max(0, labels.Count - 1);
        _plotLeft = left;
        _plotTop = top;
        _plotBottom = bottom;
        _slot = Math.Max(1, slot);
        _pointCount = labels.Count;

        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#2C5878")), 1);
        var axisPen = new Pen(new SolidColorBrush(Color.Parse("#376581")), 1);
        for (var step = 0; step <= 4; step++)
        {
            var y = bottom - (bottom - top) * step / 4;
            context.DrawLine(gridPen, new Point(left, y), new Point(right, y));
            var text = FormatAxis(max * step / 4);
            DrawText(context, text, new Point(4, y - 8), 11, "#C4D5E4");
        }

        context.DrawLine(axisPen, new Point(left, bottom), new Point(right, bottom));
        var labelStep = PointSpacing >= 32 ? 1 : Math.Max(1, labels.Count / 6);
        for (var index = 0; index < labels.Count; index += labelStep)
        {
            var x = left + slot * index;
            DrawText(context, labels[index], new Point(x - 18, bottom + 6), 11, "#C4D5E4");
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

        DrawCallout(context, area, series, left, top, bottom, slot, max);
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var index = HitIndex(e.GetPosition(this));
        if (index == _hoverIndex)
            return;
        _hoverIndex = index;
        HoverTextChanged?.Invoke(index < 0 ? null : Describe(index));
        InvalidateVisual();
    }

    protected override void OnPointerExited(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverIndex < 0)
            return;
        _hoverIndex = -1;
        HoverTextChanged?.Invoke(null);
        InvalidateVisual();
    }

    private int HitIndex(Point position)
    {
        if (_pointCount <= 0)
            return -1;
        if (position.Y < _plotTop - 6 || position.Y > Bounds.Height)
            return -1;
        if (position.X < _plotLeft - 16 || position.X > _plotLeft + _slot * Math.Max(0, _pointCount - 1) + 16)
            return -1;
        if (_pointCount == 1)
            return 0;
        return Math.Clamp((int)Math.Round((position.X - _plotLeft) / _slot), 0, _pointCount - 1);
    }

    private string? Describe(int index)
    {
        var series = Series;
        if (series is null)
            return null;
        string? when = null;
        var parts = new List<string>();
        foreach (var item in series)
        {
            if (index < 0 || index >= item.Points.Count)
                continue;
            var point = item.Points[index];
            if (double.IsNaN(point.Value))
                continue;
            SplitTip(point, out var date, out var value);
            when ??= date;
            parts.Add($"{item.Name} {value}");
        }

        return parts.Count == 0 ? null : $"{when}  {string.Join("   ", parts)}";
    }

    private void DrawCallout(DrawingContext context, Rect area, IReadOnlyList<AnalyticsChartSeries> series, double left, double top, double bottom, double slot, double max)
    {
        if (_hoverIndex < 0)
            return;

        var rows = new List<(string Text, Color Color)>();
        string? when = null;
        var x = left + slot * _hoverIndex;
        foreach (var item in series)
        {
            if (_hoverIndex >= item.Points.Count)
                continue;
            var point = item.Points[_hoverIndex];
            if (double.IsNaN(point.Value))
                continue;
            SplitTip(point, out var date, out var value);
            when ??= date;
            rows.Add(($"{item.Name}  {value}", item.Color));
            var y = bottom - (bottom - top) * (point.Value / max);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#12324D")), new Pen(new SolidColorBrush(item.Color), 2), new Point(x, y), 5, 5);
        }

        if (rows.Count == 0)
            return;

        context.DrawLine(
            new Pen(new SolidColorBrush(Color.Parse("#376581")), 1, DashStyle.Dash),
            new Point(x, top),
            new Point(x, bottom));

        const double pad = 8;
        const double lineHeight = 18;
        var texts = new List<FormattedText>();
        if (!string.IsNullOrWhiteSpace(when))
            texts.Add(MakeText(when, 12, FontWeight.SemiBold, Color.Parse("#F7FBFF")));
        foreach (var row in rows)
            texts.Add(MakeText(row.Text, 12, FontWeight.Normal, row.Color));

        var width = texts.Max(text => text.Width) + pad * 2;
        var height = pad * 2 + texts.Count * lineHeight - 4;
        var boxX = x + 12;
        if (boxX + width > area.Width - 4)
            boxX = Math.Max(4, x - 12 - width);
        var box = new Rect(boxX, Math.Max(4, top), width, height);
        context.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(245, 23, 62, 94)),
            new Pen(new SolidColorBrush(Color.Parse("#376581")), 1),
            new RoundedRect(box, 6));
        var textY = box.Y + pad;
        foreach (var text in texts)
        {
            context.DrawText(text, new Point(box.X + pad, textY));
            textY += lineHeight;
        }
    }

    private static void SplitTip(AnalyticsChartPoint point, out string when, out string value)
    {
        var tip = point.ToolTip ?? "";
        var split = tip.IndexOf("  ", StringComparison.Ordinal);
        if (split > 0)
        {
            when = tip[..split];
            value = tip[(split + 2)..];
            return;
        }

        when = point.Label;
        value = FormatAxis(point.Value);
    }

    private static FormattedText MakeText(string text, double size, FontWeight weight, Color color) =>
        new(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei", FontStyle.Normal, weight),
            size,
            new SolidColorBrush(color));

    private static void DrawMessage(DrawingContext context, Rect bounds, string message)
    {
        var formatted = new FormattedText(
            message,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei"),
            14,
            new SolidColorBrush(Color.Parse("#C4D5E4")));
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
        if (Math.Abs(value) > 10_000) return $"{value / 10_000:0.##}万";
        return value.ToString("0");
    }
}
