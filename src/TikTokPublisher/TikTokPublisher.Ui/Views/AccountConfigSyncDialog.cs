using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed class AccountConfigSyncDialog : Window
{
    private bool _accepted;

    private AccountConfigSyncDialog(TikTokAccountSyncPreview preview, bool refreshed)
    {
        Title = "同步账号配置";
        Width = 860;
        Height = 560;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var ok = new Button { Content = "确认同步", MinWidth = 108 };
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        ok.Click += (_, _) =>
        {
            _accepted = true;
            Close();
        };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        var header = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top);
        var user = string.IsNullOrWhiteSpace(preview.TtUserName) ? "当前登录用户" : preview.TtUserName;
        var direction = preview.Direction == TikTokAccountSyncDirection.PushToCloud
            ? "上传到管理系统"
            : "从管理系统同步到本机";
        header.Children.Add(new TextBlock
        {
            Text = $"{direction}。当前 TT 用户：{user}",
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        header.Children.Add(new TextBlock
        {
            Text = refreshed
                ? "云端账号刚刚有变化，请重新确认。工作目录不会被修改。本机字段有内容时优先保留本机。"
                : $"有差异的账号 {preview.Rows.Count} 个，内容一致的 {preview.UnchangedCount} 个。工作目录不会被修改。本机字段有内容时优先保留本机。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
        });
        root.Children.Add(header);

        var list = new StackPanel { Spacing = 8 };
        if (preview.Rows.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "没有需要处理的差异。",
                Foreground = Brushes.DimGray,
            });
            ok.IsEnabled = false;
        }

        foreach (var row in preview.Rows)
            list.Children.Add(CreateRow(row));
        root.Children.Add(new ScrollViewer { Content = list });
        Content = root;
    }

    public static async Task<bool> ShowAsync(Window? owner, TikTokAccountSyncPreview preview, bool refreshed)
    {
        var dialog = new AccountConfigSyncDialog(preview, refreshed);
        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            await dialog.ShowDialog(new Window());
        return dialog._accepted;
    }

    private static Control CreateRow(TikTokAccountSyncRow row)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,220"),
            Margin = new Thickness(0, 0, 0, 4),
        };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = row.Title, FontWeight = FontWeight.SemiBold });
        text.Children.Add(new TextBlock
        {
            Text = row.Difference,
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var choice = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(choice, 1);
        if (row.Kind == TikTokAccountSyncRowKind.Different)
            choice.Children.Add(CreateChoiceCombo(row));
        else if (row.Kind == TikTokAccountSyncRowKind.LocalOnly)
            AddLocalChoices(choice, row);
        else
            AddCloudChoices(choice, row);
        grid.Children.Add(choice);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#E5E7EB")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    private static ComboBox CreateChoiceCombo(TikTokAccountSyncRow row)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.Items.Add(new ComboBoxItem { Content = "合并，本机优先", Tag = TikTokAccountSyncContentChoice.MergeLocalFirst });
        combo.Items.Add(new ComboBoxItem { Content = "全部用本机", Tag = TikTokAccountSyncContentChoice.PreferLocal });
        combo.Items.Add(new ComboBoxItem { Content = "全部用云端", Tag = TikTokAccountSyncContentChoice.PreferCloud });
        combo.Items.Add(new ComboBoxItem { Content = "不处理", Tag = TikTokAccountSyncContentChoice.Skip });
        combo.SelectedIndex = row.ContentChoice switch
        {
            TikTokAccountSyncContentChoice.PreferLocal => 1,
            TikTokAccountSyncContentChoice.PreferCloud => 2,
            TikTokAccountSyncContentChoice.Skip => 3,
            _ => 0,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: TikTokAccountSyncContentChoice choice })
                row.ContentChoice = choice;
        };
        return combo;
    }

    private static void AddLocalChoices(StackPanel panel, TikTokAccountSyncRow row)
    {
        if (string.IsNullOrWhiteSpace(row.TikTokUsername))
            return;
        var upload = new CheckBox { Content = "上传到管理系统", IsChecked = row.UploadLocal };
        upload.IsCheckedChanged += (_, _) => row.UploadLocal = upload.IsChecked == true;
        var deleteLocal = new CheckBox
        {
            Content = row.LocalBusy ? "正在上传，不能从本机删除" : "从本机删除",
            IsChecked = row.DeleteLocal,
            IsEnabled = !row.LocalBusy,
        };
        deleteLocal.IsCheckedChanged += (_, _) => row.DeleteLocal = deleteLocal.IsChecked == true;
        panel.Children.Add(upload);
        panel.Children.Add(deleteLocal);
    }

    private static void AddCloudChoices(StackPanel panel, TikTokAccountSyncRow row)
    {
        var add = new CheckBox { Content = "添加到本机", IsChecked = row.AddFromCloud };
        add.IsCheckedChanged += (_, _) => row.AddFromCloud = add.IsChecked == true;
        var deleteCloud = new CheckBox { Content = "从云端删除", IsChecked = row.DeleteCloud };
        deleteCloud.IsCheckedChanged += (_, _) => row.DeleteCloud = deleteCloud.IsChecked == true;
        panel.Children.Add(add);
        panel.Children.Add(deleteCloud);
    }
}
