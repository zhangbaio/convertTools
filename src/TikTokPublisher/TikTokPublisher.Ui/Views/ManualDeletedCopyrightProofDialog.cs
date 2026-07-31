using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed record ManualDeletedCopyrightProofDialogResult(
    IReadOnlyList<ManualDeletedCopyrightProofEntry> Entries);

public sealed class ManualDeletedCopyrightProofDialog : Window
{
    private readonly StackPanel _entryRows = new() { Spacing = 8 };
    private readonly TextBlock _summary = new();
    private readonly List<EntryEditor> _editors = [];

    private ManualDeletedCopyrightProofDialog()
    {
        Title = "手动补已删除证明";
        Width = 900;
        MinWidth = 720;
        MinHeight = 390;
        MaxHeight = 680;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
            RowSpacing = 10,
        };
        root.Children.Add(new TextBlock
        {
            Text = "输入已删除剧集的新剧名，原剧名可以不填。已填写原剧名时优先从原片源恢复；未填写时将从当前账号的 TikTok 已发布项目下载必要视频；不会重新上传剧集。",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        var addButton = new Button
        {
            Content = "添加一行",
            MinWidth = 92,
        };
        addButton.Click += (_, _) => AddEntryRow(focus: true);
        toolbar.Children.Add(addButton);
        toolbar.Children.Add(new TextBlock
        {
            Text = "可添加多组剧名；同一个新剧名不能对应多个不同的原剧名。",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.DimGray,
        });
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,16,*,90"),
        };
        header.Children.Add(new TextBlock
        {
            Text = "新剧名（TikTok 已发布名称）",
            FontWeight = FontWeight.SemiBold,
        });
        var originalHeader = new TextBlock
        {
            Text = "原剧名（选填；留空则从 TikTok 恢复）",
            FontWeight = FontWeight.SemiBold,
        };
        Grid.SetColumn(originalHeader, 2);
        header.Children.Add(originalHeader);
        Grid.SetRow(header, 2);
        root.Children.Add(header);

        var scroller = new ScrollViewer
        {
            Content = _entryRows,
            MinHeight = 120,
            MaxHeight = 330,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroller, 3);
        root.Children.Add(scroller);

        _summary.Text = "请至少填写一个新剧名。";
        _summary.Foreground = Brushes.DimGray;
        Grid.SetRow(_summary, 4);
        root.Children.Add(_summary);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var execute = new Button
        {
            Content = "开始补全",
            MinWidth = 108,
        };
        execute.Click += (_, _) => Submit();
        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 88,
        };
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(execute);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 5);
        root.Children.Add(buttons);

        Content = root;
        AddEntryRow(focus: false);
        Opened += (_, _) => _editors[0].NewTitle.Focus();
    }

    public static Task<ManualDeletedCopyrightProofDialogResult?> ShowAsync(Window owner)
    {
        var dialog = new ManualDeletedCopyrightProofDialog();
        return dialog.ShowDialog<ManualDeletedCopyrightProofDialogResult?>(owner);
    }

    private void AddEntryRow(bool focus)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,16,*,90"),
        };
        var newTitle = new TextBox
        {
            Watermark = "输入新剧名",
        };
        var originalTitle = new TextBox
        {
            Watermark = "选填原剧名",
        };
        Grid.SetColumn(originalTitle, 2);
        var remove = new Button
        {
            Content = "移除",
            MinWidth = 72,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(remove, 3);

        var editor = new EntryEditor(grid, newTitle, originalTitle);
        remove.Click += (_, _) =>
        {
            if (_editors.Count == 1)
            {
                editor.NewTitle.Text = string.Empty;
                editor.OriginalTitle.Text = string.Empty;
                editor.NewTitle.Focus();
                return;
            }

            _editors.Remove(editor);
            _entryRows.Children.Remove(grid);
            UpdateSummary();
        };
        newTitle.TextChanged += (_, _) => UpdateSummary();
        originalTitle.TextChanged += (_, _) => UpdateSummary();
        grid.Children.Add(newTitle);
        grid.Children.Add(originalTitle);
        grid.Children.Add(remove);
        _editors.Add(editor);
        _entryRows.Children.Add(grid);
        UpdateSummary();
        if (focus)
            newTitle.Focus();
    }

    private void Submit()
    {
        var incompleteRows = _editors
            .Select((editor, index) => new
            {
                Index = index + 1,
                NewTitle = (editor.NewTitle.Text ?? string.Empty).Trim(),
                OriginalTitle = (editor.OriginalTitle.Text ?? string.Empty).Trim(),
            })
            .Where(row =>
                (!string.IsNullOrWhiteSpace(row.NewTitle) ||
                 !string.IsNullOrWhiteSpace(row.OriginalTitle)) &&
                string.IsNullOrWhiteSpace(row.NewTitle))
            .Select(row => row.Index)
            .ToArray();
        if (incompleteRows.Length > 0)
        {
            ShowError($"第 {string.Join("、", incompleteRows)} 行填写了原剧名，但没有填写新剧名。");
            return;
        }

        var entries = _editors
            .Select(editor => new ManualDeletedCopyrightProofEntry(
                (editor.NewTitle.Text ?? string.Empty).Trim(),
                (editor.OriginalTitle.Text ?? string.Empty).Trim()))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.NewTitle))
            .Distinct()
            .ToArray();
        if (entries.Length == 0)
        {
            ShowError("请至少填写一个新剧名。");
            return;
        }

        var conflicts = entries
            .GroupBy(entry => entry.NewTitle, StringComparer.Ordinal)
            .Where(group => group
                .Select(entry => entry.OriginalTitle)
                .Distinct(StringComparer.Ordinal)
                .Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (conflicts.Length > 0)
        {
            ShowError($"同一个新剧名填写了多个原剧名：{string.Join("、", conflicts)}");
            return;
        }

        Close(new ManualDeletedCopyrightProofDialogResult(entries));
    }

    private void UpdateSummary()
    {
        var complete = _editors.Count(editor =>
            !string.IsNullOrWhiteSpace(editor.NewTitle.Text));
        var platformRecovery = _editors.Count(editor =>
            !string.IsNullOrWhiteSpace(editor.NewTitle.Text) &&
            string.IsNullOrWhiteSpace(editor.OriginalTitle.Text));
        _summary.Text = complete > 0
            ? $"已填写 {complete} 组，其中 {platformRecovery} 组将从 TikTok 已发布项目恢复视频；点击“开始补全”后将先进行二次确认。"
            : "请至少填写一个新剧名。";
        _summary.Foreground = complete > 0 ? Brushes.SeaGreen : Brushes.DimGray;
    }

    private void ShowError(string message)
    {
        _summary.Text = message;
        _summary.Foreground = Brushes.IndianRed;
    }

    private sealed record EntryEditor(
        Grid Container,
        TextBox NewTitle,
        TextBox OriginalTitle);
}
