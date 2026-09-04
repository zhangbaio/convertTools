using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ChannelsPublisher.Core.Models;
using PlatformPublisher.Adx.Automation;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Desktop.ViewModels;
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
    private WeixinMaterialChannelVideoDeleteService? _channelVideoDeleteService;
    private UnifiedPublishViewModel? _unifiedPublishViewModel;
    private Action? _showUnifiedPublish;

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
        WeixinMaterialChannelVideoDeleteService channelVideoDeleteService,
        UnifiedPublishViewModel unifiedPublishViewModel,
        Action showUnifiedPublish)
    {
        _accountsProvider = accountsProvider;
        _accountProvider = accountProvider;
        _selectAccount = selectAccount;
        _shellViewModel = shellViewModel;
        _adxService = adxService;
        _adxBatchStore = adxBatchStore;
        _directoryPublishService = directoryPublishService;
        _channelVideoDeleteService = channelVideoDeleteService;
        _unifiedPublishViewModel = unifiedPublishViewModel;
        _showUnifiedPublish = showUnifiedPublish;

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
        var row = ViewModel?.Projects.FirstOrDefault(item => item.IsSelected) ?? ViewModel?.Projects.FirstOrDefault();
        if (row is null)
        {
            SetStatus("请先扫描并选择一个项目，再按项目原剧名发现素材。");
            return;
        }
        await OpenAdxAsync(row);
    }

    private async Task OpenAdxAsync(MaterialProjectRowViewModel row)
    {
        var account = _accountProvider?.Invoke();
        if (OwnerWindow is null || account is null || _adxService is null || _adxBatchStore is null)
        {
            SetStatus("请先选择视频号账号。");
            return;
        }
        var dialog = new AdxMaterialsDialog(_adxService, _adxBatchStore, account.Id, account.Name,
            account.ProfileDir, row.WorkflowDirectory, row.OriginalTitle, row.NewTitle, QueueUnifiedAdxAsync);
        await dialog.ShowDialog(OwnerWindow);
    }

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

    private void OnDownloadSystemHighlightClick(object? sender, RoutedEventArgs e) =>
        SetStatus("系统高光下载面板将在第 3 阶段接入；当前可使用“发布系统高光视频”。");

    private void OnHighlightScheduleClick(object? sender, RoutedEventArgs e) =>
        SetStatus("系统高光自动发布配置将在第 4 阶段接入。");

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
