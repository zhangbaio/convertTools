using Avalonia.Controls;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views;

public partial class ConfigWindow : Window
{
    public ConfigWindow()
    {
        InitializeComponent();
    }

    public ConfigWindow(ConfigWindowViewModel viewModel)
        : this()
    {
        ConfigViewHost.DataContext = viewModel;
    }

    public bool WasSaved => ConfigViewHost.WasSaved;
}
