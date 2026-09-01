using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed record ManualDeletedCopyrightProofDialogResult(
    ManualDeletedCopyrightProofInputMode Mode,
    IReadOnlyList<ManualDeletedCopyrightProofEntry> Entries,
    CopyrightProofExecutionMode ExecutionMode);

public sealed class ManualDeletedCopyrightProofDialog : Window
{
    private readonly TextBlock _instructions = new();
    private readonly StackPanel _entryRows = new() { Spacing = 8 };
    private readonly TextBox _unknownTitles = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 180,
        MaxHeight = 330,
        Watermark = "一行输入一个新剧名，可一次粘贴多个",
    };
    private readonly TextBlock _summary = new();
    private readonly CopyrightProofExecutionModeSelector _executionModeSelector = new();
    private readonly List<EntryEditor> _editors = [];
    private readonly Grid _knownModePanel;
    private ManualDeletedCopyrightProofInputMode _mode =
        ManualDeletedCopyrightProofInputMode.UnknownOriginalTitle;

    private ManualDeletedCopyrightProofDialog()
    {
        Title = "手动补已删除证明";
        Width = 900;
        MinWidth = 720;
        MinHeight = 430;
        MaxHeight = 720;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            RowSpacing = 12,
        };

        _instructions.TextWrapping = TextWrapping.Wrap;
        _instructions.FontWeight = FontWeight.SemiBold;
        root.Children.Add(_instructions);

        Grid.SetRow(_executionModeSelector, 1);
        root.Children.Add(_executionModeSelector);

        _knownModePanel = BuildKnownModePanel();
        var modeTabs = new TabControl
        {
            ItemsSource = new object[]
            {
                new TabItem
                {
                    Header = "仅有新剧名",
                    Content = _unknownTitles,
                },
                new TabItem
                {
                    Header = "已知原剧名",
                    Content = _knownModePanel,
                },
            },
            SelectedIndex = 0,
        };
        modeTabs.SelectionChanged += (_, _) =>
        {
            var mode = modeTabs.SelectedIndex == 1
                ? ManualDeletedCopyrightProofInputMode.KnownOriginalTitle
                : ManualDeletedCopyrightProofInputMode.UnknownOriginalTitle;
            SetInputMode(mode, focus: true);
        };
        Grid.SetRow(modeTabs, 2);
        root.Children.Add(modeTabs);

        _unknownTitles.TextChanged += (_, _) => UpdateSummary();
        _summary.Foreground = Brushes.DimGray;
        Grid.SetRow(_summary, 3);
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
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        Content = root;
        AddEntryRow(focus: false);
        SetInputMode(ManualDeletedCopyrightProofInputMode.UnknownOriginalTitle, focus: false);
        Opened += (_, _) => FocusCurrentInput();
    }

    public static Task<ManualDeletedCopyrightProofDialogResult?> ShowAsync(Window owner)
    {
        var dialog = new ManualDeletedCopyrightProofDialog();
        return dialog.ShowDialog<ManualDeletedCopyrightProofDialogResult?>(owner);
    }

    private Grid BuildKnownModePanel()
    {
        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            RowSpacing = 8,
        };
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
            Text = "可添加多组剧名；每组的新剧名和原剧名都必须填写。",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.DimGray,
        });
        panel.Children.Add(toolbar);

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
            Text = "原剧名（用于查找原始短剧）",
            FontWeight = FontWeight.SemiBold,
        };
        Grid.SetColumn(originalHeader, 2);
        header.Children.Add(originalHeader);
        Grid.SetRow(header, 1);
        panel.Children.Add(header);

        var scroller = new ScrollViewer
        {
            Content = _entryRows,
            MinHeight = 120,
            MaxHeight = 330,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroller, 2);
        panel.Children.Add(scroller);
        return panel;
    }

    private void SetInputMode(ManualDeletedCopyrightProofInputMode mode, bool focus)
    {
        _mode = mode;
        var known = mode == ManualDeletedCopyrightProofInputMode.KnownOriginalTitle;
        _knownModePanel.IsVisible = known;
        _unknownTitles.IsVisible = !known;
        _instructions.Text = known
            ? "输入已删除剧集的新剧名和原剧名。系统优先复用当前队列或归档项目，否则按原剧名重新查找片源；不会重新上传剧集。"
            : "输入已删除剧集的新剧名，一行一个。系统会从当前账号的 TikTok 原创管理项目精确匹配并下载生成证明材料所需的视频，不限制剧集状态；不会重新上传剧集。";
        UpdateSummary();
        if (focus && IsVisible)
            FocusCurrentInput();
    }

    private void FocusCurrentInput()
    {
        if (_mode == ManualDeletedCopyrightProofInputMode.UnknownOriginalTitle)
            _unknownTitles.Focus();
        else if (_editors.Count > 0)
            _editors[0].NewTitle.Focus();
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
            Watermark = "输入原剧名",
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
        var entries = _mode == ManualDeletedCopyrightProofInputMode.KnownOriginalTitle
            ? ReadKnownOriginalEntries()
            : ReadUnknownOriginalEntries();
        if (entries is null)
            return;

        Close(new ManualDeletedCopyrightProofDialogResult(
            _mode,
            entries,
            _executionModeSelector.ExecutionMode));
    }

    private IReadOnlyList<ManualDeletedCopyrightProofEntry>? ReadKnownOriginalEntries()
    {
        var rows = _editors
            .Select((editor, index) => new
            {
                Index = index + 1,
                NewTitle = (editor.NewTitle.Text ?? string.Empty).Trim(),
                OriginalTitle = (editor.OriginalTitle.Text ?? string.Empty).Trim(),
            })
            .Where(row =>
                !string.IsNullOrWhiteSpace(row.NewTitle) ||
                !string.IsNullOrWhiteSpace(row.OriginalTitle))
            .ToArray();
        var incompleteRows = rows
            .Where(row =>
                string.IsNullOrWhiteSpace(row.NewTitle) ||
                string.IsNullOrWhiteSpace(row.OriginalTitle))
            .Select(row => row.Index)
            .ToArray();
        if (incompleteRows.Length > 0)
        {
            ShowError($"第 {string.Join("、", incompleteRows)} 行的新剧名和原剧名没有填写完整。");
            return null;
        }

        var entries = rows
            .Select(row => new ManualDeletedCopyrightProofEntry(row.NewTitle, row.OriginalTitle))
            .Distinct()
            .ToArray();
        if (entries.Length == 0)
        {
            ShowError("请至少填写一组新剧名和原剧名。");
            return null;
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
            return null;
        }

        return entries;
    }

    private IReadOnlyList<ManualDeletedCopyrightProofEntry>? ReadUnknownOriginalEntries()
    {
        var entries = ManualDeletedCopyrightProofService
            .ParseUnknownOriginalTitles(_unknownTitles.Text);
        if (entries.Count == 0)
        {
            ShowError("请至少填写一个新剧名。");
            return null;
        }

        return entries;
    }

    private void UpdateSummary()
    {
        if (_mode == ManualDeletedCopyrightProofInputMode.UnknownOriginalTitle)
        {
            var entries = ManualDeletedCopyrightProofService
                .ParseUnknownOriginalTitles(_unknownTitles.Text);
            _summary.Text = entries.Count > 0
                ? $"已输入 {entries.Count} 个新剧名；将逐个从当前账号的 TikTok 原创管理项目恢复视频。"
                : "请至少填写一个新剧名。";
            _summary.Foreground = entries.Count > 0 ? Brushes.SeaGreen : Brushes.DimGray;
            return;
        }

        var complete = _editors.Count(editor =>
            !string.IsNullOrWhiteSpace(editor.NewTitle.Text) &&
            !string.IsNullOrWhiteSpace(editor.OriginalTitle.Text));
        var incomplete = _editors.Count(editor =>
            !string.IsNullOrWhiteSpace(editor.NewTitle.Text) ^
            !string.IsNullOrWhiteSpace(editor.OriginalTitle.Text));
        _summary.Text = complete > 0
            ? $"已填写 {complete} 组完整剧名" +
              (incomplete > 0 ? $"，另有 {incomplete} 行未填写完整。" : "。")
            : "请至少填写一组新剧名和原剧名。";
        _summary.Foreground = incomplete > 0
            ? Brushes.IndianRed
            : complete > 0
                ? Brushes.SeaGreen
                : Brushes.DimGray;
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
