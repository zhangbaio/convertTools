using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views.SettingsTabs;

public sealed class AiTextSettingsTab : UserControl
{
    public AiTextSettingsTab()
    {
        Content = new ScrollViewer
        {
            Content = BuildContent()
        };
    }

    private static Control BuildContent()
    {
        var panel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16)
        };

        panel.Children.Add(Hint("AI 文本配置迁移到全局设置，供项目改写流程统一复用。"));
        panel.Children.Add(Row("接口地址", BindText(nameof(ConfigWindowViewModel.AiTextEndpoint))));
        panel.Children.Add(Row("API Key", BindText(nameof(ConfigWindowViewModel.AiTextApiKey), isPassword: true)));
        panel.Children.Add(Row("模型名称", BindText(nameof(ConfigWindowViewModel.AiTextModel))));
        panel.Children.Add(Row("请求超时", BindText(nameof(ConfigWindowViewModel.AiTextTimeoutSeconds))));
        panel.Children.Add(Row("单批项目数", BindText(nameof(ConfigWindowViewModel.AiTextMaxBatchSize))));
        panel.Children.Add(Row("System Prompt", MultiLineText(nameof(ConfigWindowViewModel.AiTextSystemPrompt), 120)));
        panel.Children.Add(Row("Batch Prompt", MultiLineText(nameof(ConfigWindowViewModel.AiTextBatchPrompt), 220)));
        panel.Children.Add(Row("Retry Prompt", MultiLineText(nameof(ConfigWindowViewModel.AiTextRetryPrompt), 140)));

        return panel;
    }

    private static TextBox BindText(string propertyName, bool isPassword = false)
    {
        var textBox = new TextBox();
        textBox[!TextBox.TextProperty] = new Binding(propertyName);
        if (isPassword)
        {
            textBox.PasswordChar = '*';
        }
        return textBox;
    }

    private static TextBox MultiLineText(string propertyName, double minHeight)
    {
        var textBox = BindText(propertyName);
        textBox.AcceptsReturn = true;
        textBox.TextWrapping = TextWrapping.Wrap;
        textBox.MinHeight = minHeight;
        return textBox;
    }

    private static Control Row(string label, Control editor)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("220,*"),
            ColumnSpacing = 12
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Top
        });
        grid.Children.Add(editor);
        Grid.SetColumn(editor, 1);
        return grid;
    }

    private static TextBlock Hint(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };
    }
}
