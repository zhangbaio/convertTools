using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TikTokPublisher.Ui.Views;

public sealed class TextPromptDialog : Window
{
    private readonly TextBox _input;
    private bool _accepted;

    public string? Result { get; private set; }

    private TextPromptDialog(string title, string label, string defaultValue)
    {
        Title = title;
        Width = 440;
        Height = 180;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
        });
        _input = new TextBox { Text = defaultValue, MinWidth = 380 };
        root.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var ok = new Button { Content = "确定", MinWidth = 88 };
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        ok.Click += (_, _) =>
        {
            _accepted = true;
            Close();
        };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        Opened += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
        Closed += (_, _) =>
        {
            if (_accepted)
                Result = _input.Text?.Trim();
        };
    }

    public static async Task<string?> ShowAsync(
        Window? owner,
        string title,
        string label,
        string defaultValue = "")
    {
        var dialog = new TextPromptDialog(title, label, defaultValue);
        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            await dialog.ShowDialog(new Window());
        return dialog.Result;
    }
}
