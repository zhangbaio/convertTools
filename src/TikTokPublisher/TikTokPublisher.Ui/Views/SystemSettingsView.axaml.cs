using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ShortDrama.Infrastructure.Automation;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class SystemSettingsView : UserControl
{
    private SystemSettingsViewModel? _vm;
    private bool _syncingProjectImageTemplateCombo;

    public SystemSettingsView()
    {
        InitializeComponent();
    }
    public void BindServices(SystemServicesViewModel vm)
    {
        SoftwareLicenseHost.Bind(vm);
        var xinge = new XingeConnectionViewModel(null, "TikTokPublisher",
            "启用后客户端会用账号登录 XINGE，注册当前工作目录和 TikTok 账号列表，并接收 XINGE 下发的上传任务。")
        {
            ShowSaveButton = true,
            ShowHeading = false,
        };
        XingeConnectionHost.Bind(xinge);
        vm.AttachXinge(xinge);
        SoftwareLicenseTab.IsVisible = true;
        XingeSettingsTab.IsVisible = true;
    }

    private void OnSettingsTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is TabControl tabs && ReferenceEquals(e.Source, tabs) && SettingsSaveFooter is { } footer)
        {
            footer.IsVisible = tabs.SelectedItem != SoftwareLicenseTab && tabs.SelectedItem != XingeSettingsTab;
        }
    }


    public void Bind(SystemSettingsViewModel vm)
    {
        if (_vm is not null)
        {
            _vm.SettingsSaved -= OnSettingsSaved;
            _vm.HgnewLoginProbeSucceeded -= OnHgnewLoginProbeSucceeded;
            _vm.CopyToClipboardAsync -= CopyTextToClipboardAsync;
        }

        _vm = vm;
        DataContext = vm;
        vm.SettingsSaved += OnSettingsSaved;
        vm.HgnewLoginProbeSucceeded += OnHgnewLoginProbeSucceeded;
        vm.CopyToClipboardAsync += CopyTextToClipboardAsync;
        InitializeComboBoxes();
        SyncCombosFromVm();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SystemSettingsViewModel.DramaDownloadDefaultQuality)
                or nameof(SystemSettingsViewModel.DramaSourceChain)
                or nameof(SystemSettingsViewModel.OverseasDramaSourceChain)
                or nameof(SystemSettingsViewModel.HghighEdition)
                or nameof(SystemSettingsViewModel.PikachuDramaType)
                or nameof(SystemSettingsViewModel.TiktokRoleReferenceSelectionMode)
                or nameof(SystemSettingsViewModel.TiktokRoleVectorViewMode)
                or nameof(SystemSettingsViewModel.PosterMode)
                or nameof(SystemSettingsViewModel.ImageModelId)
                or nameof(SystemSettingsViewModel.PosterTitleVerifyMode)
                or nameof(SystemSettingsViewModel.TiktokProjectImageTemplateId)
                or nameof(SystemSettingsViewModel.TiktokProjectImageSubtitleAiMode)
                or nameof(SystemSettingsViewModel.TiktokProofPdfRenderer)
                or nameof(SystemSettingsViewModel.ManagementDedupScope))
            {
                SyncCombosFromVm();
            }
        };
    }

    private async Task CopyTextToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            throw new InvalidOperationException("当前窗口无法访问剪贴板。");
        }

        await clipboard.SetTextAsync(text);
    }

    private async void OnSettingsSaved(ClientSettings settings)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        await InfoDialog.ShowSaveSuccessAsync(owner, "系统设置已保存成功。");
    }

    private async void OnHgnewLoginProbeSucceeded(HongguoLoginProbeResult result)
    {
        var lines = new List<string> { $"登录令牌：{MaskToken(result.Token)}" };
        if (!string.IsNullOrWhiteSpace(result.Email))
        {
            lines.Add($"邮箱：{result.Email}");
        }

        if (!string.IsNullOrWhiteSpace(result.VipExpiresAt))
        {
            lines.Add($"会员到期：{result.VipExpiresAt}");
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        await InfoDialog.ShowLoginProbeSuccessAsync(owner, string.Join(Environment.NewLine, lines));
    }

    private static string MaskToken(string? token)
    {
        var text = (token ?? string.Empty).Trim();
        return text.Length > 12 ? $"{text[..6]}...{text[^4..]}" : text;
    }

    private void InitializeComboBoxes()
    {
        FillDramaSourceCombo(DramaSourceCombo);
        DramaSourceCombo.SelectionChanged += OnDramaSourceChanged;
        FillDramaSourceCombo(OverseasDramaSourceCombo);
        OverseasDramaSourceCombo.SelectionChanged += OnOverseasDramaSourceChanged;

        DramaQualityCombo.Items.Clear();
        DramaQualityCombo.Items.Add(CreateItem("1080p+", "1080P+"));
        DramaQualityCombo.Items.Add(CreateItem("1080p", "1080P"));
        DramaQualityCombo.Items.Add(CreateItem("720p", "720P"));
        DramaQualityCombo.SelectionChanged += OnDramaQualityChanged;

        ForeignSourceCombo.Items.Clear();
        foreach (var source in ForeignPlayletClient.Sources)
            ForeignSourceCombo.Items.Add(CreateItem(source.Label, source.Id));
        ForeignSourceCombo.SelectionChanged += OnForeignSourceChanged;

        ForeignLanguageCombo.Items.Clear();
        foreach (var language in ForeignPlayletClient.Languages)
            ForeignLanguageCombo.Items.Add(CreateItem(language.Label, language.Id));
        ForeignLanguageCombo.SelectionChanged += OnForeignLanguageChanged;

        HghighEditionCombo.Items.Clear();
        HghighEditionCombo.Items.Add(CreateItem("高码率版", HongguoClientProfile.HighEdition));
        HghighEditionCombo.Items.Add(CreateItem("标准版", HongguoClientProfile.StandardEdition));
        HghighEditionCombo.SelectionChanged += OnHghighEditionChanged;

        HghighStandardPlaybackCombo.Items.Clear();
        HghighStandardPlaybackCombo.Items.Add(CreateItem("单集请求", HongguoClientProfile.StandardPlaybackSingle));
        HghighStandardPlaybackCombo.Items.Add(CreateItem("批量请求", HongguoClientProfile.StandardPlaybackBatch));
        HghighStandardPlaybackCombo.SelectionChanged += OnHghighStandardPlaybackChanged;

        PikachuTypeCombo.Items.Clear();
        PikachuTypeCombo.Items.Add(CreateItem("红果短剧（需要番茄 Cookie）", "short"));
        PikachuTypeCombo.Items.Add(CreateItem("红果漫剧（类型编号 13，免 Cookie）", "manga"));
        PikachuTypeCombo.SelectionChanged += OnPikachuTypeChanged;



        RoleReferenceSelectionModeCombo.Items.Clear();
        RoleReferenceSelectionModeCombo.Items.Add(CreateItem("本地链路（默认）", "local"));
        RoleReferenceSelectionModeCombo.Items.Add(CreateItem("AI全量优选（推荐）", "ai_full_review"));
        RoleReferenceSelectionModeCombo.SelectionChanged += OnRoleReferenceSelectionModeChanged;

        RoleVectorViewModeCombo.Items.Clear();
        RoleVectorViewModeCombo.Items.Add(CreateItem("多角度转面图（推荐）", "multi_angle"));
        RoleVectorViewModeCombo.Items.Add(CreateItem("单图兼容模式", "single"));
        RoleVectorViewModeCombo.SelectionChanged += OnRoleVectorViewModeChanged;

        PosterModeCombo.Items.Clear();
        PosterModeCombo.Items.Add(CreateItem("AI 直接生成", ClientSettingsDefaults.PosterModeDirect));
        PosterModeCombo.Items.Add(CreateItem("AI 消除文字后固定字体写入", ClientSettingsDefaults.PosterModeEraseFixedTitle));
        PosterModeCombo.SelectionChanged += OnPosterModeChanged;



        ImageModelCombo.Items.Clear();
        ImageModelCombo.Items.Add(CreateItem(ClientSettingsDefaults.ImageModelId, ClientSettingsDefaults.ImageModelId));
        ImageModelCombo.Items.Add(CreateItem(ClientSettingsDefaults.ImageModelIdPro, ClientSettingsDefaults.ImageModelIdPro));
        ImageModelCombo.SelectionChanged += OnImageModelChanged;

        PosterTitleVerifyModeCombo.Items.Clear();
        PosterTitleVerifyModeCombo.Items.Add(CreateItem("AI重试后重绘", "fallback_repaint"));
        PosterTitleVerifyModeCombo.Items.Add(CreateItem("AI重试后仅警告", "warn"));
        PosterTitleVerifyModeCombo.Items.Add(CreateItem("AI重试后阻断", "blocking"));
        PosterTitleVerifyModeCombo.SelectionChanged += OnPosterTitleVerifyModeChanged;


        ProjectImageTemplateCombo.SelectionChanged -= OnProjectImageTemplateChanged;
        ProjectImageTemplateCombo.SelectionChanged += OnProjectImageTemplateChanged;

        ProjectImageSubtitleAiModeCombo.Items.Clear();
        ProjectImageSubtitleAiModeCombo.Items.Add(CreateItem("快速（推荐）", "fast"));
        ProjectImageSubtitleAiModeCombo.Items.Add(CreateItem("准确", "accurate"));
        ProjectImageSubtitleAiModeCombo.Items.Add(CreateItem("关闭", "off"));
        ProjectImageSubtitleAiModeCombo.SelectionChanged += OnProjectImageSubtitleAiModeChanged;

        ProofPdfRendererCombo.Items.Clear();
        ProofPdfRendererCombo.Items.Add(CreateItem("WPS（默认）", "wps"));
        ProofPdfRendererCombo.Items.Add(CreateItem("LibreOffice", "libreoffice"));
        ProofPdfRendererCombo.SelectionChanged += OnProofPdfRendererChanged;

        ManagementDedupScopeCombo.Items.Clear();
        ManagementDedupScopeCombo.Items.Add(CreateItem("按 TIKTOK用户名", "tiktok_username"));
        ManagementDedupScopeCombo.Items.Add(CreateItem("按软件账号", "software_user"));
        ManagementDedupScopeCombo.Items.Add(CreateItem("全部剧集", "all_series"));
        ManagementDedupScopeCombo.SelectionChanged += OnManagementDedupScopeChanged;
    }

    private static ComboBoxItem CreateItem(string label, string value) =>
        new() { Content = label, Tag = value };

    private void SyncCombosFromVm()
    {
        if (_vm is null) return;
        SelectComboItem(DramaSourceCombo, _vm.DramaSourceChain);
        SelectComboItem(OverseasDramaSourceCombo, _vm.OverseasDramaSourceChain);
        SelectComboItem(DramaQualityCombo, _vm.DramaDownloadDefaultQuality);
        SelectComboItem(ForeignSourceCombo, _vm.ForeignSearchSource);
        SelectComboItem(ForeignLanguageCombo, _vm.ForeignLanguage);
        SelectComboItem(HghighEditionCombo, _vm.HghighEdition);
        SelectComboItem(HghighStandardPlaybackCombo, _vm.HghighStandardPlaybackMode);
        SelectComboItem(PikachuTypeCombo, _vm.PikachuDramaType);
        SelectComboItem(RoleReferenceSelectionModeCombo, _vm.TiktokRoleReferenceSelectionMode);
        SelectComboItem(RoleVectorViewModeCombo, _vm.TiktokRoleVectorViewMode);
        SelectComboItem(PosterModeCombo, _vm.PosterMode);
        SelectComboItem(ImageModelCombo, ClientSettingsDefaults.NormalizeImageModelId(_vm.ImageModelId));
        SelectComboItem(PosterTitleVerifyModeCombo, _vm.PosterTitleVerifyMode);
        SyncProjectImageTemplateComboFromVm();
        SelectComboItem(ProjectImageSubtitleAiModeCombo, _vm.TiktokProjectImageSubtitleAiMode);
        SelectComboItem(ProofPdfRendererCombo, _vm.TiktokProofPdfRenderer);
        SelectComboItem(ManagementDedupScopeCombo, _vm.ManagementDedupScope);
        UpdatePosterModeUi();
    }

    private static void SelectComboItem(ComboBox combo, string? value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private void SyncProjectImageTemplateComboFromVm()
    {
        if (_vm is null || _syncingProjectImageTemplateCombo)
            return;

        var selectedId = TikTokProjectImageTemplateCatalog.NormalizeId(_vm.TiktokProjectImageTemplateId);

        _syncingProjectImageTemplateCombo = true;
        try
        {
            ProjectImageTemplateCombo.Items.Clear();
            foreach (var option in TikTokProjectImageTemplateCatalog.BuiltInOptions)
                ProjectImageTemplateCombo.Items.Add(CreateItem(option.SelectionLabel, option.Id));

            var selectedItem = ProjectImageTemplateCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    selectedId,
                    StringComparison.OrdinalIgnoreCase));
            ProjectImageTemplateCombo.SelectedItem = selectedItem;
        }
        finally
        {
            _syncingProjectImageTemplateCombo = false;
        }
    }

    private void OnDramaQualityChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || DramaQualityCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.DramaDownloadDefaultQuality = item.Tag as string ?? "1080P";
    }

    private static void FillDramaSourceCombo(ComboBox combo)
    {
        combo.Items.Clear();
        combo.Items.Add(CreateItem("红果短剧助手", "hghigh"));
        combo.Items.Add(CreateItem("皮卡丘", "pikachu"));
        combo.Items.Add(CreateItem("红果短剧下载器", "playlet"));
        combo.Items.Add(CreateItem("红果APP", "hongguoapp"));
        combo.Items.Add(CreateItem("DramaBox", "dramabox"));
        combo.Items.Add(CreateItem("海外短剧下载器", "foreign"));
    }

    private void OnDramaSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || DramaSourceCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.DramaSourceChain = item.Tag as string ?? VisibleDramaSource.DefaultChain;
    }

    private void OnOverseasDramaSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || OverseasDramaSourceCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.OverseasDramaSourceChain = item.Tag as string ?? VisibleDramaSource.DefaultOverseasChain;
    }

    private void OnForeignSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || ForeignSourceCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.ForeignSearchSource = item.Tag as string ?? ForeignPlayletClient.DefaultSource;
    }

    private void OnForeignLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || ForeignLanguageCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.ForeignLanguage = item.Tag as string ?? ForeignPlayletClient.DefaultLanguage;
    }

    private void OnPikachuTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || PikachuTypeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.PikachuDramaType = item.Tag as string ?? "short";
    }

    private void OnRoleReferenceSelectionModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || RoleReferenceSelectionModeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.TiktokRoleReferenceSelectionMode = item.Tag as string
            ?? ClientSettingsDefaults.TiktokRoleReferenceSelectionMode;
    }

    private void OnHghighEditionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || HghighEditionCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.HghighEdition = item.Tag as string ?? HongguoClientProfile.HighEdition;
    }

    private void OnHghighStandardPlaybackChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || HghighStandardPlaybackCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.HghighStandardPlaybackMode = item.Tag as string ?? HongguoClientProfile.StandardPlaybackSingle;
    }

    private void OnRoleVectorViewModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || RoleVectorViewModeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.TiktokRoleVectorViewMode = item.Tag as string ?? ClientSettingsDefaults.TiktokRoleVectorViewMode;
    }

    private void OnPosterModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || PosterModeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.PosterMode = item.Tag as string ?? "original";
        UpdatePosterModeUi();
    }

    private void UpdatePosterModeUi()
    {
        var mode = (PosterModeCombo.SelectedItem as ComboBoxItem)?.Tag as string
                   ?? _vm?.PosterMode
                   ?? "original";
        FrameExtractSettingsPanel.IsVisible = string.Equals(mode, "video_frame", StringComparison.OrdinalIgnoreCase);
        PosterModeHintText.Text = string.Equals(mode, "erase_fixed_lower_title", StringComparison.OrdinalIgnoreCase)
            ? "AI 只清除封面上的全部原文字，然后在下半部分用固定黄字黑边写入新剧名。"
            : "AI 重新设计海报：更换背景、构图和人物动作服饰，只写一次新剧名，并进行标题校验。";
    }

    private void OnImageModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || ImageModelCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.ImageModelId = item.Tag as string ?? ClientSettingsDefaults.ImageModelId;
    }

    private void OnPosterTitleVerifyModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || PosterTitleVerifyModeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.PosterTitleVerifyMode = item.Tag as string ?? "fallback_repaint";
    }

    private void OnProjectImageSubtitleAiModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || ProjectImageSubtitleAiModeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.TiktokProjectImageSubtitleAiMode = item.Tag as string ?? "fast";
    }

    private void OnProjectImageTemplateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingProjectImageTemplateCombo ||
            _vm is null ||
            ProjectImageTemplateCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string templateId ||
            string.IsNullOrWhiteSpace(templateId))
        {
            return;
        }

        // Updating the view model raises PropertyChanged synchronously. The view reacts by
        // synchronizing all combo boxes, which must not clear this ComboBox while Avalonia is
        // still committing the user's selection; doing so corrupts its internal selected index.
        _syncingProjectImageTemplateCombo = true;
        try
        {
            _vm.TiktokProjectImageTemplateId = templateId;
        }
        finally
        {
            _syncingProjectImageTemplateCombo = false;
        }
    }

    private void OnProofPdfRendererChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || ProofPdfRendererCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.TiktokProofPdfRenderer = item.Tag as string ?? ClientSettingsDefaults.TiktokProofPdfRenderer;
    }

    private async void OnBrowseSyncWorkspaceRootClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || _vm is null)
            return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择账号同步工作根目录",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            _vm.TiktokSyncWorkspaceRoot = path;
    }

    private async void OnBrowseProofTemplateClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("选择证明材料 Word 模板", "*.docx");
        if (_vm is not null && !string.IsNullOrWhiteSpace(path))
            _vm.TiktokProofTemplateDocxPath = path;
    }

    private async void OnBrowseProofWpsClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("选择 WPS 程序", "*.exe");
        if (_vm is not null && !string.IsNullOrWhiteSpace(path))
            _vm.TiktokProofWpsPath = path;
    }

    private async void OnBrowseHghighClientExeClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("选择 HG 短剧下载器客户端", "*.exe");
        if (_vm is not null && !string.IsNullOrWhiteSpace(path))
            _vm.HghighActiveClientExe = path;
    }

    private async Task<string?> PickFileAsync(string title, params string[] patterns)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(title) { Patterns = patterns },
            ],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private void OnManagementDedupScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || ManagementDedupScopeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.ManagementDedupScope = item.Tag as string ?? "tiktok_username";
    }
}
