using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ShortDrama.Desktop.ViewModels;

namespace ShortDrama.Desktop.Views.SettingsTabs;

public sealed class CostReportSettingsTab : UserControl
{
    public CostReportSettingsTab()
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

        panel.Children.Add(Hint("保留当前仓库的报表渲染能力，维护项目级成本报表基础参数。"));
        panel.Children.Add(Row("报表模板 (docx)", BuildFileRow(nameof(ConfigWindowViewModel.TemplateDocxPath), BrowseTemplateDocx_Click)));
        panel.Children.Add(Row("成本报表底图", BuildFileRow(nameof(ConfigWindowViewModel.CostReportBaseImagePath), BrowseBaseImage_Click)));
        panel.Children.Add(Row("演员总片酬占比", BindText(nameof(ConfigWindowViewModel.CostReportActorPayRatio))));
        panel.Children.Add(Row("法定代表人 / 总编审", BindText(nameof(ConfigWindowViewModel.CostReportLegalRepresentative))));

        return panel;
    }

    private static Control BuildFileRow(string propertyName, EventHandler<RoutedEventArgs> handler)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8
        };
        var textBox = BindText(propertyName);
        grid.Children.Add(textBox);
        Grid.SetColumn(textBox, 0);
        var button = new Button { Content = "浏览", MinWidth = 72 };
        button.Click += handler;
        grid.Children.Add(button);
        Grid.SetColumn(button, 1);
        return grid;
    }

    private static async void BrowseTemplateDocx_Click(object? sender, RoutedEventArgs e)
    {
        await BrowseFileAsync(sender, "选择成本报表模板", [new FilePickerFileType("Word") { Patterns = ["*.docx"] }], (vm, path) => vm.SetTemplateDocxPath(path));
    }

    private static async void BrowseBaseImage_Click(object? sender, RoutedEventArgs e)
    {
        await BrowseFileAsync(sender, "选择成本报表底图", [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }], (vm, path) => vm.SetCostReportBaseImagePath(path));
    }

    private static async Task BrowseFileAsync(
        object? sender,
        string title,
        IReadOnlyList<FilePickerFileType> fileTypes,
        Action<ConfigWindowViewModel, string> setter)
    {
        if (sender is not Control { DataContext: ConfigWindowViewModel viewModel } control ||
            TopLevel.GetTopLevel(control) is not Window window)
        {
            return;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = fileTypes
        });

        var file = files.FirstOrDefault();
        if (file is not null)
        {
            setter(viewModel, file.Path.LocalPath);
        }
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
