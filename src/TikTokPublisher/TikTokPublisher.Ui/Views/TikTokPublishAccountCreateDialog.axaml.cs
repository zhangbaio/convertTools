using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Ui.Views;

public partial class TikTokPublishAccountCreateDialog : Window
{
    internal sealed record Target(int Number, string Label);
    private sealed record PreviewRow(PublishAccountMaterial Material, CheckBox Selection, TextBlock Destination, TextBlock Status);
    private sealed record Job(PreviewRow Row, Target Target);

    private readonly List<PreviewRow> _previewRows = [];
    private readonly List<Bitmap> _thumbnails = [];
    private IReadOnlyList<Target> _targets = [];
    private Func<IReadOnlyDictionary<int, TikTokAccountProfile>>? _resolveAccounts;
    private IReadOnlyDictionary<int, TikTokAccountProfile>? _accounts;
    private TikTokPublishAccountBatchRunner? _runner;
    private List<Job>? _jobs;
    private CancellationTokenSource? _running;
    private bool _loading;
    private bool _updatingSelection;

    public TikTokPublishAccountCreateDialog()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (_running is null)
                return;
            e.Cancel = true;
            _running.Cancel();
        };
        Closed += (_, _) => DisposeThumbnails();
    }

    internal void Bind(TikTokPublishAccountBatchRunner runner, IReadOnlyList<Target> targets,
        Func<IReadOnlyDictionary<int, TikTokAccountProfile>> resolveAccounts, bool alsoImportsMainAccounts = false)
    {
        _runner = runner;
        _targets = targets;
        _resolveAccounts = resolveAccounts;
        PlanText.Text = alsoImportsMainAccounts
            ? $"即将添加 {targets.Count} 个主账号。勾选的素材按预览分配，每个主账号最多提交一次平台批量创建申请。"
            : $"主账号 {targets[0].Label}。选择一套素材，平台批量创建该主账号下的发布账号。";
        PlanText.Text += "\n请先阅读平台创建须知。提交即确认授权用户均已年满 18 周岁，并同意平台服务条款及已阅读隐私政策。";
    }

    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        if (_loading || _jobs is not null)
            return;
        _loading = true;
        FolderButton.IsEnabled = false;
        UpdateSubmitButton();
        try
        {
            var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择发布账号素材的父目录" });
            if (selected.FirstOrDefault()?.TryGetLocalPath() is not { } root)
                return;
            RootBox.Text = root;
            StatusText.Text = "正在扫描素材…";
            RowsPanel.Children.Clear();
            _previewRows.Clear();
            DisposeThumbnails();
            var rows = await Task.Run(() => TikTokPublishAccountMaterials.Scan(root, CancellationToken.None));
            if (IsVisible)
                ShowPreview(rows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = "无法读取素材目录，请检查访问权限。";
        }
        finally
        {
            _loading = false;
            FolderButton.IsEnabled = true;
            UpdateSubmitButton();
        }
    }

    private void ShowPreview(IReadOnlyList<PublishAccountMaterial> rows)
    {
        _updatingSelection = true;
        var suggested = 0;
        foreach (var material in rows)
        {
            var ready = material.State == PublishAccountMaterialState.Ready;
            var select = new CheckBox { IsEnabled = ready, IsChecked = ready && suggested++ < _targets.Count };
            var destination = Cell("");
            var status = Cell(material.Detail);
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("30,55,2*,1.2*,1.3*"), ColumnSpacing = 10 };
            AddCell(line, 0, select);
            if (ready && TryLoadThumbnail(material.AvatarPath) is { } thumbnail)
                AddCell(line, 1, new Image { Source = thumbnail, Width = 48, Height = 48 });
            AddCell(line, 2, new StackPanel
            {
                Spacing = 3,
                Children = { new TextBlock { Text = material.Nickname, FontWeight = FontWeight.SemiBold }, Cell(material.Biography) },
            });
            AddCell(line, 3, destination);
            AddCell(line, 4, status);
            RowsPanel.Children.Add(new Border
            {
                Padding = new Thickness(5, 8), BorderBrush = Brush.Parse("#DFE6EE"),
                BorderThickness = new Thickness(0, 0, 0, 1), Child = line,
            });
            _previewRows.Add(new PreviewRow(material, select, destination, status));
            select.IsCheckedChanged += (_, _) => OnSelectionChanged(select);
        }
        _updatingSelection = false;
        RefreshAssignments();
        StatusText.Text = rows.Count == 0 ? "目录中没有账号素材。" :
            $"可用 {rows.Count(row => row.State == PublishAccountMaterialState.Ready)} 套，已使用 {rows.Count(row => row.State == PublishAccountMaterialState.Used)} 套，待核对 {rows.Count(row => row.State == PublishAccountMaterialState.Pending)} 套。";
    }

    private void OnSelectionChanged(CheckBox selection)
    {
        if (_updatingSelection)
            return;
        if (_jobs is null && _previewRows.Count(row => row.Selection.IsChecked == true) > _targets.Count)
        {
            _updatingSelection = true;
            selection.IsChecked = false;
            _updatingSelection = false;
            StatusText.Text = "每个主账号最多分配一套素材，请先取消已勾选的素材。";
        }
        RefreshAssignments();
    }

    private void RefreshAssignments()
    {
        if (_jobs is null)
        {
            var index = 0;
            foreach (var row in _previewRows)
                row.Destination.Text = row.Selection.IsChecked == true ? $"主账号 · {_targets[index++].Label}" : "";
        }
        UpdateSubmitButton();
    }

    private void UpdateSubmitButton() =>
        SubmitButton.IsEnabled = !_loading && _running is null &&
            _previewRows.Any(row => row.Selection.IsEnabled && row.Selection.IsChecked == true);

    private async void OnSubmit(object? sender, RoutedEventArgs e)
    {
        if (_runner is null || _resolveAccounts is null || !SubmitButton.IsEnabled)
            return;
        using var cancellation = new CancellationTokenSource();
        _running = cancellation;
        FolderButton.IsEnabled = SubmitButton.IsEnabled = false;
        CloseButton.Content = "停止";
        try
        {
            if (_jobs is null)
            {
                _accounts = _resolveAccounts();
                _jobs = _previewRows.Where(row => row.Selection.IsChecked == true)
                    .Select((row, index) => new Job(row, _targets[index])).ToList();
                foreach (var row in _previewRows)
                    row.Selection.IsEnabled = false;
                foreach (var job in _jobs)
                    job.Row.Selection.IsEnabled = _accounts.ContainsKey(job.Target.Number);
            }

            var jobs = _jobs.Where(job => job.Row.Selection.IsEnabled && job.Row.Selection.IsChecked == true).ToArray();
            foreach (var job in _jobs.Where(job => !_accounts!.ContainsKey(job.Target.Number)))
                job.Row.Status.Text = "对应主账号未添加成功";

            for (var i = 0; i < jobs.Length && !cancellation.IsCancellationRequested; i++)
            {
                var job = jobs[i];
                var row = job.Row;
                row.Selection.IsEnabled = false;
                row.Status.Text = "正在核对已有登录状态…";
                StatusText.Text = $"正在处理 {i + 1}/{jobs.Length} · {job.Target.Label}";
                var finished = false;
                var progress = new Progress<string>(message =>
                {
                    if (!finished)
                        row.Status.Text = message;
                });
                var result = await _runner.CreateAsync(_accounts![job.Target.Number], row.Material, cancellation.Token, progress);
                finished = true;
                row.Status.Text = result.Message;
                row.Selection.IsEnabled = result.State == PublishAccountCreationState.Retryable;
            }

            StatusText.Text = cancellation.IsCancellationRequested ? "已停止。已开始提交的申请按平台返回记录。" :
                "本轮处理完成。可勾选未受理的条目重试，待核对条目请先查看平台记录。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = "无法启动本轮批量创建，请检查账号和目录。";
        }
        finally
        {
            _running = null;
            CloseButton.Content = "关闭";
            SubmitButton.Content = _jobs is null ? "提交创建申请" : "重试勾选项";
            FolderButton.IsEnabled = _jobs is null;
            UpdateSubmitButton();
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (_running is not null)
            _running.Cancel();
        else
            Close();
    }

    private Bitmap? TryLoadThumbnail(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = Bitmap.DecodeToWidth(stream, 64);
            _thumbnails.Add(bitmap);
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private void DisposeThumbnails()
    {
        foreach (var image in _thumbnails)
            image.Dispose();
        _thumbnails.Clear();
    }

    private static TextBlock Cell(string value) =>
        new() { Text = value, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    private static void AddCell(Grid grid, int column, Control cell)
    {
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }
}
