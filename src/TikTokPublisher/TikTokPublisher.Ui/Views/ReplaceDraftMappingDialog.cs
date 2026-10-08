using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TikTokPublisher.Ui.Views;

public sealed record ReplaceDraftMapping(string ProjectDir, string DramaName, string DraftTarget);

public sealed class ReplaceDraftMappingDialog : Window
{
    private static readonly Regex DraftIdPattern = new(@"\b(\d{16,20})\b", RegexOptions.Compiled);

    private readonly IReadOnlyList<MappingRow> _rows;
    private readonly TextBlock _error;

    private ReplaceDraftMappingDialog(IReadOnlyList<ReplaceDraftMapping> dramas)
    {
        Title = "新剧替换草稿";
        Width = 760;
        MinWidth = 640;
        MaxHeight = 680;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 12,
        };
        root.Children.Add(new TextBlock
        {
            Text = "为每个勾选剧集填写要替换的草稿名称或草稿 ID。名称按原创管理精确匹配；ID 或剧集链接直接打开该草稿。" +
                   "确定后会清空对应草稿的剧名、简介、封面和已上传视频，再从第 1 集重新上传。合同保持不变。此操作不能撤销。",
            TextWrapping = TextWrapping.Wrap,
        });

        var list = new StackPanel { Spacing = 8 };
        _rows = dramas.Select(drama =>
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("240,*"),
                ColumnSpacing = 12,
            };
            row.Children.Add(new TextBlock
            {
                Text = drama.DramaName,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            var input = new TextBox
            {
                Watermark = "草稿名称或 ID",
                Text = drama.DraftTarget,
                MinHeight = 32,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(input, 1);
            row.Children.Add(input);
            list.Children.Add(row);
            return new MappingRow(drama.ProjectDir, drama.DramaName, input);
        }).ToArray();

        var scroller = new ScrollViewer
        {
            Content = list,
            MaxHeight = 420,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);

        _error = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        Grid.SetRow(_error, 2);
        root.Children.Add(_error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var ok = new Button { Content = "确定替换", MinWidth = 108 };
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        ok.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        Content = root;
    }

    public static Task<IReadOnlyList<ReplaceDraftMapping>?> ShowAsync(
        Window owner,
        IReadOnlyList<ReplaceDraftMapping> dramas) =>
        new ReplaceDraftMappingDialog(dramas).ShowDialog<IReadOnlyList<ReplaceDraftMapping>?>(owner);

    private void Confirm()
    {
        var mappings = _rows
            .Select(row => new ReplaceDraftMapping(
                row.ProjectDir,
                row.DramaName,
                row.Input.Text?.Trim() ?? ""))
            .ToArray();
        var missing = mappings.FirstOrDefault(mapping => mapping.DraftTarget.Length == 0);
        if (missing is not null)
        {
            ShowError($"请为「{missing.DramaName}」填写草稿名称或 ID。");
            return;
        }

        var duplicate = mappings
            .GroupBy(mapping => DuplicateKey(mapping.DraftTarget), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            var names = string.Join("、", duplicate.Select(mapping => mapping.DramaName));
            ShowError($"「{names}」填写了同一个草稿，请分别指定不同草稿。");
            return;
        }

        Close(mappings);
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    private static string DuplicateKey(string target)
    {
        var trimmed = target.Trim();
        var match = DraftIdPattern.Match(trimmed);
        if (!match.Success)
            return "name:" + trimmed;

        var id = match.Groups[1].Value;
        var remainder = DraftIdPattern.Replace(trimmed, "").Trim();
        return remainder.Length == 0 || trimmed.Contains("/series/", StringComparison.OrdinalIgnoreCase)
            ? "id:" + id
            : "name:" + trimmed;
    }

    private sealed record MappingRow(string ProjectDir, string DramaName, TextBox Input);
}
