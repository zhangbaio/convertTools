using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views.SettingsTabs;

public sealed class FeishuSettingsTab : UserControl
{
    public FeishuSettingsTab()
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

        panel.Children.Add(Hint("迁移飞书步骤通知配置。当前先接任务队列执行通知，命令机器人和更复杂的交互能力后续再补。"));
        panel.Children.Add(BindCheck("启用飞书通知", nameof(ConfigWindowViewModel.FeishuNotificationEnabled)));
        panel.Children.Add(Row("App ID", BindText(nameof(ConfigWindowViewModel.FeishuAppId))));
        panel.Children.Add(Row("App Secret", BindText(nameof(ConfigWindowViewModel.FeishuAppSecret), isPassword: true)));
        panel.Children.Add(Row("接收 ID", BindText(nameof(ConfigWindowViewModel.FeishuReceiveId))));
        panel.Children.Add(Row("接收 ID 类型", BuildReceiveIdTypeCombo()));
        panel.Children.Add(BindCheck("步骤开始时通知", nameof(ConfigWindowViewModel.FeishuNotifyOnStepStart)));
        panel.Children.Add(BindCheck("步骤成功时通知", nameof(ConfigWindowViewModel.FeishuNotifyOnStepSuccess)));
        panel.Children.Add(BindCheck("步骤失败时通知", nameof(ConfigWindowViewModel.FeishuNotifyOnStepFailure)));
        panel.Children.Add(BindCheck("队列完成时汇总通知", nameof(ConfigWindowViewModel.FeishuNotifyOnQueueSummary)));
        panel.Children.Add(BindCheck("登录二维码提醒", nameof(ConfigWindowViewModel.FeishuNotifyOnLoginQr)));
        panel.Children.Add(Row("通知步骤", MultiLineText(nameof(ConfigWindowViewModel.FeishuNotifyStepKeysText), 180)));

        return panel;
    }

    private static ComboBox BuildReceiveIdTypeCombo()
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        combo[!ItemsControl.ItemsSourceProperty] = new Binding(nameof(ConfigWindowViewModel.FeishuReceiveIdTypeOptions));
        combo[!SelectingItemsControl.SelectedItemProperty] = new Binding(nameof(ConfigWindowViewModel.FeishuReceiveIdType));
        return combo;
    }

    private static CheckBox BindCheck(string content, string propertyName)
    {
        var checkBox = new CheckBox { Content = content };
        checkBox[!ToggleButton.IsCheckedProperty] = new Binding(propertyName);
        return checkBox;
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
        textBox.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
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
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
    }
}
