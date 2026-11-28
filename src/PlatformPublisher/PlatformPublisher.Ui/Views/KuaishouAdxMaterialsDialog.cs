using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PlatformPublisher.Adx.Automation;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Desktop.Views;

public sealed class KuaishouAdxMaterialsDialog : Window
{
    private readonly AdxAutomationService _adx;
    private readonly AdxBatchStore _batchStore;
    private readonly KuaishouAdxBatchResolver _resolver;
    private readonly KuaishouAdxProjectContext _context;
    private readonly Func<KuaishouAdxPublishPayload, KuaishouAdxProjectContext, bool, Task> _queue;
    private readonly int _topCount;
    private KuaishouPersonalConfig? _kuaishouConfig;
    private readonly TextBox _titleTemplate = new() { Text = "{新剧名}{排名}-{素材ID}" };
    private readonly TextBox _materialType = new() { Text = "高光" };
    private readonly TextBox _authorDeclaration = new() { Text = "含AI生成内容" };
    private readonly ComboBox _coverMode = new() { ItemsSource = new[] { "ADX封面", "项目竖屏海报", "单图封面" }, SelectedIndex = 0 };
    private readonly TextBox _coverPath = new() { Watermark = "单图封面路径" };
    private readonly CheckBox _autoStart = new() { Content = "加入队列后立即执行", IsChecked = false };
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly StackPanel _list = new() { Spacing = 5 };
    private readonly List<(AdxCandidate Candidate, CheckBox Check)> _remote = [];
    private readonly List<(KuaishouLocalAdxMaterial Item, CheckBox Check)> _local = [];
    private readonly StackPanel _remoteActions = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel _localActions = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private CancellationTokenSource? _cts;
    private bool _busy;

    public KuaishouAdxMaterialsDialog(AdxAutomationService adx, AdxBatchStore batchStore,
        KuaishouAdxBatchResolver resolver, KuaishouAdxProjectContext context, int topCount,
        bool publishLocal,
        Func<KuaishouAdxPublishPayload, KuaishouAdxProjectContext, bool, Task> queue)
    {
        _adx = adx; _batchStore = batchStore; _resolver = resolver; _context = context;
        _topCount = Math.Clamp(topCount, 1, 20); _queue = queue;
        Title = publishLocal ? "快手 ADX 素材发布" : "快手 ADX 素材下载";
        Width = 980; Height = 720; MinWidth = 760; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildContent();
        LoadSettings();
        ShowMode(publishLocal);
        Opened += async (_, _) => { if (publishLocal) await LoadLocalAsync(); else await QueryAsync(); };
        Closed += (_, _) => _cts?.Cancel();
    }

    private Control BuildContent()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto"), RowSpacing = 9, Margin = new Thickness(14) };
        var heading = new TextBlock
        {
            Text = $"《{_context.NewTitle}》　原剧名：{_context.OriginalTitle}\n{_context.WorkflowDirectory}",
            FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        root.Children.Add(heading);

        var login = new Border
        {
            Background = Avalonia.Media.Brush.Parse("#F8FAFC"),
            BorderBrush = Avalonia.Media.Brush.Parse("#E2E8F0"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7),
            Child = new TextBlock
            {
                Text = "ADX 登录状态由“系统设置 → ADX素材服务”统一维护，本页面自动复用。",
                Foreground = Avalonia.Media.Brush.Parse("#667085"),
            },
        };
        Grid.SetRow(login, 1); root.Children.Add(login);

        var config = new Grid { ColumnDefinitions = new("70,2*,70,100,70,110,70,*"), ColumnSpacing = 6 };
        Add(config, Label("标题模板"), 0); Add(config, _titleTemplate, 1);
        Add(config, Label("剪辑类型"), 2); Add(config, _materialType, 3);
        Add(config, Label("作者声明"), 4); Add(config, _authorDeclaration, 5);
        Add(config, Label("封面"), 6);
        var cover = new Grid { ColumnDefinitions = new("130,*,Auto"), ColumnSpacing = 5 };
        cover.Children.Add(_coverMode); Grid.SetColumn(_coverPath, 1); cover.Children.Add(_coverPath);
        var pickCover = Button("选择", PickCoverAsync); Grid.SetColumn(pickCover, 2); cover.Children.Add(pickCover); Add(config, cover, 7);
        Grid.SetRow(config, 2); root.Children.Add(config);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 3); root.Children.Add(scroll);

        _remoteActions.Children.Add(Button("查询ADX", QueryAsync));
        _remoteActions.Children.Add(Button($"选择前{_topCount}条", () => SelectRemote(_topCount)));
        _remoteActions.Children.Add(Button("全选未下载", () => { foreach (var row in _remote.Where(x => !x.Candidate.Downloaded)) row.Check.IsChecked = true; }));
        _remoteActions.Children.Add(Button("仅下载", async () => await DownloadAsync(false)));
        _remoteActions.Children.Add(Button("下载并加入发布", async () => await DownloadAsync(true)));
        _localActions.Children.Add(Button("刷新本地素材", LoadLocalAsync));
        _localActions.Children.Add(Button("选择全部未发布", () => { foreach (var row in _local) row.Check.IsChecked = row.Item.Status == KuaishouLocalAdxMaterialStatus.Available; }));
        _localActions.Children.Add(Button("加入发布队列", QueueLocalAsync));
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        footer.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        Grid.SetColumn(buttons, 1); buttons.Children.Add(_autoStart); buttons.Children.Add(_remoteActions); buttons.Children.Add(_localActions);
        buttons.Children.Add(Button("停止", () => _cts?.Cancel())); footer.Children.Add(buttons);
        Grid.SetRow(footer, 4); root.Children.Add(footer);
        return root;
    }

    private void ShowMode(bool local)
    {
        _remoteActions.IsVisible = !local;
        _localActions.IsVisible = local;
    }

    private void LoadSettings()
    {
        var config = KuaishouPersonalConfig.Load(new PlatformPublisher.Common.Models.PublishJob
        {
            Platform = PlatformPublisher.Common.Models.PublishPlatform.KuaishouPersonalRevenue,
            AccountId = _context.AccountId, ConfigPath = _context.ConfigPath,
        });
        _kuaishouConfig = config;
        _titleTemplate.Text = config.MaterialTitleTemplate;
        _materialType.Text = config.MaterialType;
        _authorDeclaration.Text = config.MaterialAuthorDeclaration;
        _coverMode.SelectedIndex = config.MaterialCoverMode switch { "project-poster" => 1, "single-image" => 2, _ => 0 };
        _coverPath.Text = config.MaterialCoverPath;
        _status.Text = _adx.GetLoginStatus().Message;
    }

    private async Task QueryAsync()
    {
        if (!Begin()) return;
        try
        {
            _list.Children.Clear(); _remote.Clear();
            var result = await _adx.QueryAsync(new AdxQueryRequest(_context.AccountId, _context.NewTitle,
                _context.OriginalTitle, _context.WorkflowDirectory, _adx.LoadSettings().QueryLimit), Progress(), _cts!.Token);
            foreach (var candidate in result.Candidates)
            {
                var check = new CheckBox
                {
                    Content = $"TOP {candidate.Rank}　ID {candidate.MaterialId}　曝光 {candidate.Exposure:N0}　播放 {candidate.PlayCount:N0}　点赞 {candidate.LikeCount:N0}" + (candidate.Downloaded ? "　[已下载]" : ""),
                    Margin = new Thickness(5),
                };
                _remote.Add((candidate, check)); _list.Children.Add(check);
            }
            SelectRemote(_topCount); _status.Text = $"ADX 返回 {result.Candidates.Count}/{result.Total} 条素材。";
        }
        catch (Exception ex) { _status.Text = "ADX 查询失败：" + ex.Message; }
        finally { End(); }
    }

    private async Task DownloadAsync(bool queue)
    {
        var selected = _remote.Where(row => row.Check.IsChecked == true).Select(row => row.Candidate.MaterialId).ToArray();
        if (selected.Length == 0) { _status.Text = "请先选择要下载的 ADX 素材。"; return; }
        if (!Begin()) return;
        try
        {
            var result = await _adx.DownloadAsync(new AdxDownloadRequest(_context.AccountId, _context.NewTitle,
                _context.OriginalTitle, _context.WorkflowDirectory, selected), Progress(), _cts!.Token);
            _status.Text = result.Message;
            if (!queue) return;
            var manifest = _batchStore.Read(Path.Combine(result.DownloadDirectory, AdxBatchStore.ManifestFileName))
                ?? throw new InvalidOperationException("下载完成但无法读取 ADX 批次清单。");
            var payload = BuildPayload(manifest.Items.Select(item => new KuaishouAdxPublishItem
            {
                MaterialId = item.MaterialId, Rank = item.Rank, VideoPath = item.VideoPath,
                CoverPath = item.CoverPath, ManifestPath = manifest.ManifestPath,
            }));
            await SaveMaterialDefaultsAsync();
            await _queue(payload, _context, _autoStart.IsChecked == true); Close();
        }
        catch (Exception ex) { _status.Text = "ADX 下载/入队失败：" + ex.Message; }
        finally { End(); }
    }

    private async Task LoadLocalAsync()
    {
        if (!Begin()) return;
        try
        {
            _list.Children.Clear(); _local.Clear();
            foreach (var item in _resolver.List(_context.WorkflowDirectory, _context.AccountId))
            {
                var label = item.Status switch { KuaishouLocalAdxMaterialStatus.Published => "已发布", KuaishouLocalAdxMaterialStatus.Missing => "文件缺失", KuaishouLocalAdxMaterialStatus.SubmissionUnknown => "结果待核对", _ => "待发布" };
                var check = new CheckBox { Content = $"TOP {item.Rank}　ID {item.MaterialId}　{Path.GetFileName(item.VideoPath)}　[{label}]", IsEnabled = item.Status is not (KuaishouLocalAdxMaterialStatus.Missing or KuaishouLocalAdxMaterialStatus.SubmissionUnknown), IsChecked = item.Status == KuaishouLocalAdxMaterialStatus.Available, Margin = new Thickness(5) };
                _local.Add((item, check)); _list.Children.Add(check);
            }
            _status.Text = $"本地找到 {_local.Count} 条 ADX 素材，其中 {_local.Count(row => row.Item.Status == KuaishouLocalAdxMaterialStatus.Published)} 条已发布。";
        }
        catch (Exception ex) { _status.Text = "读取本地 ADX 素材失败：" + ex.Message; }
        finally { End(); }
        await Task.CompletedTask;
    }

    private async Task QueueLocalAsync()
    {
        var selected = _local.Where(row => row.Check.IsChecked == true).Select(row => new KuaishouAdxPublishItem
        {
            MaterialId = row.Item.MaterialId, Rank = row.Item.Rank, VideoPath = row.Item.VideoPath,
            CoverPath = row.Item.CoverPath, ManifestPath = row.Item.ManifestPath,
        }).ToArray();
        if (selected.Length == 0) { _status.Text = "请先选择要发布的本地 ADX 素材。"; return; }
        try
        {
            await SaveMaterialDefaultsAsync();
            await _queue(BuildPayload(selected), _context, _autoStart.IsChecked == true); Close();
        }
        catch (Exception ex) { _status.Text = "加入快手发布队列失败：" + ex.Message; }
    }

    private KuaishouAdxPublishPayload BuildPayload(IEnumerable<KuaishouAdxPublishItem> items) => new()
    {
        OriginalTitle = _context.OriginalTitle, NewTitle = _context.NewTitle, Items = items.ToList(),
        Options = new KuaishouAdxPublishOptions
        {
            TitleTemplate = _titleTemplate.Text ?? string.Empty,
            MaterialType = _materialType.Text ?? string.Empty,
            AuthorDeclaration = _authorDeclaration.Text ?? string.Empty,
            CoverMode = _coverMode.SelectedIndex switch { 1 => "project-poster", 2 => "single-image", _ => "adx" },
            CoverPath = _coverPath.Text ?? string.Empty,
        },
    };

    private async Task SaveMaterialDefaultsAsync()
    {
        if (_kuaishouConfig is null) return;
        _kuaishouConfig.MaterialTitleTemplate = _titleTemplate.Text?.Trim() ?? string.Empty;
        _kuaishouConfig.MaterialType = _materialType.Text?.Trim() ?? string.Empty;
        _kuaishouConfig.MaterialAuthorDeclaration = _authorDeclaration.Text?.Trim() ?? string.Empty;
        _kuaishouConfig.MaterialCoverMode = _coverMode.SelectedIndex switch { 1 => "project-poster", 2 => "single-image", _ => "adx" };
        _kuaishouConfig.MaterialCoverPath = _coverPath.Text?.Trim() ?? string.Empty;
        var path = string.IsNullOrWhiteSpace(_context.ConfigPath)
            ? KuaishouPersonalConfig.DefaultConfigPath(_context.AccountId)
            : _context.ConfigPath;
        await _kuaishouConfig.SaveAsync(path);
    }

    private async Task PickCoverAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "选择快手宣发素材统一封面", AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("图片") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp"] }],
        });
        if (files.Count > 0) { _coverPath.Text = files[0].Path.LocalPath; _coverMode.SelectedIndex = 2; }
    }

    private void SelectRemote(int count)
    {
        foreach (var row in _remote) row.Check.IsChecked = false;
        foreach (var row in _remote.Take(count)) row.Check.IsChecked = true;
    }
    private IProgress<AdxProgress> Progress() => new Progress<AdxProgress>(value => _status.Text = value.Message);
    private bool Begin() { if (_busy) return false; _busy = true; _cts?.Dispose(); _cts = new CancellationTokenSource(); return true; }
    private void End() { _busy = false; }
    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
    private static Button Button(string text, Action action) { var button = new Button { Content = text }; button.Click += (_, _) => action(); return button; }
    private static Button Button(string text, Func<Task> action) { var button = new Button { Content = text }; button.Click += async (_, _) => await action(); return button; }
    private static void Add(Grid grid, Control control, int column) { Grid.SetColumn(control, column); grid.Children.Add(control); }
}
