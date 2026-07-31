using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Ui.Views;

public sealed class CopyrightProofExecutionModeSelector : UserControl
{
    private readonly RadioButton _generateMaterialOnly;
    private readonly RadioButton _generateAndEdit;
    private readonly TextBlock _description;

    public CopyrightProofExecutionModeSelector()
    {
        var groupName = $"copyright-proof-mode-{Guid.NewGuid():N}";
        _generateMaterialOnly = new RadioButton
        {
            Content = "仅生成证明材料",
            GroupName = groupName,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _generateAndEdit = new RadioButton
        {
            Content = "生成材料并编辑 TikTok",
            GroupName = groupName,
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _description = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
        };

        _generateMaterialOnly.IsCheckedChanged += (_, _) => UpdateDescription();
        _generateAndEdit.IsCheckedChanged += (_, _) => UpdateDescription();

        var choices = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
        };
        choices.Children.Add(_generateMaterialOnly);
        choices.Children.Add(_generateAndEdit);

        var content = new StackPanel { Spacing = 5 };
        content.Children.Add(new TextBlock
        {
            Text = "执行模式",
            FontWeight = FontWeight.SemiBold,
        });
        content.Children.Add(choices);
        content.Children.Add(_description);
        Content = content;
        UpdateDescription();
    }

    public CopyrightProofExecutionMode ExecutionMode =>
        _generateMaterialOnly.IsChecked == true
            ? CopyrightProofExecutionMode.GenerateMaterialOnly
            : CopyrightProofExecutionMode.GenerateAndEdit;

    private void UpdateDescription()
    {
        _description.Text = ExecutionMode == CopyrightProofExecutionMode.GenerateMaterialOnly
            ? "只生成或复用本地证明材料；不会打开、编辑或提交 TikTok 版权证明页面。"
            : "生成或复用证明材料后，继续编辑并提交 TikTok 版权证明页面。";
    }
}

public sealed class CopyrightProofExecutionModeDialog : Window
{
    private readonly CopyrightProofExecutionModeSelector _selector = new();

    private CopyrightProofExecutionModeDialog(string title, string message)
    {
        Title = title;
        Width = 620;
        MinWidth = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = 14,
        };
        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetRow(_selector, 1);
        root.Children.Add(_selector);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var execute = new Button { Content = "开始执行", MinWidth = 108 };
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        execute.Click += (_, _) => Close(_selector.ExecutionMode);
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(execute);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
    }

    public static Task<CopyrightProofExecutionMode?> ShowAsync(
        Window owner,
        string title,
        string message)
    {
        var dialog = new CopyrightProofExecutionModeDialog(title, message);
        return dialog.ShowDialog<CopyrightProofExecutionMode?>(owner);
    }
}
