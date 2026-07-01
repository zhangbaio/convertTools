using Avalonia.Controls;

namespace ChannelsPublisher.Desktop.Views;

/// <summary>独立运行时的窗口，仅承载 MaterialPublishView。壳（ConvertTools）里则把该视图挂成一个 Tab。</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
