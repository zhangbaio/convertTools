using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ShortDrama.Desktop.ViewModels;
using Avalonia.Controls.Primitives;

namespace ShortDrama.Desktop.Views.SettingsTabs;

public sealed class SeriesInfoSettingsTab : UserControl
{
    public SeriesInfoSettingsTab()
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

        panel.Children.Add(Hint("迁移剧目基础信息配置，供上传与提审流程复用。"));
        panel.Children.Add(Row("变现类型", BindStringCombo(nameof(ConfigWindowViewModel.WeixinMonetizationType), nameof(ConfigWindowViewModel.WeixinMonetizationTypeOptions))));
        panel.Children.Add(Row("剧目类型", BindStringCombo(nameof(ConfigWindowViewModel.WeixinDramaType), nameof(ConfigWindowViewModel.WeixinDramaTypeOptions))));
        panel.Children.Add(Row("剧目资质", BindStringCombo(nameof(ConfigWindowViewModel.WeixinDramaQualification), nameof(ConfigWindowViewModel.WeixinDramaQualificationOptions))));
        panel.Children.Add(Row("提审身份", BindStringCombo(nameof(ConfigWindowViewModel.WeixinSubmitterIdentity), nameof(ConfigWindowViewModel.WeixinSubmitterIdentityOptions))));
        panel.Children.Add(Row("试看集数", BindText(nameof(ConfigWindowViewModel.WeixinTrialEpisodes))));
        panel.Children.Add(BindCheck("填写推荐语", nameof(ConfigWindowViewModel.WeixinFillRecommendation)));
        panel.Children.Add(Row("公司名称", BindText(nameof(ConfigWindowViewModel.CompanyName))));

        return panel;
    }

    private static Control BindStringCombo(string selectedProperty, string itemsSourceProperty)
    {
        var combo = new ComboBox();
        combo[!ItemsControl.ItemsSourceProperty] = new Binding(itemsSourceProperty);
        combo[!SelectingItemsControl.SelectedItemProperty] = new Binding(selectedProperty);
        return combo;
    }

    private static CheckBox BindCheck(string content, string propertyName)
    {
        var checkBox = new CheckBox { Content = content };
        checkBox[!ToggleButton.IsCheckedProperty] = new Binding(propertyName);
        return checkBox;
    }

    private static TextBox BindText(string propertyName)
    {
        var textBox = new TextBox();
        textBox[!TextBox.TextProperty] = new Binding(propertyName);
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
            VerticalAlignment = VerticalAlignment.Center
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
