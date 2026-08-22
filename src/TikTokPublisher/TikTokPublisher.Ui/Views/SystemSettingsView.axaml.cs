using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ShortDrama.Infrastructure.Automation;
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
            if (args.PropertyName is nameof(SystemSettingsViewModel.DramaSourceChain)
                or nameof(SystemSettingsViewModel.PikachuDramaType)
                or nameof(SystemSettingsViewModel.HongguoLocalDownloadMode)
                or nameof(SystemSettingsViewModel.HongguoLocalTranscodeEngine)
                or nameof(SystemSettingsViewModel.TiktokSilenceAsrEngine)
                or nameof(SystemSettingsViewModel.TiktokSilenceRepairMode)
                or nameof(SystemSettingsViewModel.TiktokRoleReferenceSelectionMode)
                or nameof(SystemSettingsViewModel.TiktokRoleVectorViewMode)
                or nameof(SystemSettingsViewModel.PosterMode)
                or nameof(SystemSettingsViewModel.ImageProvider)
                or nameof(SystemSettingsViewModel.PosterTitleVerifyMode)
                or nameof(SystemSettingsViewModel.TiktokProjectImageGenerationMode)
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
        DramaSourceCombo.Items.Clear();
        DramaSourceCombo.Items.Add(CreateItem("红果新接口", "hgnew"));
        DramaSourceCombo.Items.Add(CreateItem("红果高码率", "hghigh"));
        DramaSourceCombo.Items.Add(CreateItem("本地直连", "hglocal"));
        DramaSourceCombo.Items.Add(CreateItem("皮卡丘", "pikachu"));
        DramaSourceCombo.SelectionChanged += OnDramaSourceChanged;

        PikachuTypeCombo.Items.Clear();
        PikachuTypeCombo.Items.Add(CreateItem("红果短剧（需要番茄 Cookie）", "short"));
        PikachuTypeCombo.Items.Add(CreateItem("红果漫剧（类型编号 13，免 Cookie）", "manga"));
        PikachuTypeCombo.SelectionChanged += OnPikachuTypeChanged;

        HongguoLocalDownloadModeCombo.Items.Clear();
        HongguoLocalDownloadModeCombo.Items.Add(CreateItem("快速模式（保持原格式）", "fast"));
        HongguoLocalDownloadModeCombo.Items.Add(CreateItem("兼容模式（必要时转码）", "compatible"));
        HongguoLocalDownloadModeCombo.SelectionChanged += OnHongguoLocalDownloadModeChanged;

        HongguoLocalTranscodeEngineCombo.Items.Clear();
        HongguoLocalTranscodeEngineCombo.Items.Add(CreateItem("自动选择（推荐）", "auto"));
        HongguoLocalTranscodeEngineCombo.Items.Add(CreateItem("英伟达显卡加速", "nvenc"));
        HongguoLocalTranscodeEngineCombo.Items.Add(CreateItem("处理器转码", "cpu"));
        HongguoLocalTranscodeEngineCombo.SelectionChanged += OnHongguoLocalTranscodeEngineChanged;

        AsrEngineCombo.Items.Clear();
        AsrEngineCombo.Items.Add(CreateItem("火山 ASR（在线，最准）", "volcengine"));
        AsrEngineCombo.Items.Add(CreateItem("本地 Paraformer（免费离线）", "local"));
        AsrEngineCombo.Items.Add(CreateItem("混合（本地 + 临界用火山复核）", "hybrid"));
        AsrEngineCombo.SelectionChanged += OnAsrEngineChanged;

        SilenceRepairModeCombo.Items.Clear();
        SilenceRepairModeCombo.Items.Add(CreateItem("自动（片头尾裁剪/中间变速）", "auto"));
        SilenceRepairModeCombo.Items.Add(CreateItem("一律裁剪", "trim"));
        SilenceRepairModeCombo.Items.Add(CreateItem("一律变速", "speedup"));
        SilenceRepairModeCombo.SelectionChanged += OnSilenceRepairModeChanged;

        RoleReferenceSelectionModeCombo.Items.Clear();
        RoleReferenceSelectionModeCombo.Items.Add(CreateItem("本地链路（默认）", "local"));
        RoleReferenceSelectionModeCombo.Items.Add(CreateItem("AI全量优选（推荐）", "ai_full_review"));
        RoleReferenceSelectionModeCombo.SelectionChanged += OnRoleReferenceSelectionModeChanged;

        RoleVectorViewModeCombo.Items.Clear();
        RoleVectorViewModeCombo.Items.Add(CreateItem("多角度转面图（推荐）", "multi_angle"));
        RoleVectorViewModeCombo.Items.Add(CreateItem("单图兼容模式", "single"));
        RoleVectorViewModeCombo.SelectionChanged += OnRoleVectorViewModeChanged;

        PosterModeCombo.Items.Clear();
        PosterModeCombo.Items.Add(CreateItem("原始海报AI改标题并校验", "original"));
        PosterModeCombo.SelectionChanged += OnPosterModeChanged;

        ImageProviderCombo.Items.Clear();
        ImageProviderCombo.Items.Add(CreateItem("豆包", "doubao"));
        ImageProviderCombo.Items.Add(CreateItem("Ofox Image2", "ofox_image2"));
        ImageProviderCombo.SelectionChanged += OnImageProviderChanged;

        PosterTitleVerifyModeCombo.Items.Clear();
        PosterTitleVerifyModeCombo.Items.Add(CreateItem("AI重试后重绘", "fallback_repaint"));
        PosterTitleVerifyModeCombo.Items.Add(CreateItem("AI重试后仅警告", "warn"));
        PosterTitleVerifyModeCombo.Items.Add(CreateItem("AI重试后阻断", "blocking"));
        PosterTitleVerifyModeCombo.SelectionChanged += OnPosterTitleVerifyModeChanged;

        ProjectImageGenerationModeCombo.Items.Clear();
        ProjectImageGenerationModeCombo.Items.Add(CreateItem("图片模板", "image_template"));
        ProjectImageGenerationModeCombo.Items.Add(CreateItem("FableCut真实工程", "fablecut"));
        ProjectImageGenerationModeCombo.SelectionChanged += OnProjectImageGenerationModeChanged;

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
        SelectComboItem(PikachuTypeCombo, _vm.PikachuDramaType);
        SelectComboItem(HongguoLocalDownloadModeCombo, _vm.HongguoLocalDownloadMode);
        SelectComboItem(HongguoLocalTranscodeEngineCombo, _vm.HongguoLocalTranscodeEngine);
        SelectComboItem(AsrEngineCombo, _vm.TiktokSilenceAsrEngine);
        SelectComboItem(SilenceRepairModeCombo, _vm.TiktokSilenceRepairMode);
        SelectComboItem(RoleReferenceSelectionModeCombo, _vm.TiktokRoleReferenceSelectionMode);
        SelectComboItem(RoleVectorViewModeCombo, _vm.TiktokRoleVectorViewMode);
        _vm.PosterMode = ClientSettingsDefaults.PosterMode;
        SelectComboItem(PosterModeCombo, ClientSettingsDefaults.PosterMode);
        SelectComboItem(ImageProviderCombo, _vm.ImageProvider);
        SelectComboItem(PosterTitleVerifyModeCombo, _vm.PosterTitleVerifyMode);
        SelectComboItem(ProjectImageGenerationModeCombo, _vm.TiktokProjectImageGenerationMode);
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

        var selectedId = (_vm.TiktokProjectImageTemplateId ?? string.Empty).Trim();
        if (selectedId.Length == 0)
            selectedId = ClientSettingsDefaults.TiktokProjectImageTemplateId;

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
            if (selectedItem is null)
            {
                selectedItem = CreateItem(
                    TikTokProjectImageTemplateCatalog.CreateSelectionLabel(selectedId),
                    selectedId);
                ProjectImageTemplateCombo.Items.Add(selectedItem);
            }

            ProjectImageTemplateCombo.SelectedItem = selectedItem;
        }
        finally
        {
            _syncingProjectImageTemplateCombo = false;
        }
    }

    private void OnDramaSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || DramaSourceCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.DramaSourceChain = item.Tag as string ?? "hgnew";
    }

    private void OnPikachuTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || PikachuTypeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.PikachuDramaType = item.Tag as string ?? "short";
    }

    private void OnHongguoLocalDownloadModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || HongguoLocalDownloadModeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.HongguoLocalDownloadMode = item.Tag as string ?? "fast";
    }

    private void OnHongguoLocalTranscodeEngineChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || HongguoLocalTranscodeEngineCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.HongguoLocalTranscodeEngine = item.Tag as string ?? "auto";
    }

    private void OnAsrEngineChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || AsrEngineCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.TiktokSilenceAsrEngine = item.Tag as string ?? "local";
    }

    private void OnSilenceRepairModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || SilenceRepairModeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.TiktokSilenceRepairMode = item.Tag as string ?? "auto";
    }

    private void OnRoleReferenceSelectionModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || RoleReferenceSelectionModeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.TiktokRoleReferenceSelectionMode = item.Tag as string
            ?? ClientSettingsDefaults.TiktokRoleReferenceSelectionMode;
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
        PosterModeHintText.Text = "替换原海报标题，清除人物名、作者说明等其他文字，并进行全图校验。";
    }

    private void OnImageProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || ImageProviderCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.ImageProvider = item.Tag as string ?? "doubao";
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

    private void OnProjectImageGenerationModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || ProjectImageGenerationModeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.TiktokProjectImageGenerationMode = item.Tag as string ?? ClientSettingsDefaults.TiktokProjectImageGenerationMode;
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
        var path = await PickFileAsync("选择高码率客户端", "*.exe");
        if (_vm is not null && !string.IsNullOrWhiteSpace(path))
            _vm.HghighClientExe = path;
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
