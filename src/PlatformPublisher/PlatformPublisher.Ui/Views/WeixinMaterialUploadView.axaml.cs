using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ChannelsPublisher.Core.Models;
using PlatformPublisher.Adx.Automation;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Publishing.Models;
using PlatformPublisher.Weixin.Publishing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Desktop.Services;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinMaterialUploadView : UserControl
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".ts", ".wmv", ".webm",
    };

    private Func<IReadOnlyList<PublishAccount>>? _accountsProvider;
    private Func<PublishAccount?>? _accountProvider;
    private Action<string>? _selectAccount;
    private MainWindowViewModel? _shellViewModel;
    private AdxAutomationService? _adxService;
    private AdxBatchStore? _adxBatchStore;
    private WeixinDirectoryMaterialPublishService? _directoryPublishService;
    private WeixinMaterialDownloadService? _materialDownloadService;
    private WeixinHighlightScheduleService? _highlightScheduleService;
    private WeixinMaterialChannelVideoDeleteService? _channelVideoDeleteService;
    private UnifiedPublishViewModel? _unifiedPublishViewModel;
    private Action? _showUnifiedPublish;
    private Action? _showSettings;
    private MaterialProjectRowViewModel? _activeAdxRow;
    private PublishAccount? _activeAdxAccount;
    private readonly List<AdxCandidateSelection> _adxCandidates = [];
    private CancellationTokenSource? _adxCancellation;
    private bool _adxBusy;
    private static readonly HttpClient AdxImageClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private WeixinMaterialsWorkspaceViewModel? ViewModel => DataContext as WeixinMaterialsWorkspaceViewModel;
    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    public WeixinMaterialUploadView()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) => await ActivateAsync();
        PropertyChanged += async (_, args) =>
        {
            if (args.Property == IsVisibleProperty && IsVisible) await ActivateAsync();
        };
    }

    public void Bind(
        Func<IReadOnlyList<PublishAccount>> accountsProvider,
        Func<PublishAccount?> accountProvider,
        Action<string> selectAccount,
        MainWindowViewModel shellViewModel,
        IProjectScanner projectScanner,
        AdxAutomationService adxService,
        AdxBatchStore adxBatchStore,
        WeixinDirectoryMaterialPublishService directoryPublishService,
        WeixinMaterialDownloadService materialDownloadService,
        WeixinHighlightScheduleService highlightScheduleService,
        WeixinMaterialChannelVideoDeleteService channelVideoDeleteService,
        UnifiedPublishViewModel unifiedPublishViewModel,
        Action showUnifiedPublish,
        Action showSettings)
    {
        _accountsProvider = accountsProvider;
        _accountProvider = accountProvider;
        _selectAccount = selectAccount;
        _shellViewModel = shellViewModel;
        _adxService = adxService;
        _adxBatchStore = adxBatchStore;
        _directoryPublishService = directoryPublishService;
        _materialDownloadService = materialDownloadService;
        _highlightScheduleService = highlightScheduleService;
        _channelVideoDeleteService = channelVideoDeleteService;
        _unifiedPublishViewModel = unifiedPublishViewModel;
        _showUnifiedPublish = showUnifiedPublish;
        _showSettings = showSettings;

        var workspace = new WeixinMaterialsWorkspaceViewModel(projectScanner);
        workspace.AccountSelectionRequested += account => _selectAccount?.Invoke(account.Id);
        DataContext = workspace;
        ApplyAccount(accountProvider());
    }

    public void ApplyAccount(PublishAccount? account)
    {
        _shellViewModel?.UseWeixinAccount(account?.Id, account?.Name, account?.ProfileDir);
        ViewModel?.ApplyAccounts(_accountsProvider?.Invoke() ?? [], account);
        if (IsVisible) _ = ScanIfReadyAsync();
    }

    private async Task ActivateAsync()
    {
        _shellViewModel?.ActivateMaterialWorkflow();
        var account = _accountProvider?.Invoke();
        _shellViewModel?.UseWeixinAccount(account?.Id, account?.Name, account?.ProfileDir);
        ViewModel?.ApplyAccounts(_accountsProvider?.Invoke() ?? [], account);
        await ScanIfReadyAsync();
    }

    private async Task ScanIfReadyAsync()
    {
        if (ViewModel is null || ViewModel.IsBusy || !Directory.Exists(ViewModel.WorkspaceRoot)) return;
        await ViewModel.ScanAsync();
    }

    private async void OnScanProjectsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.ScanAsync();
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => ViewModel?.SetAllSelected(true);
    private void OnClearSelectionClick(object? sender, RoutedEventArgs e) => ViewModel?.SetAllSelected(false);

    private async void OnPickProjectDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow?.StorageProvider is null || ViewModel is null) return;
        var folders = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择素材发布工作目录",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var selected = folders[0].Path.LocalPath;
        if (_shellViewModel?.SetActiveAccountWorkRootDirectory(selected) != true) return;
        ViewModel.SetWorkspace(selected);
        await ViewModel.ScanAsync();
    }

    private async void OnDirectoryMaterialsClick(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow is null || _directoryPublishService is null || _unifiedPublishViewModel is null) return;
        var initial = Directory.Exists(ViewModel?.WorkspaceRoot) ? ViewModel!.WorkspaceRoot : string.Empty;
        var dialog = new MaterialDirectoryPublishDialog(_directoryPublishService, initial);
        if (!await dialog.ShowDialog<bool>(OwnerWindow) || dialog.Selection is not { } selection) return;
        var title = Path.GetFileName(selection.WorkspaceDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var draft = new PublishDraft
        {
            Source = new MaterialSourceSpec
            {
                Kind = MaterialSourceKind.DirectoryGroups,
                Label = "目录批量发表",
                WorkflowDirectory = selection.WorkspaceDirectory,
                OriginalTitle = title,
                NewTitle = title,
                Files = selection.Items.Select(item => item.VideoPath).ToList(),
            },
            Items = selection.Items.Select((item, index) => new ResolvedMaterial
            {
                Sequence = index + 1,
                VideoPath = item.VideoPath,
                CoverPath = string.IsNullOrWhiteSpace(item.CoverPath) ? null : item.CoverPath,
                Description = item.Description,
                Origin = new MaterialOrigin(MaterialSourceKind.DirectoryGroups, item.VideoPath),
            }).ToList(),
            Form = BuildForm(title, title),
        };
        await _unifiedPublishViewModel.AcceptDraftAsync(draft);
        SetStatus($"目录批量发表已生成 {draft.Items.Count} 条素材草稿。");
        _showUnifiedPublish?.Invoke();
    }

    private async void OnAdxProjectClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetRow(sender) is { } row) await OpenAdxAsync(row);
    }

    private async void OnDiscoverMaterialsClick(object? sender, RoutedEventArgs e)
    {
        await RunDownloadAsync(systemHighlights: false);
    }

    private async Task OpenAdxAsync(MaterialProjectRowViewModel row)
    {
        var account = _accountProvider?.Invoke();
        if (account is null || _adxService is null || _adxBatchStore is null)
        {
            SetStatus("请先选择视频号账号。");
            return;
        }
        _activeAdxRow = row;
        _activeAdxAccount = account;
        AdxDrawerTitle.Text = row.NewTitle;
        AdxDrawerOriginalTitle.Text = $"原剧名：{row.OriginalTitle}";
        AdxDrawer.IsVisible = true;
        var settings = _adxService.LoadSettings();
        SelectDefaultTopButton.Content = $"选择前{settings.DefaultTopCount}条";
        AdxCandidatePanel.Children.Clear();
        _adxCandidates.Clear();
        UpdateAdxSelectionSummary();
        var status = _adxService.GetLoginStatus();
        if (status.State != AdxLoginState.LoggedIn)
        {
            AdxLoginNotice.IsVisible = true;
            AdxLoginNoticeText.Text = status.Message ?? "请先在系统设置中登录 ADX。";
            SetAdxEmpty("ADX尚未登录", "完成一次登录后，视频号和快手页面都会复用该登录状态。", false);
            return;
        }
        AdxLoginNotice.IsVisible = false;
        await QueryAdxAsync();
    }

    private async Task QueryAdxAsync()
    {
        if (_adxService is null || _activeAdxRow is null || _activeAdxAccount is null || !BeginAdxOperation()) return;
        try
        {
            SetAdxEmpty("正在查询 ADX 素材…", $"按原剧名“{_activeAdxRow.OriginalTitle}”精确查询", true);
            var settings = _adxService.LoadSettings();
            var result = await _adxService.QueryAsync(new AdxQueryRequest(_activeAdxAccount.Id,
                _activeAdxRow.NewTitle, _activeAdxRow.OriginalTitle, _activeAdxRow.WorkflowDirectory,
                settings.QueryLimit), AdxProgress(), _adxCancellation!.Token);
            AdxCandidatePanel.Children.Clear();
            _adxCandidates.Clear();
            foreach (var candidate in result.Candidates)
                AddAdxCandidate(candidate);
            SetAdxEmpty("未找到匹配素材", "请确认项目原剧名与 ADX 中的名称完全一致。", false,
                result.Candidates.Count == 0);
            SelectAdxTop(settings.DefaultTopCount);
            AdxDrawerStatus.Text = $"ADX 返回 {result.Candidates.Count}/{result.Total} 条素材。";
        }
        catch (OperationCanceledException) { AdxDrawerStatus.Text = "ADX 查询已取消。"; }
        catch (Exception ex)
        {
            AdxDrawerStatus.Text = "ADX 查询失败：" + ex.Message;
            SetAdxEmpty("查询失败", ex.Message, false);
        }
        finally { EndAdxOperation(); }
    }

    private void AddAdxCandidate(AdxCandidate candidate)
    {
        var select = new CheckBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Thickness(0, 5, 5, 0) };
        var redownload = new CheckBox { Content = "重新下载", FontSize = 10,
            IsVisible = candidate.Downloaded, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        select.IsCheckedChanged += (_, _) => UpdateAdxSelectionSummary();
        redownload.IsCheckedChanged += (_, _) =>
        {
            if (redownload.IsChecked == true) select.IsChecked = true;
        };

        var image = new Image { Height = 184, Stretch = Stretch.UniformToFill };
        var imageHost = new Grid { Height = 184, ClipToBounds = true, Background = Brush.Parse("#E9EEF5") };
        imageHost.Children.Add(image);
        imageHost.Children.Add(new Border
        {
            Background = Brush.Parse("#B2141B2A"), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3), Margin = new Thickness(6), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Child = new TextBlock { Text = $"TOP {candidate.Rank}", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeight.Bold },
        });
        imageHost.Children.Add(select);

        var details = new StackPanel { Spacing = 4, Margin = new Thickness(8, 7) };
        details.Children.Add(new TextBlock { Text = $"ID: {candidate.MaterialId}", FontSize = 11, FontWeight = FontWeight.SemiBold });
        details.Children.Add(new TextBlock
        {
            Text = $"曝光 {FormatMetric(candidate.Exposure)}   播放 {FormatMetric(candidate.PlayCount)}   点赞 {FormatMetric(candidate.LikeCount)}",
            FontSize = 10, Foreground = Brush.Parse("#98A2B3"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var state = new Grid { ColumnDefinitions = new("*,Auto") };
        state.Children.Add(new TextBlock
        {
            Text = candidate.Downloaded ? "已下载" : "未下载", FontSize = 10,
            Foreground = Brush.Parse(candidate.Downloaded ? "#087A42" : "#667085"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        Grid.SetColumn(redownload, 1);
        state.Children.Add(redownload);
        details.Children.Add(state);

        var card = new Border
        {
            Width = 179, Height = 276, Margin = new Thickness(4), Background = Brushes.White,
            BorderBrush = Brush.Parse("#DDE4ED"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
            ClipToBounds = true, Child = new StackPanel { Children = { imageHost, details } },
        };
        _adxCandidates.Add(new(candidate, select, redownload));
        AdxCandidatePanel.Children.Add(card);
        if (!string.IsNullOrWhiteSpace(candidate.CoverUrl)) _ = LoadAdxCoverAsync(image, candidate.CoverUrl!);
    }

    private async Task LoadAdxCoverAsync(Image image, string source)
    {
        try
        {
            var baseUri = new Uri(_adxService?.LoadSettings().BaseUrl ?? "https://localhost/");
            var uri = Uri.TryCreate(source, UriKind.Absolute, out var absolute) ? absolute : new Uri(baseUri, source);
            await using var stream = await AdxImageClient.GetStreamAsync(uri);
            var bitmap = new Bitmap(stream);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => image.Source = bitmap);
        }
        catch { }
    }

    private async void OnAdxDownloadClick(object? sender, RoutedEventArgs e) => await DownloadAdxAsync(false);
    private async void OnAdxDownloadPublishClick(object? sender, RoutedEventArgs e) => await DownloadAdxAsync(true);

    private async Task DownloadAdxAsync(bool autoPublish)
    {
        if (_adxService is null || _adxBatchStore is null || _activeAdxRow is null || _activeAdxAccount is null) return;
        var selected = _adxCandidates.Where(item => item.Select.IsChecked == true).ToArray();
        if (selected.Length == 0) { AdxDrawerStatus.Text = "请先选择要下载的 ADX 素材。"; return; }
        if (!BeginAdxOperation()) return;
        try
        {
            AdxDownloadProgress.IsVisible = true;
            var result = await _adxService.DownloadAsync(new AdxDownloadRequest(_activeAdxAccount.Id,
                _activeAdxRow.NewTitle, _activeAdxRow.OriginalTitle, _activeAdxRow.WorkflowDirectory,
                selected.Select(item => item.Candidate.MaterialId).ToArray(), RedownloadMaterialIds:
                selected.Where(item => item.Redownload.IsChecked == true).Select(item => item.Candidate.MaterialId).ToArray()),
                AdxProgress(), _adxCancellation!.Token);
            AdxDrawerStatus.Text = result.Message;
            if (!autoPublish) return;
            var manifestPath = Path.Combine(result.DownloadDirectory, AdxBatchStore.ManifestFileName);
            var manifest = _adxBatchStore.Read(manifestPath) ?? throw new InvalidOperationException("下载完成但无法读取 ADX 批次清单。");
            var payload = new AdxPublishPayload
            {
                OriginalTitle = _activeAdxRow.OriginalTitle,
                NewTitle = _activeAdxRow.NewTitle,
                PublishOptionsJson = new WeixinPublishOptions
                {
                    EpisodeSelectionMode = "all", FinalAction = AdxFinalAction.SelectedIndex == 1 ? "publish" : "draft",
                    ReplaceCoverWithLocalImage = true,
                }.ToJson(),
                Items = manifest.Items.Select(item => new AdxPublishItem(item.MaterialId, item.VideoPath,
                    item.CoverPath, item.Description, item.ShortTitle, manifestPath)).ToList(),
            };
            await QueueUnifiedAdxAsync(payload, _activeAdxRow.WorkflowDirectory, _activeAdxAccount.Id,
                _activeAdxAccount.Name, _activeAdxAccount.ProfileDir, true);
            AdxDrawer.IsVisible = false;
        }
        catch (OperationCanceledException) { AdxDrawerStatus.Text = "ADX 下载已取消。"; }
        catch (Exception ex) { AdxDrawerStatus.Text = "ADX 下载失败：" + ex.Message; }
        finally { EndAdxOperation(); }
    }

    private IProgress<AdxProgress> AdxProgress() => new Progress<AdxProgress>(value =>
    {
        AdxDrawerStatus.Text = value.Message;
        if (value.Total > 0)
        {
            AdxDownloadProgress.Maximum = value.Total;
            AdxDownloadProgress.Value = value.Current;
        }
    });

    private bool BeginAdxOperation()
    {
        if (_adxBusy) return false;
        _adxBusy = true;
        _adxCancellation?.Dispose();
        _adxCancellation = new CancellationTokenSource();
        AdxDownloadButton.IsEnabled = false;
        AdxDownloadPublishButton.IsEnabled = false;
        return true;
    }

    private void EndAdxOperation()
    {
        _adxBusy = false;
        AdxDownloadButton.IsEnabled = true;
        AdxDownloadPublishButton.IsEnabled = true;
        AdxDownloadProgress.IsVisible = false;
    }

    private void OnCloseAdxDrawerClick(object? sender, RoutedEventArgs e) => CloseAdxDrawer();
    private void OnAdxScrimPressed(object? sender, PointerPressedEventArgs e) => CloseAdxDrawer();
    private void CloseAdxDrawer() { _adxCancellation?.Cancel(); AdxDrawer.IsVisible = false; }
    private void OnOpenAdxSettingsClick(object? sender, RoutedEventArgs e) { CloseAdxDrawer(); _showSettings?.Invoke(); }
    private void OnSelectAllUndownloadedClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _adxCandidates) item.Select.IsChecked = !item.Candidate.Downloaded;
    }
    private void OnSelectDefaultTopClick(object? sender, RoutedEventArgs e) => SelectAdxTop(_adxService?.LoadSettings().DefaultTopCount ?? 5);
    private void OnSelectTopClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && int.TryParse(value, out var count)) SelectAdxTop(count);
    }
    private void OnClearAdxSelectionClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _adxCandidates) item.Select.IsChecked = false;
    }
    private void SelectAdxTop(int count)
    {
        foreach (var item in _adxCandidates)
            item.Select.IsChecked = item.Candidate.Rank <= count && !item.Candidate.Downloaded;
    }
    private void UpdateAdxSelectionSummary() => AdxSelectionText.Text = $"已选 {_adxCandidates.Count(item => item.Select.IsChecked == true)} 条";
    private void SetAdxEmpty(string title, string description, bool loading, bool visible = true)
    {
        AdxEmptyPanel.IsVisible = visible;
        AdxEmptyTitle.Text = title;
        AdxEmptyDescription.Text = description;
        AdxQueryProgress.IsVisible = loading;
    }
    private static string FormatMetric(long value) => value >= 10_000 ? $"{value / 10_000d:0.#}万" : value.ToString("N0");
    private sealed record AdxCandidateSelection(AdxCandidate Candidate, CheckBox Select, CheckBox Redownload);

    private async void OnCustomProjectClick(object? sender, RoutedEventArgs e)
    {
        var row = TryGetRow(sender);
        if (row is null || OwnerWindow?.StorageProvider is null) return;
        var files = await OwnerWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"为《{row.NewTitle}》选择发布视频",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("视频文件") { Patterns = ["*.mp4", "*.mov", "*.m4v", "*.mkv", "*.avi", "*.flv", "*.ts", "*.wmv", "*.webm"] }],
        });
        if (files.Count == 0) return;
        await CreateDraftAsync(MaterialSourceKind.CustomFiles, "自选发布", row.WorkflowDirectory,
            row.OriginalTitle, row.NewTitle, files.Select(file => file.Path.LocalPath));
    }

    private async void OnPublishAdxProjectClick(object? sender, RoutedEventArgs e)
    {
        var row = TryGetRow(sender);
        if (row is null) return;
        await CreateDraftAsync(MaterialSourceKind.AdxBatch, "ADX素材", row.WorkflowDirectory,
            row.OriginalTitle, row.NewTitle, []);
    }

    private async void OnPublishDownloadedProjectClick(object? sender, RoutedEventArgs e)
    {
        var row = TryGetRow(sender);
        if (row is null) return;
        var files = FindDownloadedVideos(row.WorkflowDirectory);
        await CreateDraftAsync(MaterialSourceKind.DownloadedWork, "已下载视频", row.WorkflowDirectory,
            row.OriginalTitle, row.NewTitle, files);
    }

    private async void OnSystemHighlightClick(object? sender, RoutedEventArgs e)
    {
        var row = ViewModel?.Projects.FirstOrDefault(item => item.IsSelected) ?? ViewModel?.Projects.FirstOrDefault();
        if (row is null)
        {
            SetStatus("请先扫描并选择要发布系统高光的项目。");
            return;
        }
        await CreateDraftAsync(MaterialSourceKind.SystemHighlight, "系统高光", row.WorkflowDirectory,
            row.OriginalTitle, row.NewTitle, [], "{\"count\":10,\"videoTypes\":\"混剪,解说,切片\"}");
    }

    private async void OnDownloadSystemHighlightClick(object? sender, RoutedEventArgs e) =>
        await RunDownloadAsync(systemHighlights: true);

    private async Task RunDownloadAsync(bool systemHighlights)
    {
        var account = _accountProvider?.Invoke();
        if (OwnerWindow is null || ViewModel is null || account is null || _materialDownloadService is null)
        {
            SetStatus("请先选择视频号工作账号。");
            return;
        }
        if (!Directory.Exists(ViewModel.WorkspaceRoot))
        {
            SetStatus("请先选择有效的素材工作目录。");
            return;
        }
        var initial = systemHighlights
            ? string.Join(Environment.NewLine, ViewModel.Projects.Where(item => item.IsSelected).Select(item => item.OriginalTitle))
            : string.Empty;
        var dialog = new MaterialDownloadDialog(systemHighlights, initial);
        if (!await dialog.ShowDialog<bool>(OwnerWindow)) return;
        try
        {
            ViewModel.IsBusy = true;
            var request = new MaterialDownloadRequest(account.Id, ViewModel.WorkspaceRoot, dialog.Values,
                dialog.Limit, account.WeixinAuthStatePath);
            var progress = new Progress<string>(SetStatus);
            var result = systemHighlights
                ? await _materialDownloadService.DownloadSystemHighlightsAsync(request, progress, CancellationToken.None)
                : await _materialDownloadService.DownloadByQueriesAsync(request, progress, CancellationToken.None);
            SetStatus($"{(systemHighlights ? "系统高光" : "素材视频")}下载完成：新下载 {result.DownloadedCount} 条。");
            await ViewModel.ScanAsync();
        }
        catch (Exception ex)
        {
            SetStatus("素材下载失败：" + ex.Message);
        }
        finally
        {
            ViewModel.IsBusy = false;
        }
    }

    private async void OnHighlightScheduleClick(object? sender, RoutedEventArgs e)
    {
        var account = _accountProvider?.Invoke();
        if (OwnerWindow is null || ViewModel is null || account is null || _highlightScheduleService is null)
        {
            SetStatus("请先选择视频号工作账号。");
            return;
        }
        var rules = _highlightScheduleService.LoadRules()
            .Where(item => string.Equals(item.AccountId, account.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        var dialog = new HighlightScheduleDialog(rules, account.Id, ViewModel.WorkspaceRoot);
        if (!await dialog.ShowDialog<bool>(OwnerWindow)) return;
        var otherRules = _highlightScheduleService.LoadRules()
            .Where(item => !string.Equals(item.AccountId, account.Id, StringComparison.OrdinalIgnoreCase));
        _highlightScheduleService.SaveRules(otherRules.Concat(dialog.Rules));
        SetStatus($"已保存 {dialog.Rules.Count} 条系统高光自动发布规则。");
        if (string.IsNullOrWhiteSpace(dialog.RunNowRuleId)) return;
        try
        {
            var progress = new Progress<string>(SetStatus);
            await _highlightScheduleService.RunNowAsync(dialog.RunNowRuleId, progress, CancellationToken.None);
        }
        catch (Exception ex) { SetStatus("系统高光自动发布失败：" + ex.Message); }
    }

    private async void OnDeleteChannelMaterialsClick(object? sender, RoutedEventArgs e)
    {
        var row = ViewModel?.Projects.FirstOrDefault(item => item.IsSelected) ?? ViewModel?.Projects.FirstOrDefault();
        if (row is null || OwnerWindow is null || _channelVideoDeleteService is null)
        {
            SetStatus("请先扫描并选择要清理平台素材的项目。");
            return;
        }
        var dialog = new MaterialChannelDeleteDialog(row.NewTitle);
        if (!await dialog.ShowDialog<bool>(OwnerWindow)) return;
        try
        {
            SetStatus($"正在删除视频号素材：{dialog.Keyword}");
            var progress = new Progress<string>(SetStatus);
            var result = await _channelVideoDeleteService.DeleteAsync(row.WorkflowDirectory,
                _shellViewModel?.DraftConfigPath, dialog.Keyword, dialog.DeleteCount, progress, CancellationToken.None);
            SetStatus(result.Deleted
                ? $"已删除 {result.DeletedCount} 条视频号素材：{string.Join("、", result.DeletedTitles)}"
                : "未删除任何视频号素材。");
        }
        catch (Exception ex)
        {
            SetStatus("删除视频号素材失败：" + ex.Message);
        }
    }

    private void OnOpenSourceProjectClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetRow(sender) is { } row) OpenPath(row.SourceDirectory);
    }

    private void OnOpenWorkflowProjectClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetRow(sender) is { } row) OpenPath(row.WorkflowDirectory);
    }

    private async Task CreateDraftAsync(MaterialSourceKind kind, string label, string workflowDirectory,
        string originalTitle, string newTitle, IEnumerable<string> files, string payloadJson = "{}")
    {
        if (_unifiedPublishViewModel is null) return;
        try
        {
            var source = new MaterialSourceSpec
            {
                Kind = kind,
                Label = label,
                WorkflowDirectory = workflowDirectory,
                OriginalTitle = originalTitle,
                NewTitle = newTitle,
                Files = files.ToList(),
                PayloadJson = payloadJson,
            };
            await _unifiedPublishViewModel.CreateAndAcceptAsync(source, BuildForm(originalTitle, newTitle), new MediaProcessingProfile());
            SetStatus($"已生成一键发布草稿：{newTitle}，来源：{label}。");
            _showUnifiedPublish?.Invoke();
        }
        catch (Exception ex)
        {
            SetStatus("生成发布草稿失败：" + ex.Message);
        }
    }

    private async Task QueueUnifiedAdxAsync(AdxPublishPayload payload, string workflowDirectory, string accountId,
        string accountName, string accountSessionDirectory, bool autoStart)
    {
        if (_unifiedPublishViewModel is null || _adxBatchStore is null) return;
        var items = payload.Items.Select((item, index) =>
        {
            var manifest = _adxBatchStore.Read(item.ManifestPath);
            return new ResolvedMaterial
            {
                Id = item.MaterialId,
                Sequence = index + 1,
                VideoPath = item.VideoPath,
                CoverPath = item.CoverPath,
                Description = item.Description,
                ShortTitle = item.ShortTitle,
                Origin = new MaterialOrigin(MaterialSourceKind.AdxBatch, item.MaterialId,
                    manifest?.BatchId ?? string.Empty, item.ManifestPath),
            };
        }).ToList();
        var options = WeixinPublishOptions.FromJob(new PlatformPublisher.Common.Models.PublishJob
        {
            PlatformOptionsJson = payload.PublishOptionsJson,
        });
        var draft = new PublishDraft
        {
            Source = new MaterialSourceSpec
            {
                Kind = MaterialSourceKind.AdxBatch,
                Label = "ADX素材",
                WorkflowDirectory = workflowDirectory,
                OriginalTitle = payload.OriginalTitle,
                NewTitle = payload.NewTitle,
                Files = items.Select(item => item.VideoPath).ToList(),
            },
            Items = items,
            Form = BuildForm(payload.OriginalTitle, payload.NewTitle, options),
        };
        await _unifiedPublishViewModel.AcceptDraftAsync(draft, autoStart);
        _showUnifiedPublish?.Invoke();
    }

    private UnifiedPublishForm BuildForm(string originalTitle, string newTitle, WeixinPublishOptions? options = null)
    {
        options ??= WeixinPublishOptions.FromJob(new PlatformPublisher.Common.Models.PublishJob
        {
            PlatformOptionsJson = _shellViewModel?.DraftPlatformOptionsJson ?? string.Empty,
            PublishDescription = _shellViewModel?.DraftPublishDescription ?? string.Empty,
            DeclareOriginal = _shellViewModel?.DraftDeclareOriginal ?? true,
            HideLocation = _shellViewModel?.DraftHideLocation ?? true,
        });
        return new UnifiedPublishForm
        {
            OriginalTitle = originalTitle,
            NewTitle = newTitle,
            SeriesName = newTitle,
            DescriptionTemplate = options.DescriptionTemplate,
            FillDescription = options.FillDescription,
            DeclareOriginal = options.DeclareOriginal,
            FillShortTitle = options.FillShortTitle,
            ShortTitleMaxLength = options.ShortTitleMaxLength,
            LinkSeries = !string.IsNullOrWhiteSpace(options.LinkOptionText),
            LinkSeriesName = newTitle,
            LocationOption = options.LocationOptionText,
            FinalAction = options.FinalAction == "publish" ? UnifiedFinalAction.Publish : UnifiedFinalAction.Draft,
            StopOnError = options.PauseOnError,
        };
    }

    private static IReadOnlyList<string> FindDownloadedVideos(string workflowDirectory)
    {
        var candidateRoots = new[]
        {
            Path.Combine(workflowDirectory, "materials"),
            Path.Combine(workflowDirectory, "material-videos"),
            Path.Combine(workflowDirectory, "downloads"),
            Path.Combine(workflowDirectory, "downloaded"),
        };
        return candidateRoots.Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static MaterialProjectRowViewModel? TryGetRow(object? sender) =>
        sender is Button { Tag: MaterialProjectRowViewModel row } ? row : null;

    private void SetStatus(string message)
    {
        if (ViewModel is not null) ViewModel.StatusMessage = message;
        if (_shellViewModel is not null) _shellViewModel.StatusMessage = message;
    }

    private static void OpenPath(string path)
    {
        if (!Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
}
