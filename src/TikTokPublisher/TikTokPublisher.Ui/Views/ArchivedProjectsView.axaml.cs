using Avalonia.Controls;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class ArchivedProjectsView : UserControl
{
    public ArchivedProjectsView() => InitializeComponent();

    public void Bind(ArchivedProjectsViewModel vm) => DataContext = vm;
}
