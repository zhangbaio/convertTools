using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ChannelsPublisher.Core.Models;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Kuaishou.Publishing;
using PlatformPublisher.Weixin.Publishing;
using TikTokPublisher.Ui.Views;
using PublishJobKind = PlatformPublisher.Common.Models.PublishJobKind;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinWorkflowView : UserControl
{
    private static readonly double[] KuaishouColumnMinWidths =
    {
        36, 40, 88, 88, 44, 84, 60, 72, 72, 72, 80, 64, 72,
    };
    private readonly double[] _kuaishouColumnWidths =
    {
        44, 48, 168, 168, 56, 100, 72, 84, 84, 84, 92, 76, 84,
    };
    private int _kuaishouResizeColumn = -1;
    private double _kuaishouResizeStartX;
    private double _kuaishouResizeStartWidth;
    private bool _embeddedUploadLayout;
    private bool _kuaishouColumnsAdjusted;
    private Func<PublishAccount?>? _accountProvider;
    private bool _isKuaishouMode;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ViewLogsRequested;
    public event EventHandler? ArchiveRequested;
    public event EventHandler? KuaishouConfigRequested;
    public event EventHandler<KuaishouAdxRequestedEventArgs>? KuaishouAdxRequested;

    public bool IsKuaishouMode
    {
        get => _isKuaishouMode;
        set
        {
            _isKuaishouMode = value;
            ApplyMode();
        }
    }

    public WeixinWorkflowView()
    {
        InitializeComponent();
        QueueTableGrid.SetColumnWidths(KuaishouTableRoot, _kuaishouColumnWidths);
        AttachedToVisualTree += (_, _) => ViewModel?.ActivateSeriesWorkflow();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && IsVisible)
                ViewModel?.ActivateSeriesWorkflow();
        };
    }

    private void OnToggleWeixinStepsClick(object? sender, RoutedEventArgs e)
    {
        var expanded = !WeixinStepsBody.IsVisible;
        WeixinStepsBody.IsVisible = expanded;
        WeixinStepsIndicator.RenderTransform = expanded ? new RotateTransform(180) : null;
        AutomationProperties.SetName(WeixinStepsToggle, expanded ? "收起执行步骤" : "展开执行步骤");
    }

    internal void UseEmbeddedUploadLayout()
    {
        _embeddedUploadLayout = true;
        Classes.Add("embeddedUploadQueue");
        WorkflowLayout.Classes.Add("pageRoot");
        WorkflowLayout.RowSpacing = 0;
        KuaishouChrome.Classes.Remove("panel");
        KuaishouChrome.Classes.AddRange(["controlsFrame", "uploadQueueToolbar"]);
        KuaishouChrome.Margin = new Thickness(0, 0, 0, 8);
        KuaishouChrome.ClearValue(Border.PaddingProperty);
        KuaishouQueueHeading.IsVisible = false;
        WorkflowTableFrame.Classes.Remove("panel");
        WorkflowTableFrame.Classes.Add("queueTableFrame");
        WorkflowStatus.IsVisible = false;
        WorkflowQueueScroll.AllowAutoHide = false;
    }

    internal Control DetachWorkDirectoryEditor()
    {
        KuaishouWorkDirectoryActions.Children.Remove(KuaishouResetJobButton);
        KuaishouQueueControls.Children.Remove(KuaishouWorkDirectoryEditor);
        KuaishouWorkDirectoryEditor.ClearValue(MarginProperty);
        KuaishouWorkDirectoryEditor.Bind(DataContextProperty, this.GetObservable(DataContextProperty));
        return KuaishouWorkDirectoryEditor;
    }

    private void OnWorkflowQueueViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_embeddedUploadLayout)
            ApplyKuaishouColumnWidths(e.NewSize.Width);
    }

    private void ApplyKuaishouColumnWidths(double viewportWidth)
    {
        var widths = _kuaishouColumnWidths.ToArray();
        if (_embeddedUploadLayout)
        {
            KuaishouTableRoot.MinWidth = viewportWidth;
            if (!_kuaishouColumnsAdjusted)
            {
                var titleExtra = Math.Max(0, viewportWidth - widths.Sum()) / 2;
                widths[2] += titleExtra;
                widths[3] += titleExtra;
            }
        }
        QueueTableGrid.SetColumnWidths(KuaishouTableRoot, widths);
    }

    private void OnKuaishouColumnResizerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control ||
            !int.TryParse(control.Tag?.ToString(), out var column) ||
            column < 0 ||
            column >= _kuaishouColumnWidths.Length)
            return;
        _kuaishouResizeColumn = column;
        _kuaishouResizeStartX = e.GetPosition(KuaishouQueueHeaderGrid).X;
        _kuaishouResizeStartWidth = QueueTableGrid.GetColumnWidths(KuaishouTableRoot)?[column] ??
            _kuaishouColumnWidths[column];
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void OnKuaishouColumnResizerMoved(object? sender, PointerEventArgs e)
    {
        if (_kuaishouResizeColumn < 0)
            return;
        var width = Math.Max(
            KuaishouColumnMinWidths[_kuaishouResizeColumn],
            _kuaishouResizeStartWidth + e.GetPosition(KuaishouQueueHeaderGrid).X - _kuaishouResizeStartX);
        var visibleWidths = QueueTableGrid.GetColumnWidths(KuaishouTableRoot) ?? _kuaishouColumnWidths;
        if (Math.Abs(width - visibleWidths[_kuaishouResizeColumn]) < 0.5)
            return;
        if (!_kuaishouColumnsAdjusted)
        {
            visibleWidths.CopyTo(_kuaishouColumnWidths, 0);
            _kuaishouColumnsAdjusted = true;
        }
        _kuaishouColumnWidths[_kuaishouResizeColumn] = width;
        ApplyKuaishouColumnWidths(WorkflowQueueScroll.Bounds.Width);
        e.Handled = true;
    }

    private void OnKuaishouColumnResizerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_kuaishouResizeColumn < 0)
            return;
        e.Pointer.Capture(null);
        _kuaishouResizeColumn = -1;
        e.Handled = true;
    }

    private void ApplyMode()
    {
        if (KuaishouChrome is null || WeixinChrome is null || WeixinTableRoot is null ||
            WorkflowQueueScroll is null || WeixinFooter is null || WorkflowStatus is null) return;
        KuaishouChrome.IsVisible = _isKuaishouMode;
        WeixinChrome.IsVisible = !_isKuaishouMode;
        WorkflowQueueScroll.IsVisible = _isKuaishouMode;
        KuaishouQueueHeader.IsVisible = KuaishouQueueList.IsVisible = _isKuaishouMode;
        WeixinTableRoot.IsVisible = WeixinFooter.IsVisible = !_isKuaishouMode;
        WorkflowStatus.IsVisible = _isKuaishouMode && !_embeddedUploadLayout;
        Classes.Set("kuaishouQueue", _isKuaishouMode);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnQueueCheckClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: PublishJobRowViewModel row } checkBox || ViewModel is not { } viewModel)
            return;
        row.IsChecked = checkBox.IsChecked == true;
        await viewModel.SaveQueueSelectionAsync(row);
    }

    private void OnOpenSourceDirectoryClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenRowDirectory(sender, workflow: false);
    }

    private void OnOpenWorkflowDirectoryClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenRowDirectory(sender, workflow: true);
    }

    private void OpenRowDirectory(object? sender, bool workflow)
    {
        if ((sender as Control)?.DataContext is not PublishJobRowViewModel row || ViewModel is null)
            return;
        ViewModel.StatusMessage = workflow ? row.OpenWorkflowDirectory() : row.OpenSourceDirectory();
    }

    public void Bind(Func<PublishAccount?> accountProvider)
    {
        _accountProvider = accountProvider;
        ApplyAccount(accountProvider());
    }

    public void ApplyAccount(PublishAccount? account)
    {
        ViewModel?.UseWeixinAccount(account?.Id, account?.Name, account?.ProfileDir);
    }

    internal void BindWorkspaceAccountActions(Action openAccountSettings)
    {
        OpenLoginButton.Command = null;
        OpenLoginButton.Click += (_, _) => openAccountSettings();
    }

    private async void OnPickProjectDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _isKuaishouMode ? "选择快手分账工作目录" : "选择视频号项目或素材目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
            ViewModel.SetActiveAccountWorkRootDirectory(folders[0].Path.LocalPath);
    }

    private async void OnImportLocalProjectsClick(object? sender, RoutedEventArgs e)
    {
        if (_isKuaishouMode)
        {
            await ImportKuaishouLocalDramasAsync();
            return;
        }

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要导入的视频号短剧项目",
            AllowMultiple = true,
        });
        if (folders.Count > 0)
            await ViewModel.ImportLocalProjectDirectoriesAsync(folders.Select(folder => folder.Path.LocalPath));
    }

    private async Task ImportKuaishouLocalDramasAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null || ViewModel is null)
            return;
        if (!Directory.Exists(ViewModel.DraftProjectDirectory))
        {
            ViewModel.StatusMessage = "请先选择快手分账工作目录。";
            return;
        }

        ViewModel.StatusMessage = "正在扫描本地短剧目录…";
        var candidates = await Task.Run(ViewModel.ListKuaishouLocalImportCandidates);
        var selected = await ShowKuaishouLocalImportDialogAsync(owner, candidates, ViewModel.DraftProjectDirectory);
        if (selected is null)
            return;
        if (selected.Count == 0)
        {
            ViewModel.StatusMessage = "请至少选择一个短剧目录。";
            return;
        }

        await ViewModel.ImportKuaishouLocalDramasAsync(selected);
    }

    private async Task<IReadOnlyList<string>?> ShowKuaishouLocalImportDialogAsync(
        Window owner,
        IReadOnlyList<KuaishouLocalDramaCandidate> candidates,
        string workspace)
    {
        var checks = new List<CheckBox>();
        var list = new StackPanel { Spacing = 4 };
        foreach (var candidate in candidates)
        {
            var parts = new List<string> { candidate.DisplayName, $"{candidate.EpisodeCount} 集", candidate.HasPoster ? "有海报" : "缺海报" };
            if (candidate.MetadataExists)
                parts.Add("已导入");
            var check = new CheckBox
            {
                Content = string.Join(" | ", parts),
                IsChecked = !candidate.MetadataExists,
                Tag = candidate.ProjectDirectory,
            };
            checks.Add(check);
            list.Children.Add(check);
        }

        var dialog = new Window
        {
            Title = "导入本地短剧",
            Width = 680,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var root = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = "选择已手动下载好的短剧目录。导入后会自动创建项目元数据并刷新项目列表；本地导入项目会自动跳过下载步骤。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        root.Children.Add(new ScrollViewer { Height = 340, Content = list });
        var browse = new Button { Content = "浏览目录…", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        browse.Click += async (_, _) =>
        {
            var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择短剧目录",
                AllowMultiple = false,
                SuggestedStartLocation = await dialog.StorageProvider.TryGetFolderFromPathAsync(workspace),
            });
            if (folders.Count == 0)
                return;
            var path = folders[0].Path.LocalPath;
            if (checks.Any(item => string.Equals(item.Tag as string, path, StringComparison.OrdinalIgnoreCase)))
                return;
            var check = new CheckBox { Content = $"{Path.GetFileName(path)} | (手动添加)", IsChecked = true, Tag = path };
            checks.Add(check);
            list.Children.Add(check);
        };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消" };
        var ok = new Button { Content = "导入选中" };
        IReadOnlyList<string>? result = null;
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) =>
        {
            result = checks.Where(item => item.IsChecked == true && item.Tag is string).Select(item => (string)item.Tag!).ToArray();
            dialog.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(browse);
        root.Children.Add(buttons);
        dialog.Content = root;
        await dialog.ShowDialog(owner);
        return result;
    }

    private async void OnUploadDramasClick(object? sender,RoutedEventArgs e)
    {
        var owner=TopLevel.GetTopLevel(this) as Window;if(owner is null||ViewModel is null)return;
        if(string.IsNullOrWhiteSpace(ViewModel.DraftProjectDirectory)||!Directory.Exists(ViewModel.DraftProjectDirectory))
        {
            ViewModel.StatusMessage="请先选择有效的工作目录。";
            var folders=await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title="上传短剧前请选择工作目录",
                AllowMultiple=false,
            });
            if(folders.Count==0)return;
            if(!ViewModel.SetActiveAccountWorkRootDirectory(folders[0].Path.LocalPath))return;
        }
        var sourceStatus=ViewModel.GetDramaSourceConfigurationStatus();
        if(!sourceStatus.IsConfigured)
        {
            var openSettings=await ConfirmDialog.ShowAsync(owner,"下载数据链路未配置",sourceStatus.Message+Environment.NewLine+Environment.NewLine+"是否立即打开系统设置？");
            if(openSettings)SettingsRequested?.Invoke(this,EventArgs.Empty);
            return;
        }
        var titles=await UploadDramaTitlesDialog.ShowAsync(owner);if(string.IsNullOrWhiteSpace(titles))return;
        UploadDramasButton.IsEnabled=false;
        KuaishouUploadDramasButton.IsEnabled=false;
        UploadDramasButton.Content="正在处理…";
        KuaishouUploadDramasButton.Content="正在处理…";
        try
        {
            ViewModel.StatusMessage="已接收剧名，正在搜索并创建项目…";
            var message=await ViewModel.ImportDramaTitlesAsync(titles);
            await InfoDialog.ShowAsync(owner,message,
                message.StartsWith("上传短剧完成",StringComparison.Ordinal)?"上传短剧完成":"上传短剧未完成",520,220);
        }
        finally
        {
            UploadDramasButton.Content="按剧名入队";
            KuaishouUploadDramasButton.Content="上传短剧";
            UploadDramasButton.IsEnabled=true;
            KuaishouUploadDramasButton.IsEnabled=true;
        }
    }

    private async void OnUploadWeixinTitlesClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || ViewModel is not { } viewModel)
            return;

        try
        {
            var target = viewModel.CaptureWeixinTitleUploadTarget();
            var request = await UploadDramaTitlesDialog.ShowWeixinUploadAsync(owner, target);
            if (request is null) return;
            var summary = await viewModel.UploadWeixinTitlesAsync(target, request.Titles, request.MatchMode);
            await InfoDialog.ShowAsync(owner, summary, "视频号上传短剧结果", 620, 325);
        }
        catch (Exception ex)
        {
            await InfoDialog.ShowAsync(owner, ex.Message, "无法开始上传短剧", 520, 220);
        }
    }

    private void OnKuaishouConfigClick(object? sender, RoutedEventArgs e) =>
        KuaishouConfigRequested?.Invoke(this, EventArgs.Empty);

    private void OnViewWeixinLogsClick(object? sender, RoutedEventArgs e) =>
        ViewLogsRequested?.Invoke(this, EventArgs.Empty);

    private void OnViewKuaishouLogsClick(object? sender, RoutedEventArgs e) =>
        ViewLogsRequested?.Invoke(this, EventArgs.Empty);

    private async void OnKuaishouDraftActionClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || ViewModel is not { } viewModel || sender is not Button button)
            return;
        var submit = button.Tag as string == "submit";
        KuaishouCheckDraftButton.IsEnabled = false;
        KuaishouSubmitDraftButton.IsEnabled = false;
        try
        {
            var message = await viewModel.CheckKuaishouDraftAsync(submit
                ? inspection => ConfirmDialog.ShowAsync(owner, "仅提交审核", inspection.Describe() + "\n\n确认提交原草稿？",
                    560, 300, "确认提交")
                : null);
            await InfoDialog.ShowAsync(owner, message, submit ? "仅提交审核" : "检查草稿", 620, 370);
        }
        finally
        {
            KuaishouCheckDraftButton.IsEnabled = true;
            KuaishouSubmitDraftButton.IsEnabled = true;
        }
    }

    private async void OnResubmitKuaishouCommitmentClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null || ViewModel is null)
            return;
        KuaishouResubmitButton.IsEnabled = false;
        KuaishouResubmitButton.Content = "正在提交…";
        try
        {
            var message = await ViewModel.ResubmitKuaishouCommitmentAsync();
            await InfoDialog.ShowAsync(owner, message, "重新提交", 560, 320);
        }
        finally
        {
            KuaishouResubmitButton.Content = "重新提交";
            KuaishouResubmitButton.IsEnabled = true;
        }
    }

    private async void OnResubmitKuaishouAuditTimeoutClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || ViewModel is not { } viewModel)
            return;

        KuaishouAuditTimeoutButton.IsEnabled = false;
        try
        {
            var message = await viewModel.ResubmitKuaishouAuditTimeoutAsync(owner);
            if (message is not null)
                await InfoDialog.ShowAsync(owner, message, "审核超时重提", 640, 340);
        }
        finally
        {
            KuaishouAuditTimeoutButton.IsEnabled = true;
        }
    }

    private async void OnReplaceKuaishouDraftClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null || ViewModel is null)
            return;
        var seriesId = await KuaishouDraftReplaceDialog.ShowAsync(owner);
        if (string.IsNullOrWhiteSpace(seriesId))
            return;
        KuaishouReplaceDraftButton.IsEnabled = false;
        KuaishouReplaceDraftButton.Content = "正在替换…";
        try
        {
            var message = await ViewModel.ReplaceKuaishouDraftAsync(seriesId);
            await InfoDialog.ShowAsync(owner, message, "新剧替换草稿", 560, 280);
        }
        finally
        {
            KuaishouReplaceDraftButton.Content = "新剧替换草稿";
            KuaishouReplaceDraftButton.IsEnabled = true;
        }
    }

    private async void OnRepairKuaishouVideosClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null || ViewModel is null)
            return;
        var titles = await KuaishouVideoRepairDialog.ShowAsync(owner);
        if (string.IsNullOrWhiteSpace(titles))
            return;
        KuaishouRepairVideosButton.IsEnabled = false;
        KuaishouRepairVideosButton.Content = "正在补传…";
        try
        {
            var message = await ViewModel.RepairKuaishouVideosAsync(titles);
            await InfoDialog.ShowAsync(owner, message, "视频补传", 560, 320);
        }
        finally
        {
            KuaishouRepairVideosButton.Content = "视频补传";
            KuaishouRepairVideosButton.IsEnabled = true;
        }
    }

    private void OnArchiveCheckedClick(object? sender, RoutedEventArgs e) =>
        ArchiveRequested?.Invoke(this, EventArgs.Empty);

    private void OnExportUploadReportClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        try { ViewModel.ExportKuaishouUploadReport(); }
        catch (Exception ex) { ViewModel.StatusMessage = $"导出上传记录失败：{ex.Message}"; }
    }

    private void OnOpenUploadReportClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        try { ViewModel.OpenKuaishouUploadReport(); }
        catch (Exception ex) { ViewModel.StatusMessage = $"查看上传记录失败：{ex.Message}"; }
    }

    private void OnKuaishouAdxDownloadClick(object? sender, RoutedEventArgs e) =>
        KuaishouAdxRequested?.Invoke(this, new KuaishouAdxRequestedEventArgs(false, (int)(KuaishouAdxCount.Value ?? 8)));

    private void OnKuaishouAdxPublishClick(object? sender, RoutedEventArgs e) =>
        KuaishouAdxRequested?.Invoke(this, new KuaishouAdxRequestedEventArgs(true, (int)(KuaishouAdxCount.Value ?? 8)));

    private async void OnPickConfigClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择视频号自动化配置",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON 配置") { Patterns = ["*.json"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count > 0)
            ViewModel.DraftConfigPath = files[0].Path.LocalPath;
    }

    private async void OnPickCustomVideosClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || ViewModel is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发表的视频",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("视频文件")
                {
                    Patterns = ["*.mp4", "*.mov", "*.m4v", "*.mkv", "*.avi", "*.flv", "*.ts", "*.wmv", "*.webm"],
                },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0) return;
        var paths = files.Select(file => file.Path.LocalPath).ToArray();
        ViewModel.DraftCustomVideoFilesText = string.Join(Environment.NewLine, paths);
        if (!Directory.Exists(ViewModel.DraftProjectDirectory))
            ViewModel.DraftProjectDirectory = Path.GetDirectoryName(paths[0]) ?? string.Empty;
    }

    private async void OnAdvancedConfigClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null || ViewModel is null) return;
        var draftJob = new PlatformPublisher.Common.Models.PublishJob
        {
            PlatformOptionsJson = ViewModel.DraftPlatformOptionsJson,
            PublishDescription = ViewModel.DraftPublishDescription,
            DeclareOriginal = ViewModel.DraftDeclareOriginal,
            HideLocation = ViewModel.DraftHideLocation,
        };
        var result = await WeixinPublishConfigDialog.ShowAsync(owner, WeixinPublishOptions.FromJob(draftJob),
            showSeriesUploadMode: ViewModel.IsWeixinPlatform && ViewModel.SelectedJobKind?.Value == PublishJobKind.Series);
        if (result is null) return;
        ViewModel.DraftPlatformOptionsJson = result.ToJson();
        ViewModel.DraftPublishDescription = result.DescriptionTemplate;
        ViewModel.DraftDeclareOriginal = result.DeclareOriginal;
        ViewModel.DraftHideLocation = !string.IsNullOrWhiteSpace(result.LocationOptionText);
        ViewModel.StatusMessage = "视频号高级发表配置已应用到新任务。";
    }
}

public sealed record KuaishouAdxRequestedEventArgs(bool PublishLocal, int TopCount);
