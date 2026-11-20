using Avalonia.Data.Converters;

namespace TikTokPublisher.Ui.Controls;

public static class UploadQueueToolbarHeight
{
    private const double ReservedTableHeight = 144;

    public static IValueConverter Limit { get; } = new FuncValueConverter<double, double>(
        height => height > 0 ? Math.Max(0, height - ReservedTableHeight) : double.PositiveInfinity);
}
