using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Ui.Views;

public sealed class TikTokPublishedSeriesMatchDialog : Window
{
    private readonly Func<
        IReadOnlyList<string>,
        IProgress<TikTokPublishedSeriesLookupProgress>,
        CancellationToken,
        Task<IReadOnlyList<TikTokPublishedSeriesMatch>>> _lookup;
    private readonly TextBox _input;
    private readonly TextBox _output;
    private readonly TextBlock _summary;
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly Button _copyPublishedButton;
    private readonly Button _copyAllButton;
    private readonly Dictionary<string, TikTokPublishedSeriesMatch> _matches = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _activeTitles = [];
    private CancellationTokenSource? _lookupCts;

    private TikTokPublishedSeriesMatchDialog(
        string accountName,
        Func<
            IReadOnlyList<string>,
            IProgress<TikTokPublishedSeriesLookupProgress>,
            CancellationToken,
            Task<IReadOnlyList<TikTokPublishedSeriesMatch>>> lookup)
    {
        _lookup = lookup;
        Title = "匹配已发布剧集";
        Width = 860;
        Height = 680;
        MinWidth = 700;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,145,Auto,Auto,*,Auto"),
            RowSpacing = 10,
        };

        root.Children.Add(new TextBlock
        {
            Text = $"当前账号：{accountName}。输入新剧名，一行一个；只按新剧名完全一致匹配，点击开始后自动查询全部名称。",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Watermark = "每行输入一个新剧名",
        };
        Grid.SetRow(_input, 1);
        root.Children.Add(_input);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        _startButton = BuildButton("开始匹配", StartLookupAsync, primary: true, minWidth: 104);
        _stopButton = BuildButton("停止匹配", StopLookup, minWidth: 104);
        _stopButton.IsEnabled = false;
        actions.Children.Add(_startButton);
        actions.Children.Add(_stopButton);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);

        _summary = new TextBlock
        {
            Text = "等待开始匹配",
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(_summary, 3);
        root.Children.Add(_summary);

        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Watermark = "匹配结果将在这里显示，可使用鼠标选择并按 Ctrl+C 复制。",
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(
            _output,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(
            _output,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        Grid.SetRow(_output, 4);
        root.Children.Add(_output);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            ColumnSpacing = 8,
        };
        _copyPublishedButton = BuildButton(
            "复制已发布剧名",
            CopyPublishedAsync,
            minWidth: 132);
        _copyAllButton = BuildButton(
            "复制全部结果",
            CopyAllAsync,
            minWidth: 120);
        _copyPublishedButton.IsEnabled = false;
        _copyAllButton.IsEnabled = false;
        footer.Children.Add(_copyPublishedButton);
        Grid.SetColumn(_copyAllButton, 1);
        footer.Children.Add(_copyAllButton);

        var closeButton = BuildButton("关闭", CloseDialog, minWidth: 88);
        Grid.SetColumn(closeButton, 3);
        footer.Children.Add(closeButton);
        Grid.SetRow(footer, 5);
        root.Children.Add(footer);

        Content = root;
        Opened += (_, _) => _input.Focus();
        Closed += (_, _) =>
        {
            _lookupCts?.Cancel();
            _lookupCts?.Dispose();
            _lookupCts = null;
        };
    }

    public static Task ShowAsync(
        Window owner,
        string accountName,
        Func<
            IReadOnlyList<string>,
            IProgress<TikTokPublishedSeriesLookupProgress>,
            CancellationToken,
            Task<IReadOnlyList<TikTokPublishedSeriesMatch>>> lookup)
    {
        var dialog = new TikTokPublishedSeriesMatchDialog(accountName, lookup);
        return dialog.ShowDialog(owner);
    }

    private async void StartLookupAsync()
    {
        if (_lookupCts is not null)
            return;

        var titles = TikTokPublishedSeriesMatchText.ParseNewTitles(_input.Text);
        if (titles.Count == 0)
        {
            _summary.Text = "请输入至少一个新剧名。";
            _summary.Foreground = Brushes.IndianRed;
            return;
        }

        _activeTitles = titles;
        _matches.Clear();
        RenderResults();
        SetRunning(true);
        _summary.Text = $"正在准备当前账号浏览器，共 {titles.Count} 个新剧名…";
        _summary.Foreground = Brushes.Black;
        _lookupCts = new CancellationTokenSource();

        var progress = new Progress<TikTokPublishedSeriesLookupProgress>(update =>
        {
            if (update.Match is not null)
                _matches[update.Match.InputTitle] = update.Match;
            RenderResults();
            _summary.Text =
                $"正在匹配 {update.Completed}/{update.Total}：{update.CurrentTitle}{BuildCountsSuffix()}";
        });

        try
        {
            var results = await _lookup(titles, progress, _lookupCts.Token);
            _matches.Clear();
            foreach (var result in results)
                _matches[result.InputTitle] = result;
            RenderResults();
            _summary.Text = $"匹配完成，共 {results.Count} 个新剧名{BuildCountsSuffix()}";
            _summary.Foreground = Brushes.Black;
        }
        catch (OperationCanceledException)
        {
            RenderResults();
            _summary.Text = $"匹配已停止，已完成 {_matches.Count}/{titles.Count}{BuildCountsSuffix()}";
            _summary.Foreground = Brushes.DarkOrange;
        }
        catch (Exception ex)
        {
            RenderResults();
            _summary.Text = $"匹配失败：{ex.Message}";
            _summary.Foreground = Brushes.IndianRed;
        }
        finally
        {
            _lookupCts?.Dispose();
            _lookupCts = null;
            SetRunning(false);
        }
    }

    private void StopLookup() => _lookupCts?.Cancel();

    private async void CopyPublishedAsync()
    {
        var text = TikTokPublishedSeriesMatchText.BuildPublishedTitlesCopyText(OrderedMatches());
        if (string.IsNullOrWhiteSpace(text))
        {
            _summary.Text = "当前没有可复制的已发布剧名。";
            _summary.Foreground = Brushes.DarkOrange;
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        await clipboard.SetTextAsync(text);
        _summary.Text = $"已复制 {_matches.Values.Count(match => match.IsPublished)} 个已发布剧名。";
        _summary.Foreground = Brushes.SeaGreen;
    }

    private async void CopyAllAsync()
    {
        var text = _output.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        await clipboard.SetTextAsync(text);
        _summary.Text = "已复制结果框内容。";
        _summary.Foreground = Brushes.SeaGreen;
    }

    private void RenderResults()
    {
        var matches = OrderedMatches();
        _output.Text = TikTokPublishedSeriesMatchText.BuildDisplayText(matches);
        _copyPublishedButton.IsEnabled = matches.Any(match => match.IsPublished);
        _copyAllButton.IsEnabled = matches.Count > 0;
    }

    private IReadOnlyList<TikTokPublishedSeriesMatch> OrderedMatches() =>
        _activeTitles
            .Where(title => _matches.ContainsKey(title))
            .Select(title => _matches[title])
            .ToArray();

    private string BuildCountsSuffix()
    {
        var matches = _matches.Values.ToArray();
        if (matches.Length == 0)
            return string.Empty;
        return $"；已发布 {matches.Count(match => match.Kind == TikTokPublishedSeriesMatchKind.Published)}，" +
               $"未发布 {matches.Count(match => match.Kind == TikTokPublishedSeriesMatchKind.NotPublished)}，" +
               $"未找到 {matches.Count(match => match.Kind == TikTokPublishedSeriesMatchKind.Missing)}，" +
               $"冲突 {matches.Count(match => match.Kind == TikTokPublishedSeriesMatchKind.Conflict)}，" +
               $"失败 {matches.Count(match => match.Kind == TikTokPublishedSeriesMatchKind.Failed)}";
    }

    private void SetRunning(bool running)
    {
        _input.IsEnabled = !running;
        _startButton.IsEnabled = !running;
        _stopButton.IsEnabled = running;
    }

    private void CloseDialog()
    {
        _lookupCts?.Cancel();
        Close();
    }

    private static Button BuildButton(
        string text,
        Action click,
        bool primary = false,
        double minWidth = 88)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = minWidth,
        };
        if (primary)
            button.Classes.Add("primaryAction");
        button.Click += (_, _) => click();
        return button;
    }
}
