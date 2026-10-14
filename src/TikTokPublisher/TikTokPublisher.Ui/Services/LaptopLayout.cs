namespace TikTokPublisher.Ui.Services;

/// <summary>
/// 按屏幕工作区计算主窗口尺寸，并在较矮或较窄的屏幕上使用紧凑密度。
/// 尺寸单位是设备无关像素。
/// </summary>
public static class LaptopLayout
{
    public const string MainWindowPlacementKey = "ui.main_window_placement";

    public const double PreferredWindowWidth = 1496;
    public const double PreferredWindowHeight = 860;
    public const double FillRatio = 0.92;

    public const double CompactWorkAreaWidth = 1440;
    public const double CompactWorkAreaHeight = 900;
    public const double ShortNavWorkAreaWidth = 1280;

    public const double ExpandedSidebarWidth = 198;
    public const double CompactSidebarWidth = 168;

    public readonly record struct WindowFit(double Width, double Height, double MinWidth, double MinHeight);

    public static bool IsCompact(double workWidthDip, double workHeightDip) =>
        workHeightDip < CompactWorkAreaHeight || workWidthDip < CompactWorkAreaWidth;

    public static bool ShouldShortenNav(double workWidthDip) =>
        workWidthDip < ShortNavWorkAreaWidth;

    /// <summary>
    /// 目标尺寸不超过 1496×860，也不超过工作区的 92%。屏幕更小时把最小尺寸降到仍能放进工作区。
    /// </summary>
    public static WindowFit FitWindow(double workWidthDip, double workHeightDip)
    {
        var maxWidth = Math.Max(800, workWidthDip - 24);
        var maxHeight = Math.Max(560, workHeightDip - 24);
        var minWidth = Math.Min(1100, maxWidth);
        var minHeight = Math.Min(680, maxHeight);
        var width = Math.Clamp(Math.Min(PreferredWindowWidth, workWidthDip * FillRatio), minWidth, maxWidth);
        var height = Math.Clamp(Math.Min(PreferredWindowHeight, workHeightDip * FillRatio), minHeight, maxHeight);
        return new WindowFit(Math.Round(width), Math.Round(height), Math.Round(minWidth), Math.Round(minHeight));
    }

    public static bool PlacementFits(double width, double height, double workWidthDip, double workHeightDip)
    {
        if (double.IsNaN(width) || double.IsNaN(height) || width < 640 || height < 480)
            return false;

        return width <= workWidthDip + 8 && height <= workHeightDip + 8;
    }

    /// <summary>
    /// 把列宽收到可视宽度内。可缩小的部分按各列相对最小宽度的余量分摊；已经到最小宽度的列不再缩小。
    /// 可视区域比最小宽度总和还窄时，返回最小宽度，由表格自己出横向滚动条。
    /// </summary>
    public static double[] FitColumns(IReadOnlyList<double> preferred, IReadOnlyList<double> minimum, double availableWidth)
    {
        var count = Math.Min(preferred.Count, minimum.Count);
        var widths = new double[count];
        if (count == 0)
            return widths;

        double preferredSum = 0;
        double minimumSum = 0;
        for (var i = 0; i < count; i++)
        {
            var min = Math.Min(minimum[i], preferred[i]);
            widths[i] = Math.Max(preferred[i], min);
            preferredSum += widths[i];
            minimumSum += min;
        }

        if (availableWidth <= 0 || preferredSum <= availableWidth + 0.5)
            return widths;

        if (minimumSum >= availableWidth - 0.5)
        {
            for (var i = 0; i < count; i++)
                widths[i] = Math.Min(minimum[i], preferred[i]);
            return widths;
        }

        var ratio = (preferredSum - availableWidth) / (preferredSum - minimumSum);
        for (var i = 0; i < count; i++)
        {
            var min = Math.Min(minimum[i], preferred[i]);
            var extra = widths[i] - min;
            if (extra <= 0)
                continue;
            widths[i] = min + extra * (1 - ratio);
        }

        return widths;
    }
}
