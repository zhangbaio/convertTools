using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ShortDrama.Desktop.ViewModels;
using Avalonia.Controls.Primitives;

namespace ShortDrama.Desktop.Views.SettingsTabs;

public sealed class BasicSettingsTab : UserControl
{
    public BasicSettingsTab()
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

        panel.Children.Add(Hint("迁移基础运行参数和工程图模板参数。工程图生成方式固定为图片框选模板。"));
        panel.Children.Add(Row("搜索分页大小", BindText(nameof(ConfigWindowViewModel.SearchPageSize))));
        panel.Children.Add(BindCheck("无头模式 (Headless)", nameof(ConfigWindowViewModel.WeixinHeadless)));
        panel.Children.Add(Row("操作间隔 (slow_mo_ms)", BindText(nameof(ConfigWindowViewModel.WeixinSlowMoMs))));
        panel.Children.Add(Row("运行结束保持浏览器", BindText(nameof(ConfigWindowViewModel.WeixinKeepOpenSeconds))));
        panel.Children.Add(Row("扫码登录超时", BindText(nameof(ConfigWindowViewModel.WeixinLoginTimeoutSeconds))));
        panel.Children.Add(BindCheck("启用提交 (submit_enabled)", nameof(ConfigWindowViewModel.WeixinSubmitEnabled)));
        panel.Children.Add(BindCheck("上传出错时人工接管", nameof(ConfigWindowViewModel.WeixinPauseOnError)));
        panel.Children.Add(BindCheck("保存 HTML 快照", nameof(ConfigWindowViewModel.WeixinSaveHtml)));
        panel.Children.Add(BindCheck("保存页面文本", nameof(ConfigWindowViewModel.WeixinSaveText)));
        panel.Children.Add(Row("提审记录目录", BuildSubmissionReportDirRow()));
        panel.Children.Add(Row("工程图生成方式", ReadOnlyText(nameof(ConfigWindowViewModel.ProjectImageGenerationModeDisplay))));
        panel.Children.Add(Row("工程图模板根目录", BuildTemplateRootRow()));
        panel.Children.Add(Row("截图模板", BuildTemplateCombo()));
        panel.Children.Add(Row("模板目录", ReadOnlyText(nameof(ConfigWindowViewModel.ProjectImageTemplateDir))));
        panel.Children.Add(Row("工程图数量", BindText(nameof(ConfigWindowViewModel.ProjectImageCount))));

        return panel;
    }

    private static Control BuildSubmissionReportDirRow()
    {
        var grid = BuildActionRow(BindText(nameof(ConfigWindowViewModel.WeixinSubmissionReportDir)));
        var browseButton = new Button { Content = "浏览", MinWidth = 72 };
        browseButton.Click += BrowseSubmissionReportDir_Click;
        var clearButton = new Button { Content = "清空", MinWidth = 72 };
        clearButton.Click += ClearSubmissionReportDir_Click;
        grid.Children.Add(browseButton);
        Grid.SetColumn(browseButton, 1);
        grid.Children.Add(clearButton);
        Grid.SetColumn(clearButton, 2);
        return grid;
    }

    private static Control BuildTemplateRootRow()
    {
        var grid = BuildActionRow(BindText(nameof(ConfigWindowViewModel.ProjectImageTemplateRoot)));
        var browseButton = new Button { Content = "浏览", MinWidth = 72 };
        browseButton.Click += BrowseTemplateRoot_Click;
        grid.Children.Add(browseButton);
        Grid.SetColumn(browseButton, 1);
        return grid;
    }

    private static Control BuildTemplateCombo()
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberBinding = new Binding("Label")
        };
        combo[!ItemsControl.ItemsSourceProperty] = new Binding(nameof(ConfigWindowViewModel.ProjectImageTemplateOptions));
        combo[!SelectingItemsControl.SelectedItemProperty] = new Binding(nameof(ConfigWindowViewModel.SelectedProjectImageTemplateOption));
        return combo;
    }

    private static Grid BuildActionRow(Control editor)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 8
        };
        grid.Children.Add(editor);
        Grid.SetColumn(editor, 0);
        return grid;
    }

    private static CheckBox BindCheck(string content, string propertyName)
    {
        var checkBox = new CheckBox { Content = content };
        checkBox[!ToggleButton.IsCheckedProperty] = new Binding(propertyName);
        return checkBox;
    }

    private static TextBox BindText(string propertyName)
    {
        var textBox = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        textBox[!TextBox.TextProperty] = new Binding(propertyName);
        return textBox;
    }

    private static TextBox ReadOnlyText(string propertyName)
    {
        var textBox = new TextBox();
        textBox[!TextBox.TextProperty] = new Binding(propertyName) { Mode = BindingMode.OneWay };
        textBox.IsReadOnly = true;
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
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
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

    private static async void BrowseSubmissionReportDir_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ConfigWindowViewModel viewModel } control ||
            TopLevel.GetTopLevel(control) is not Window window)
        {
            return;
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择提审记录目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            viewModel.SetWeixinSubmissionReportDir(folder.Path.LocalPath);
        }
    }

    private static void ClearSubmissionReportDir_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConfigWindowViewModel viewModel })
        {
            viewModel.SetWeixinSubmissionReportDir(string.Empty);
        }
    }

    private static async void BrowseTemplateRoot_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ConfigWindowViewModel viewModel } control ||
            TopLevel.GetTopLevel(control) is not Window window)
        {
            return;
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择工程图模板根目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            viewModel.SetProjectImageTemplateRoot(folder.Path.LocalPath);
        }
    }
}
