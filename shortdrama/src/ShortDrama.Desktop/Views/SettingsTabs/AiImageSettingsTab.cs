using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views.SettingsTabs;

public sealed class AiImageSettingsTab : UserControl
{
    public AiImageSettingsTab()
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

        panel.Children.Add(Hint("AI 图片配置迁移到全局设置。工程图生成不再暴露旧的 PIL / OpenShot / AI Prompt 分支。"));
        panel.Children.Add(Row("图片模型 ID", BindText(nameof(ConfigWindowViewModel.ImageModelId))));
        panel.Children.Add(Row("图片模型 API Key", BindText(nameof(ConfigWindowViewModel.ImageModelApiKey), isPassword: true)));
        panel.Children.Add(Row("图片模型接口", BindText(nameof(ConfigWindowViewModel.ImageModelEndpoint))));
        panel.Children.Add(Row("布局检测 Prompt", MultiLineText(nameof(ConfigWindowViewModel.PosterLayoutDetectPrompt), 120)));
        panel.Children.Add(Row("局部改字 Prompt", MultiLineText(nameof(ConfigWindowViewModel.PosterInpaintPrompt), 120)));
        panel.Children.Add(Row("局部改字安全重试 Prompt", MultiLineText(nameof(ConfigWindowViewModel.PosterInpaintSafeRetryPrompt), 120)));
        panel.Children.Add(Row("整图改字 Prompt", MultiLineText(nameof(ConfigWindowViewModel.PosterGenerationPrompt), 120)));
        panel.Children.Add(Row("整图改字安全重试 Prompt", MultiLineText(nameof(ConfigWindowViewModel.PosterGenerationSafeRetryPrompt), 120)));
        panel.Children.Add(Row("海报名 System Prompt", MultiLineText(nameof(ConfigWindowViewModel.PosterNameSystemPrompt), 100)));
        panel.Children.Add(Row("海报名 User Prompt", MultiLineText(nameof(ConfigWindowViewModel.PosterNameUserPrompt), 140)));

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
