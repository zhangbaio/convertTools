using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed class CopyrightProofMaterialCleanupDialog : Window
{
    private readonly TextBox _titles;
    private readonly CheckBox _confirmation;
    private readonly TextBlock _summary;

    private CopyrightProofMaterialCleanupDialog()
    {
        Title = "删除版权辅助材料";
        Width = 720;
        Height = 480;
        MinWidth = 600;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            RowSpacing = 12,
        };
        root.Children.Add(new TextBlock
        {
            Text = "输入 TikTok 已发布的新剧名，一行一个。系统将精确匹配项目，删除平台上的“AI 生成过程截图”和“剪辑工程文件”，取消勾选对应类型，然后提交。不会删除本地文件。",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });

        var warning = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(255, 244, 229)),
            Padding = new Thickness(10),
            Child = new TextBlock
            {
                Text = "警告：这是平台数据删除操作。任一步校验失败时不会提交该项目；多个同名项目也不会处理。",
                Foreground = Brushes.DarkOrange,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        Grid.SetRow(warning, 1);
        root.Children.Add(warning);

        _titles = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Watermark = "每行输入一个新剧名",
        };
        Grid.SetRow(_titles, 2);
        root.Children.Add(_titles);

        var confirmationPanel = new StackPanel { Spacing = 6 };
        _confirmation = new CheckBox
        {
            Content = "我确认删除上述两个平台材料并提交变更",
        };
        _summary = new TextBlock { Foreground = Brushes.Gray };
        confirmationPanel.Children.Add(_confirmation);
        confirmationPanel.Children.Add(_summary);
        Grid.SetRow(confirmationPanel, 3);
        root.Children.Add(confirmationPanel);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var execute = new Button { Content = "删除并提交", MinWidth = 110 };
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        execute.Click += (_, _) => Execute();
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(execute);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        Content = root;
        Opened += (_, _) => _titles.Focus();
    }

    public static Task<IReadOnlyList<string>?> ShowAsync(Window owner) =>
        new CopyrightProofMaterialCleanupDialog()
            .ShowDialog<IReadOnlyList<string>?>(owner);

    private void Execute()
    {
        var titles = CopyrightProofProjectMatcher.ParseNewTitles(_titles.Text ?? string.Empty);
        if (titles.Count == 0)
        {
            _summary.Text = "请至少输入一个新剧名。";
            _summary.Foreground = Brushes.IndianRed;
            return;
        }
        if (_confirmation.IsChecked != true)
        {
            _summary.Text = "请先勾选删除确认。";
            _summary.Foreground = Brushes.IndianRed;
            return;
        }

        Close(titles);
    }
}
