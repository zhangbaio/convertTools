using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ChannelsPublisher.Desktop.Views;
using ConvertTools.App.ViewModels;

namespace ConvertTools.App.Views;

public partial class ShellWindow : Window
{
    // 各 Tab 视图缓存：首次进入创建并加入 ContentArea，之后只切 IsVisible（保活）
    private readonly Dictionary<string, Control> _views = new();

    public ShellWindow()
    {
        InitializeComponent();
        TabBar.SelectionChanged += OnTabChanged;
        Loaded += (_, _) =>
        {
            if (TabBar.SelectedItem is null && TabBar.ItemCount > 0)
                TabBar.SelectedIndex = 0; // 默认「首页」
        };
    }

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
        => ShowTab(TabBar.SelectedItem as NavTab);

    private void ShowTab(NavTab? tab)
    {
        foreach (var v in _views.Values) v.IsVisible = false;
        if (tab is null) return;

        if (!_views.TryGetValue(tab.Key, out var view))
        {
            view = CreateView(tab.Key);
            _views[tab.Key] = view;
            ContentArea.Children.Add(view);
        }
        view.IsVisible = true;
    }

    private static Control CreateView(string key) => key switch
    {
        "publish" => new MaterialPublishView(),          // 复用素材发布视图
        "home" => new HomeView(),
        "transcode" => new TranscodeView(),
        "cost_report" => new CostReportView(),
        "project_info" => new ProjectInfoView(),
        "settings" => new SettingsView(),
        _ => new TextBlock
        {
            Text = $"（{key} 待接入 convertTools 功能）",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.Gray,
            FontSize = 16,
        },
    };
}
