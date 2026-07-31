using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Ui.Views;

public sealed class TikTokCopyrightProofAuditDialog : Window
{
    private readonly string _accountName;
    private readonly Func<
        IProgress<TikTokCopyrightProofAuditProgress>,
        CancellationToken,
        Task<IReadOnlyList<TikTokCopyrightProofAuditItem>>> _audit;
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
    private bool _started;

    private TikTokCopyrightProofAuditDialog(
        string accountName,
        Func<
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
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            RowSpacing = 10,
        };
        root.Children.Add(new TextBlock
        {
            Text =
                $"当前账号：{accountName}。系统将只读检查全部已发布剧集的版权证明页面，" +
                "不会修改或提交任何平台内容。",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });

        _summary = new TextBlock
        {
            Text = "正在准备检查…",
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(_summary, 1);
        root.Children.Add(_summary);

        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Height = 8,
        };
        Grid.SetRow(_progress, 2);
        root.Children.Add(_progress);

        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Text = "正在读取原创管理列表…",
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(
            _output,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(
            _output,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        Grid.SetRow(_output, 3);
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
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        Content = root;
        Opened += OnOpened;
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
        Func<
            IProgress<TikTokCopyrightProofAuditProgress>,
            CancellationToken,
            Task<IReadOnlyList<TikTokCopyrightProofAuditItem>>> audit)
    {
        var dialog = new TikTokCopyrightProofAuditDialog(accountName, audit);
        return dialog.ShowDialog<IReadOnlyList<string>?>(owner);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_started)
            return;
        _started = true;
        await StartAuditAsync();
    }

    private async Task StartAuditAsync()
    {
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
            var results = await _audit(progress, _auditCts.Token);
            _results.Clear();
            foreach (var result in results)
                _results[result.Order] = result;
            RenderResults();
            _progress.Maximum = Math.Max(1, results.Count);
            _progress.Value = results.Count;
            _summary.Text = $"检查完成：已发布 {results.Count} 个{BuildCountsSuffix()}";
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
            ? "正在读取并检查已发布剧集…"
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
            .Where(item => item.State == TikTokCopyrightProofAuditState.MissingMaterial)
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
        return $"；已上传 {values.Count(item => item.State == TikTokCopyrightProofAuditState.HasMaterial)}，" +
               $"未上传 {values.Count(item => item.State == TikTokCopyrightProofAuditState.MissingMaterial)}，" +
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
