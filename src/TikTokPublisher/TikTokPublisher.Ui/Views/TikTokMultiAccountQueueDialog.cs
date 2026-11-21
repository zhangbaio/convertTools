using Avalonia.Controls;
using Avalonia.Layout;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public sealed class TikTokMultiAccountQueueDialog : Window
{
    public TikTokMultiAccountQueueDialog(IReadOnlyList<TikTokQueueImportTarget> targets, string? currentAccountId)
    {
        var platformLabels = targets.Select(item => item.PlatformLabel).Distinct().ToArray();
        Title = platformLabels.Length == 1 && !string.IsNullOrWhiteSpace(platformLabels[0])
            ? $"加入{platformLabels[0]}队列"
            : "选择目标账号";
        Width = 520;
        Height = 480;
        MinWidth = 360;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var checks = new List<(CheckBox Box, TikTokQueueImportTarget Target)>();
        var list = new StackPanel { Spacing = 8 };
        foreach (var target in targets)
        {
            var box = new CheckBox
            {
                Content = string.IsNullOrWhiteSpace(target.PlatformLabel)
                    ? target.AccountProfileName
                    : $"{target.PlatformLabel} · {target.AccountProfileName}",
                IsChecked = string.Equals(target.AccountProfileId, currentAccountId, StringComparison.Ordinal),
            };
            ToolTip.SetTip(box, target.WorkspaceRoot);
            checks.Add((box, target));
            list.Children.Add(box);
        }

        var ok = new Button
        {
            Content = "加入队列",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true,
            IsEnabled = checks.Any(item => item.Box.IsChecked == true),
        };
        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true,
        };
        ok.Classes.Add("primaryAction");
        foreach (var (box, _) in checks)
            box.IsCheckedChanged += (_, _) => ok.IsEnabled = checks.Any(item => item.Box.IsChecked == true);
        ok.Click += (_, _) =>
        {
            var picked = checks.Where(item => item.Box.IsChecked == true).Select(item => item.Target).ToList();
            Close(picked.Count == 0 ? null : picked);
        };
        cancel.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel { Margin = new Avalonia.Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = list, Margin = new Avalonia.Thickness(0, 0, 0, 12) });
        Content = root;
    }
}
