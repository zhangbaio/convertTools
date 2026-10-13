using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public sealed record CopyrightProofBatchDialogResult(
    IReadOnlyList<CopyrightProofProjectMatch> SelectedMatches,
    IReadOnlyList<CopyrightProofProjectMatch> SkippedMatches,
    CopyrightProofExecutionMode ExecutionMode);

public sealed class CopyrightProofBatchDialog : Window
{
    private readonly Func<string, IReadOnlyList<CopyrightProofProjectMatch>> _match;
    private readonly TextBox _input;
    private readonly StackPanel _previewRows;
    private readonly TextBlock _summary;
    private readonly CopyrightProofExecutionModeSelector _executionModeSelector = new();
    private readonly Dictionary<string, string> _selectedOptionKeys = new(StringComparer.Ordinal);
    private IReadOnlyList<CopyrightProofProjectMatch> _matches = [];

    private CopyrightProofBatchDialog(
        Func<string, IReadOnlyList<CopyrightProofProjectMatch>> match,
        string? initialInput)
    {
        _match = match;
        Title = "补全版权证明";
        Width = 820;
        MinWidth = 680;
        MinHeight = 360;
        MaxHeight = 650;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,150,Auto,Auto,Auto"),
            RowSpacing = 10,
        };
        root.Children.Add(new TextBlock
        {
            Text = "输入新剧名，一行一个。只按新剧名精确匹配；依次查询当前上传队列、已归档项目、历史数据库、Excel 和本地备份。" +
                   "找不到本地或历史唯一记录时，将按「补已删除证明」自动新建本地目录，并从 TikTok 原创管理恢复视频。" +
                   "同一目录会自动合并。目录或原剧名冲突时列出候选，选中一条后才会执行，未选择的项目仍会跳过。",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Watermark = "每行输入一个新剧名",
            Text = initialInput ?? string.Empty,
        };
        Grid.SetRow(_executionModeSelector, 1);
        root.Children.Add(_executionModeSelector);

        Grid.SetRow(_input, 2);
        root.Children.Add(_input);

        var previewHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        previewHeader.Children.Add(_summary);
        var refreshButton = new Button
        {
            Content = "重新匹配",
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        refreshButton.Click += (_, _) => RefreshPreview();
        Grid.SetColumn(refreshButton, 1);
        previewHeader.Children.Add(refreshButton);
        Grid.SetRow(previewHeader, 3);
        root.Children.Add(previewHeader);

        _previewRows = new StackPanel { Spacing = 6 };
        var scroller = new ScrollViewer
        {
            Content = _previewRows,
            MaxHeight = 240,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroller, 4);
        root.Children.Add(scroller);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var execute = new Button { Content = "开始补全", MinWidth = 108 };
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        execute.Click += (_, _) =>
        {
            RefreshPreview();
            if (_matches.Count == 0)
            {
                _summary.Text = "请至少输入一个新剧名。";
                _summary.Foreground = Brushes.IndianRed;
                return;
            }

            var selected = new List<CopyrightProofProjectMatch>();
            var skipped = new List<CopyrightProofProjectMatch>();
            foreach (var match in _matches)
            {
                if (match.CanExecute)
                {
                    selected.Add(match);
                    continue;
                }

                if (SelectedResolution(match) is { } chosen)
                {
                    selected.Add(chosen);
                    continue;
                }

                skipped.Add(match);
            }

            Close(new CopyrightProofBatchDialogResult(
                selected,
                skipped,
                _executionModeSelector.ExecutionMode));
        };
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(execute);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 5);
        root.Children.Add(buttons);
        Content = root;
        Opened += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_input.Text))
                RefreshPreview();
            _input.Focus();
        };
    }

    public static Task<CopyrightProofBatchDialogResult?> ShowAsync(
        Window owner,
        Func<string, IReadOnlyList<CopyrightProofProjectMatch>> match,
        string? initialInput = null)
    {
        var dialog = new CopyrightProofBatchDialog(match, initialInput);
        return dialog.ShowDialog<CopyrightProofBatchDialogResult?>(owner);
    }

    private void RefreshPreview()
    {
        _matches = _match(_input.Text ?? string.Empty);
        var activeTitles = _matches.Select(match => match.NewTitle).ToHashSet(StringComparer.Ordinal);
        foreach (var title in _selectedOptionKeys.Keys.Where(title => !activeTitles.Contains(title)).ToArray())
            _selectedOptionKeys.Remove(title);

        _previewRows.Children.Clear();
        for (var index = 0; index < _matches.Count; index++)
        {
            var match = _matches[index];
            var (location, color) = match.Location switch
            {
                CopyrightProofProjectLocation.CurrentQueue => ("当前上传队列", Brushes.SeaGreen),
                CopyrightProofProjectLocation.Archived => ("已归档（将自动回退）", Brushes.DarkOrange),
                CopyrightProofProjectLocation.DeletedHistory when
                    ManualDeletedCopyrightProofService.IsPublishedRecoveryFallback(match) =>
                    ("本地无记录（将新建并从 TikTok 恢复）", Brushes.DodgerBlue),
                CopyrightProofProjectLocation.DeletedHistory => ("已删除（将从历史自动重建）", Brushes.DodgerBlue),
                CopyrightProofProjectLocation.Conflict => (CopyrightProofProjectMatcher.ConflictHeadline(match), Brushes.IndianRed),
                _ => ("未找到", Brushes.Gray),
            };
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock
            {
                Text = $"• {match.NewTitle}    [{location}]",
                Foreground = color,
                TextWrapping = TextWrapping.Wrap,
            });
            if (match.Location == CopyrightProofProjectLocation.Conflict &&
                match.ConflictOptions is { Count: > 0 } options)
            {
                _selectedOptionKeys.TryGetValue(match.NewTitle, out var selectedKey);
                if (selectedKey is not null &&
                    options.All(option => !string.Equals(option.Key, selectedKey, StringComparison.Ordinal)))
                {
                    _selectedOptionKeys.Remove(match.NewTitle);
                    selectedKey = null;
                }

                var groupName = $"copyright-proof-conflict-{index}";
                foreach (var option in options)
                {
                    var radio = new RadioButton
                    {
                        GroupName = groupName,
                        Content = new TextBlock
                        {
                            Text = option.Label,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        IsChecked = string.Equals(selectedKey, option.Key, StringComparison.Ordinal),
                        Margin = new Thickness(18, 0, 0, 0),
                    };
                    var optionKey = option.Key;
                    var newTitle = match.NewTitle;
                    radio.PropertyChanged += (_, args) =>
                    {
                        if (args.Property != RadioButton.IsCheckedProperty || radio.IsChecked != true)
                            return;
                        _selectedOptionKeys[newTitle] = optionKey;
                        UpdateSummary();
                    };
                    row.Children.Add(radio);
                }
            }

            _previewRows.Children.Add(row);
        }

        UpdateSummary();
    }

    private CopyrightProofProjectMatch? SelectedResolution(CopyrightProofProjectMatch match)
    {
        if (match.Location != CopyrightProofProjectLocation.Conflict ||
            match.ConflictOptions is not { Count: > 0 } options ||
            !_selectedOptionKeys.TryGetValue(match.NewTitle, out var key))
        {
            return null;
        }

        return options.FirstOrDefault(option => string.Equals(option.Key, key, StringComparison.Ordinal))?.Resolution;
    }

    private void UpdateSummary()
    {
        var executable = _matches.Count(match => match.CanExecute || SelectedResolution(match) is not null);
        var archived = _matches.Count(match => match.Location == CopyrightProofProjectLocation.Archived);
        var deleted = _matches.Count(match =>
            match.Location == CopyrightProofProjectLocation.DeletedHistory ||
            SelectedResolution(match)?.Location == CopyrightProofProjectLocation.DeletedHistory);
        var unresolved = _matches.Count - executable;
        _summary.Text =
            $"输入 {_matches.Count} 个；可执行 {executable} 个；需回退归档 {archived} 个；" +
            $"需重建已删除项目 {deleted} 个；未匹配或冲突 {unresolved} 个";
        _summary.Foreground = Brushes.Black;
    }
}
