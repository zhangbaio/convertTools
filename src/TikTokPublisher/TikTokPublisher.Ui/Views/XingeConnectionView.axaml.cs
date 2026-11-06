using Avalonia.Controls;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class XingeConnectionView : UserControl
{
    public XingeConnectionView() => InitializeComponent();

    public void Bind(XingeConnectionViewModel viewModel) => DataContext = viewModel;
}
