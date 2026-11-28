using Avalonia;
using Avalonia.Controls;

namespace TikTokPublisher.Ui.Controls;

public sealed class PublishFormGrid : Panel
{
    private const double RowSpacing = 10;

    public static readonly AttachedProperty<int> ColumnSpanProperty =
        AvaloniaProperty.RegisterAttached<PublishFormGrid, Control, int>("ColumnSpan", 1);

    static PublishFormGrid() => AffectsParentMeasure<PublishFormGrid>(ColumnSpanProperty);

    public static int GetColumnSpan(Control control) => control.GetValue(ColumnSpanProperty);

    public static void SetColumnSpan(Control control, int value) => control.SetValue(ColumnSpanProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 900;
        var columns = ColumnsForWidth(width);
        var columnWidth = width / columns;
        var rows = GetRows(columns);
        var height = RowSpacing * Math.Max(0, rows.Count - 1);
        foreach (var row in rows)
        {
            foreach (var field in row)
                field.Control.Measure(new Size(columnWidth * field.Span, double.PositiveInfinity));

            height += row.Max(field => field.Control.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnsForWidth(finalSize.Width);
        var columnWidth = finalSize.Width / columns;
        var y = 0d;
        foreach (var row in GetRows(columns))
        {
            var height = row.Max(field => field.Control.DesiredSize.Height);
            foreach (var field in row)
                field.Control.Arrange(new Rect(columnWidth * field.Column, y, columnWidth * field.Span, height));

            y += height + RowSpacing;
        }

        return finalSize;
    }

    internal static int ColumnsForWidth(double width) => width >= 900 ? 3 : width >= 610 ? 2 : 1;

    private List<List<Field>> GetRows(int columns)
    {
        var rows = new List<List<Field>>();
        var column = columns;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;

            var span = Math.Clamp(GetColumnSpan(child), 1, columns);
            if (column + span > columns)
            {
                rows.Add([]);
                column = 0;
            }

            rows[^1].Add(new Field(child, column, span));
            column += span;
        }

        return rows;
    }

    private readonly record struct Field(Control Control, int Column, int Span);
}
