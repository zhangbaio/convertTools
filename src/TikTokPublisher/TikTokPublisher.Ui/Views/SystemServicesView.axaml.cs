using Avalonia.Controls;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class SystemServicesView : UserControl
{
    public SystemServicesView() => InitializeComponent();

    public void Bind(SystemServicesViewModel vm) => DataContext = vm;
}
