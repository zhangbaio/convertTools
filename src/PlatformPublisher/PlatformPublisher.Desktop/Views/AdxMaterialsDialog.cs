using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PlatformPublisher.Adx.Automation;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Weixin.Publishing;

namespace PlatformPublisher.Desktop.Views;

public sealed class AdxMaterialsDialog : Window
{
    private readonly AdxAutomationService _service;
    private readonly AdxBatchStore _batchStore;
    private readonly string _accountId;
    private readonly string _accountName;
    private readonly string _accountSessionDirectory;
    private readonly Func<AdxPublishPayload, string, string, string, string, bool, Task> _queuePublish;
    private readonly TextBox _baseUrl = new();
    private readonly TextBox _username = new();
    private readonly TextBox _password = new() { PasswordChar = '●' };
    private readonly NumericUpDown _limit = new() { Minimum = 1, Maximum = 200, Width = 90 };
    private readonly NumericUpDown _concurrency = new() { Minimum = 1, Maximum = 5, Width = 90 };
    private readonly CheckBox _headless = new() { Content = "后台运行浏览器" };
    private readonly ComboBox _finalAction = new() { ItemsSource = new[] { "保存草稿", "直接发表" }, SelectedIndex = 0, Width = 100 };
    private readonly TextBox _workflow = new();
    private readonly TextBox _originalTitle = new();
    private readonly TextBox _newTitle = new();
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly WrapPanel _candidatePanel = new();
    private readonly List<(AdxCandidate Candidate, CheckBox Check)> _candidateChecks = [];
    private CancellationTokenSource? _cts;
    private bool _busy;

    public AdxMaterialsDialog(AdxAutomationService service, AdxBatchStore batchStore,
        string accountId, string accountName, string accountSessionDirectory,
        string workflowDirectory, string originalTitle, string newTitle,
        Func<AdxPublishPayload, string, string, string, string, bool, Task> queuePublish)
    {
        _service = service;
        _batchStore = batchStore;
        _accountId = accountId;
        _accountName = accountName;
        _accountSessionDirectory = accountSessionDirectory;
        _queuePublish = queuePublish;
        Title = "ADX 素材选择与发布";
        Width = 1080;
        Height = 760;
        MinWidth = 860;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _workflow.Text = workflowDirectory;
        _originalTitle.Text = originalTitle;
        _newTitle.Text = newTitle;
        Content = BuildContent();
        LoadSettings();
        Closed += (_, _) => _cts?.Cancel();
    }

    private Control BuildContent()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new Thickness(14), RowSpacing = 10 };
        var settings = new Grid { ColumnDefinitions = new("90,2*,80,1.2*,80,90,80,90,Auto"), ColumnSpacing = 7 };
        Add(settings, new TextBlock { Text = "ADX 服务", VerticalAlignment = VerticalAlignment.Center }, 0);
        Add(settings, _baseUrl, 1);
        Add(settings, new TextBlock { Text = "账号", VerticalAlignment = VerticalAlignment.Center }, 2);
        Add(settings, _username, 3);
        Add(settings, new TextBlock { Text = "查询数", VerticalAlignment = VerticalAlignment.Center }, 4);
        Add(settings, _limit, 5);
        Add(settings, new TextBlock { Text = "并发", VerticalAlignment = VerticalAlignment.Center }, 6);
        Add(settings, _concurrency, 7);
        var configure = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        configure.Children.Add(_headless);
        configure.Children.Add(Button("保存配置", async () => await SaveSettingsAsync()));
        configure.Children.Add(Button("登录 ADX", async () => await LoginAsync()));
        Add(settings, configure, 8);
        root.Children.Add(settings);

        var query = new Grid { ColumnDefinitions = new("90,2*,80,*,80,*,Auto"), ColumnSpacing = 7 };
        Grid.SetRow(query, 1);
        Add(query, new TextBlock { Text = "工作目录", VerticalAlignment = VerticalAlignment.Center }, 0);
        Add(query, _workflow, 1);
        Add(query, new TextBlock { Text = "原剧名", VerticalAlignment = VerticalAlignment.Center }, 2);
        Add(query, _originalTitle, 3);
        Add(query, new TextBlock { Text = "新剧名", VerticalAlignment = VerticalAlignment.Center }, 4);
        Add(query, _newTitle, 5);
        Add(query, Button("查询 ADX", QueryAsync), 6);
        root.Children.Add(query);

        var scroll = new ScrollViewer { Content = _candidatePanel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        Grid.SetRow(footer, 3);
        footer.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        Grid.SetColumn(actions, 1);
        actions.Children.Add(Button("全选未下载", () => { foreach (var item in _candidateChecks.Where(item => !item.Candidate.Downloaded)) item.Check.IsChecked = true; }));
        actions.Children.Add(Button("取消全选", () => { foreach (var item in _candidateChecks) item.Check.IsChecked = false; }));
        actions.Children.Add(_finalAction);
        actions.Children.Add(Button("仅下载", async () => await DownloadAsync(false)));
        actions.Children.Add(Button("下载后自动发布", async () => await DownloadAsync(true)));
        actions.Children.Add(Button("停止", () => _cts?.Cancel()));
        footer.Children.Add(actions);
        root.Children.Add(footer);
        return root;
    }

    private void LoadSettings()
    {
        var settings = _service.LoadSettings();
        _baseUrl.Text = settings.BaseUrl;
        _username.Text = settings.Username;
        _limit.Value = settings.QueryLimit;
        _concurrency.Value = settings.DownloadConcurrency;
        _headless.IsChecked = settings.Headless;
        var login = _service.GetLoginStatus();
        _status.Text = $"账号 {_accountName} · ADX {LoginText(login.State)}：{login.Message}";
    }

    private async Task SaveSettingsAsync()
    {
        _service.SaveSettings(CurrentSettings());
        if (!string.IsNullOrEmpty(_password.Text)) { _service.SavePassword(_password.Text); _password.Text = string.Empty; }
        _status.Text = "ADX 配置已保存。";
        await Task.CompletedTask;
    }

    private async Task LoginAsync()
    {
        if (!TryBeginOperation()) return;
        try { await SaveSettingsAsync(); _status.Text = (await _service.LoginAsync(_cts!.Token)).Message; }
        catch (Exception ex) { _status.Text = "ADX 登录失败：" + ex.Message; }
        finally { _busy = false; }
    }

    private async Task QueryAsync()
    {
        if (!TryBeginOperation()) return;
        try
        {
            _candidatePanel.Children.Clear();
            _candidateChecks.Clear();
            var result = await _service.QueryAsync(new AdxQueryRequest(_accountId, _newTitle.Text ?? string.Empty,
                _originalTitle.Text ?? string.Empty, _workflow.Text ?? string.Empty, (int)(_limit.Value ?? 50)), Progress(), _cts!.Token);
            foreach (var candidate in result.Candidates)
            {
                var check = new CheckBox { Content = $"TOP {candidate.Rank}　ID {candidate.MaterialId}\n曝光 {candidate.Exposure:N0}　播放 {candidate.PlayCount:N0}　点赞 {candidate.LikeCount:N0}" + (candidate.Downloaded ? "\n已下载" : string.Empty), Width = 245, MinHeight = 82, Margin = new Thickness(4) };
                _candidateChecks.Add((candidate, check));
                _candidatePanel.Children.Add(new Border { BorderBrush = Avalonia.Media.Brushes.LightGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Child = check });
            }
        }
        catch (OperationCanceledException) { _status.Text = "ADX 查询已停止。"; }
        catch (Exception ex) { _status.Text = "ADX 查询失败：" + ex.Message; }
        finally { _busy = false; }
    }

    private async Task DownloadAsync(bool autoPublish)
    {
        var selected = _candidateChecks.Where(item => item.Check.IsChecked == true).Select(item => item.Candidate.MaterialId).ToArray();
        if (selected.Length == 0) { _status.Text = "请先勾选要下载的 ADX 素材。"; return; }
        if (!TryBeginOperation()) return;
        try
        {
            var workflow = _workflow.Text ?? string.Empty;
            var result = await _service.DownloadAsync(new AdxDownloadRequest(_accountId, _newTitle.Text ?? string.Empty,
                _originalTitle.Text ?? string.Empty, workflow, selected), Progress(), _cts!.Token);
            if (!autoPublish) { _status.Text = result.Message; return; }
            var manifestPath = Path.Combine(result.DownloadDirectory, AdxBatchStore.ManifestFileName);
            var manifest = _batchStore.Read(manifestPath) ?? throw new InvalidOperationException("下载完成但无法读取 ADX 批次清单。");
            var payload = new AdxPublishPayload
            {
                OriginalTitle = _originalTitle.Text ?? string.Empty,
                NewTitle = _newTitle.Text ?? string.Empty,
                PublishOptionsJson = new WeixinPublishOptions { EpisodeSelectionMode = "all", FinalAction = _finalAction.SelectedIndex == 1 ? "publish" : "draft", ReplaceCoverWithLocalImage = true }.ToJson(),
                Items = manifest.Items.Select(item => new AdxPublishItem(item.MaterialId, item.VideoPath, item.CoverPath, item.Description, item.ShortTitle, manifestPath)).ToList(),
            };
            await _queuePublish(payload, workflow, _accountId, _accountName, _accountSessionDirectory, true);
            _status.Text = $"ADX 下载完成，自动发布任务已执行；终态为{(_finalAction.SelectedIndex == 1 ? "直接发表" : "保存草稿")}。";
        }
        catch (OperationCanceledException) { _status.Text = "ADX 下载或发布已停止。"; }
        catch (Exception ex) { _status.Text = "ADX 下载/发布失败：" + ex.Message; }
        finally { _busy = false; }
    }

    private AdxSettings CurrentSettings() => new()
    {
        BaseUrl = _baseUrl.Text ?? string.Empty, Username = _username.Text ?? string.Empty,
        QueryLimit = (int)(_limit.Value ?? 50), DownloadConcurrency = (int)(_concurrency.Value ?? 3),
        DefaultTopCount = 5, Headless = _headless.IsChecked == true,
    };

    private IProgress<AdxProgress> Progress() => new Progress<AdxProgress>(value => _status.Text = value.Message);
    private bool TryBeginOperation()
    {
        if (_busy) { _status.Text = "已有 ADX 任务正在运行，请先停止或等待完成。"; return false; }
        _busy = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return true;
    }
    private static string LoginText(AdxLoginState state) => state switch { AdxLoginState.LoggedIn => "已登录", AdxLoginState.Checking => "验证中", AdxLoginState.Failed => "登录失败", AdxLoginState.Expired => "已失效", AdxLoginState.NotConfigured => "未配置", _ => "未登录" };
    private static Button Button(string text, Action action) { var button = new Button { Content = text }; button.Click += (_, _) => action(); return button; }
    private static Button Button(string text, Func<Task> action) { var button = new Button { Content = text }; button.Click += async (_, _) => await action(); return button; }
    private static void Add(Grid grid, Control control, int column) { Grid.SetColumn(control, column); grid.Children.Add(control); }
}
