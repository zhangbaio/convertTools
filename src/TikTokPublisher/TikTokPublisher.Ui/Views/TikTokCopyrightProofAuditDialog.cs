using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Ui.Views;

public sealed class TikTokCopyrightProofAuditDialog : Window
{
    private readonly string _accountName;
    private readonly Func<
        TikTokCopyrightProofAuditSelection,
        IProgress<TikTokCopyrightProofAuditProgress>,
        CancellationToken,
        Task<IReadOnlyList<TikTokCopyrightProofAuditItem>>> _audit;
    private readonly CheckBox _publishedBox;
    private readonly CheckBox _videoReviewingBox;
    private readonly NumericUpDown _concurrencyBox;
    private readonly Button _startButton;
    private readonly TextBlock _summary;
    private readonly ProgressBar _progress;
    private readonly TextBox _output;
    private readonly Button _stopButton;
    private readonly Button _copyMissingButton;
    private readonly Button _copyFailedButton;
    private readonly Button _exportButton;
    private readonly Button _completeButton;
    private readonly Dictionary<int, TikTokCopyrightProofAuditItem> _results = [];
    private CancellationTokenSource? _auditCts;

    private TikTokCopyrightProofAuditDialog(
        string accountName,
        int defaultConcurrency,
        Func<
            TikTokCopyrightProofAuditSelection,
            IProgress<TikTokCopyrightProofAuditProgress>,
            CancellationToken,
            Task<IReadOnlyList<TikTokCopyrightProofAuditItem>>> audit)
    {
        _accountName = accountName;
        _audit = audit;
        Title = "检查未补版权证明";
        Width = 900;
        Height = 680;
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
            RowSpacing = 10,
        };
        root.Children.Add(new TextBlock
        {
            Text =
                $"当前账号：{accountName}。系统将按所选状态只读检查剧集版权证明页面，" +
                "不会修改或提交任何平台内容。",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });

        _publishedBox = new CheckBox
        {
            Content = "已发布",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _videoReviewingBox = new CheckBox
        {
            Content = "视频检测中",
            IsChecked = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _concurrencyBox = new NumericUpDown
        {
            Minimum = 2,
            Maximum = 8,
            Value = Math.Clamp(defaultConcurrency, 2, 8),
            Width = 72,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _startButton = BuildButton("开始检测", StartAudit, primary: true, minWidth: 104);
        _publishedBox.IsCheckedChanged += OnSelectionChanged;
        _videoReviewingBox.IsCheckedChanged += OnSelectionChanged;
        var selectionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
        };
        selectionPanel.Children.Add(new TextBlock
        {
            Text = "检测剧集状态：",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        selectionPanel.Children.Add(_publishedBox);
        selectionPanel.Children.Add(_videoReviewingBox);
        selectionPanel.Children.Add(new TextBlock
        {
            Text = "检测并发：",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        selectionPanel.Children.Add(_concurrencyBox);
        selectionPanel.Children.Add(_startButton);
        Grid.SetRow(selectionPanel, 1);
        root.Children.Add(selectionPanel);

        _summary = new TextBlock
        {
            Text = "请选择需要检查的剧集状态，然后点击“开始检测”。",
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(_summary, 2);
        root.Children.Add(_summary);

        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Height = 8,
        };
        Grid.SetRow(_progress, 3);
        root.Children.Add(_progress);

        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Text = "等待开始检测…",
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
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        _stopButton = BuildButton("停止检查", StopAudit, minWidth: 96);
        _copyMissingButton = BuildButton("复制未补剧名", CopyMissingAsync, minWidth: 120);
        _copyFailedButton = BuildButton("复制失败剧名", CopyFailedAsync, minWidth: 120);
        _exportButton = BuildButton("导出 Excel", ExportResults, minWidth: 104);
        _completeButton = BuildButton(
            "补全这些剧集",
            CompleteMissing,
            primary: true,
            minWidth: 120);
        var closeButton = BuildButton("关闭", CloseDialog, minWidth: 88);

        _copyMissingButton.IsEnabled = false;
        _copyFailedButton.IsEnabled = false;
        _exportButton.IsEnabled = false;
        _completeButton.IsEnabled = false;
        _stopButton.IsEnabled = false;

        footer.Children.Add(_stopButton);
        Grid.SetColumn(_copyMissingButton, 1);
        footer.Children.Add(_copyMissingButton);
        Grid.SetColumn(_copyFailedButton, 2);
        footer.Children.Add(_copyFailedButton);
        Grid.SetColumn(_exportButton, 3);
        footer.Children.Add(_exportButton);
        Grid.SetColumn(_completeButton, 5);
        footer.Children.Add(_completeButton);
        Grid.SetColumn(closeButton, 6);
        footer.Children.Add(closeButton);
        Grid.SetRow(footer, 5);
        root.Children.Add(footer);

        Content = root;
        Closed += (_, _) =>
        {
            _auditCts?.Cancel();
            _auditCts?.Dispose();
            _auditCts = null;
        };
    }

    public static Task<IReadOnlyList<string>?> ShowAsync(
        Window owner,
        string accountName,
        int defaultConcurrency,
        Func<
            TikTokCopyrightProofAuditSelection,
            IProgress<TikTokCopyrightProofAuditProgress>,
            CancellationToken,
            Task<IReadOnlyList<TikTokCopyrightProofAuditItem>>> audit)
    {
        var dialog = new TikTokCopyrightProofAuditDialog(accountName, defaultConcurrency, audit);
        return dialog.ShowDialog<IReadOnlyList<string>?>(owner);
    }

    private void OnSelectionChanged(object? sender, RoutedEventArgs e)
    {
        if (_auditCts is null)
            _startButton.IsEnabled = HasSelectedStatus();
    }

    private bool HasSelectedStatus() =>
        _publishedBox.IsChecked == true || _videoReviewingBox.IsChecked == true;

    private TikTokCopyrightProofAuditSelection CurrentSelection() =>
        new(
            _publishedBox.IsChecked == true,
            _videoReviewingBox.IsChecked == true,
            (int)(_concurrencyBox.Value ?? 6));

    private async void StartAudit()
    {
        if (_auditCts is not null || !HasSelectedStatus())
            return;
        await StartAuditAsync();
    }

    private async Task StartAuditAsync()
    {
        var selection = CurrentSelection();
        _results.Clear();
        RenderResults();
        _publishedBox.IsEnabled = false;
        _videoReviewingBox.IsEnabled = false;
        _concurrencyBox.IsEnabled = false;
        _startButton.IsEnabled = false;
        _stopButton.IsEnabled = true;
        _summary.Foreground = Brushes.Black;
        _summary.Text =
            $"正在读取原创管理列表；检测范围：{string.Join("、", selection.SelectedPlatformStatuses())}；" +
            $"并发：{selection.NormalizedConcurrency}。";
        _auditCts = new CancellationTokenSource();
        var progress = new Progress<TikTokCopyrightProofAuditProgress>(update =>
        {
            if (update.Total > 0)
            {
                _progress.Maximum = update.Total;
                _progress.Value = update.Completed;
            }

            if (update.Result is not null)
                _results[update.Result.Order] = update.Result;

            RenderResults();
            _summary.Text = update.Total > 0
                ? $"{update.Stage} {update.Completed}/{update.Total}：{update.CurrentTitle}{BuildCountsSuffix()}"
                : update.Stage;
        });

        try
        {
            var results = await _audit(selection, progress, _auditCts.Token);
            _results.Clear();
            foreach (var result in results)
                _results[result.Order] = result;
            RenderResults();
            _progress.Maximum = Math.Max(1, results.Count);
            _progress.Value = results.Count;
            _summary.Text = $"检查完成：共检查 {results.Count} 个{BuildCountsSuffix()}";
            _summary.Foreground = Brushes.Black;
        }
        catch (OperationCanceledException)
        {
            RenderResults();
            _summary.Text = $"检查已停止：已完成 {_results.Count} 个{BuildCountsSuffix()}";
            _summary.Foreground = Brushes.DarkOrange;
        }
        catch (Exception ex)
        {
            RenderResults();
            _summary.Text = $"检查失败：{ex.Message}";
            _summary.Foreground = Brushes.IndianRed;
        }
        finally
        {
            _auditCts?.Dispose();
            _auditCts = null;
            _stopButton.IsEnabled = false;
            _publishedBox.IsEnabled = true;
            _videoReviewingBox.IsEnabled = true;
            _concurrencyBox.IsEnabled = true;
            _startButton.Content = "重新检测";
            _startButton.IsEnabled = HasSelectedStatus();
            UpdateButtons();
        }
    }

    private void StopAudit() => _auditCts?.Cancel();

    private async void CopyMissingAsync()
    {
        var text = TikTokCopyrightProofAuditText.BuildMissingTitlesCopyText(OrderedResults());
        if (string.IsNullOrWhiteSpace(text))
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        await clipboard.SetTextAsync(text);
        _summary.Text = $"已复制 {MissingResults().Length} 个未补版权证明剧名。";
        _summary.Foreground = Brushes.SeaGreen;
    }

    private async void CopyFailedAsync()
    {
        var text = TikTokCopyrightProofAuditText.BuildFailedTitlesCopyText(OrderedResults());
        if (string.IsNullOrWhiteSpace(text))
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        await clipboard.SetTextAsync(text);
        _summary.Text = $"已复制 {FailedResults().Length} 个检查失败剧名。";
        _summary.Foreground = Brushes.SeaGreen;
    }

    private void ExportResults()
    {
        try
        {
            var path = TikTokCopyrightProofAuditExcelService.Export(
                _accountName,
                OrderedResults());
            _summary.Text = $"检查结果已导出：{path}";
            _summary.Foreground = Brushes.SeaGreen;
        }
        catch (Exception ex)
        {
            _summary.Text = $"导出失败：{ex.Message}";
            _summary.Foreground = Brushes.IndianRed;
        }
    }

    private void CompleteMissing()
    {
        var titles = MissingResults()
            .Select(item => item.Title)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titles.Length > 0)
            Close(titles);
    }

    private void RenderResults()
    {
        var results = OrderedResults();
        _output.Text = results.Count == 0
            ? "正在读取并检查所选剧集…"
            : TikTokCopyrightProofAuditText.BuildDisplayText(results);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _copyMissingButton.IsEnabled = MissingResults().Length > 0;
        _copyFailedButton.IsEnabled = FailedResults().Length > 0;
        _exportButton.IsEnabled = _results.Count > 0 && _auditCts is null;
        _completeButton.IsEnabled = MissingResults().Length > 0 && _auditCts is null;
    }

    private IReadOnlyList<TikTokCopyrightProofAuditItem> OrderedResults() =>
        _results.Values.OrderBy(item => item.Order).ToArray();

    private TikTokCopyrightProofAuditItem[] MissingResults() =>
        _results.Values
            .Where(item => item.State.IsIncomplete())
            .OrderBy(item => item.Order)
            .ToArray();

    private TikTokCopyrightProofAuditItem[] FailedResults() =>
        _results.Values
            .Where(item => item.State == TikTokCopyrightProofAuditState.Failed)
            .OrderBy(item => item.Order)
            .ToArray();

    private string BuildCountsSuffix()
    {
        var values = _results.Values.ToArray();
        if (values.Length == 0)
            return string.Empty;
        return $"；材料齐全 {values.Count(item => item.State == TikTokCopyrightProofAuditState.HasMaterial)}，" +
               $"仅 PDF {values.Count(item => item.State == TikTokCopyrightProofAuditState.ProductionAgreementOnly)}，" +
               $"部分缺失 {values.Count(item => item.State == TikTokCopyrightProofAuditState.PartialMaterial)}，" +
               $"全部未填 {values.Count(item => item.State == TikTokCopyrightProofAuditState.MissingMaterial)}，" +
               $"版权通过 {values.Count(item => item.State == TikTokCopyrightProofAuditState.SkippedApproved)}，" +
               $"暂不可编辑 {values.Count(item => item.State == TikTokCopyrightProofAuditState.SkippedUneditable)}，" +
               $"失败 {values.Count(item => item.State == TikTokCopyrightProofAuditState.Failed)}";
    }

    private void CloseDialog()
    {
        _auditCts?.Cancel();
        Close(null);
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
