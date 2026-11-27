using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Ui.Views;

internal static class VideoDedupTemplatePicker
{
    internal static Task<string?> SelectAsync(Window owner, string title, string description,
        IReadOnlyList<VideoDedupTemplateEntry> entries, string? selectedId)
    {
        var items = entries.Select(template => new ComboBoxItem { Content = template.Name, Tag = template.Id }).ToArray();
        var combo = new ComboBox
        {
            ItemsSource = items,
            SelectedItem = items.FirstOrDefault(item => Equals(item.Tag, selectedId)) ?? items.FirstOrDefault(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var ok = new Button { Content = "确定", IsEnabled = combo.SelectedItem is not null, Classes = { "primaryAction" } };
        var cancel = new Button { Content = "取消" };
        var dialog = new Window
        {
            Title = title,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap },
                    combo,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } },
                },
            },
        };
        combo.SelectionChanged += (_, _) => ok.IsEnabled = combo.SelectedItem is not null;
        ok.Click += (_, _) => dialog.Close((combo.SelectedItem as ComboBoxItem)?.Tag as string);
        cancel.Click += (_, _) => dialog.Close(null);
        return dialog.ShowDialog<string?>(owner);
    }
}
