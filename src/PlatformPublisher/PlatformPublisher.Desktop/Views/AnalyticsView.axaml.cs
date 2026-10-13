using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Desktop.ViewModels;

namespace PlatformPublisher.Desktop.Views;

public partial class AnalyticsView : UserControl
{
    public AnalyticsView()
    {
        InitializeComponent();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && IsVisible && DataContext is AnalyticsViewModel vm)
                _ = vm.ActivateAsync();
        };
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AnalyticsViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出数据统计 CSV",
            SuggestedFileName = $"{vm.FromDate}_{vm.ToDate}-数据统计.csv",
            FileTypeChoices = [new FilePickerFileType("CSV 文件") { Patterns = ["*.csv"] }],
        });
        if (file is not null) vm.Export(file.Path.LocalPath);
    }
}
