using Avalonia;
using Avalonia.Controls;

namespace TikTokPublisher.Ui.Views;

/// <summary>
/// 表头和虚拟化行共用同一组列宽。宽度在测量时写入，避免新行先按 XAML 默认宽度排一帧。
/// </summary>
public class QueueTableGrid : Grid
{
    public static readonly AttachedProperty<double[]?> ColumnWidthsProperty =
        AvaloniaProperty.RegisterAttached<QueueTableGrid, Control, double[]?>(
            "ColumnWidths",
            inherits: true);

    public static double[]? GetColumnWidths(Control control) => control.GetValue(ColumnWidthsProperty);

    public static void SetColumnWidths(Control control, double[]? value) =>
        control.SetValue(ColumnWidthsProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        ApplySharedColumnWidths();
        return base.MeasureOverride(availableSize);
    }

    private void ApplySharedColumnWidths()
    {
        var widths = GetValue(ColumnWidthsProperty);
        if (widths is null || ColumnDefinitions.Count != widths.Length)
            return;

        var total = 0d;
        for (var i = 0; i < widths.Length; i++)
        {
            var width = widths[i];
            total += width;
            var definition = ColumnDefinitions[i];
            if (!definition.Width.IsAbsolute || Math.Abs(definition.Width.Value - width) > 0.5)
                definition.Width = new GridLength(width);
        }

        if (double.IsNaN(Width) || Math.Abs(Width - total) > 0.5)
            Width = total;
    }
}
