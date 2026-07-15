using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ShortDrama.Infrastructure.Automation;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class SystemSettingsView : UserControl
{
    private SystemSettingsViewModel? _vm;

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
        }

        _vm = vm;
        DataContext = vm;
        vm.SettingsSaved += OnSettingsSaved;
        vm.HgnewLoginProbeSucceeded += OnHgnewLoginProbeSucceeded;
        InitializeComboBoxes();
        SyncCombosFromVm();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SystemSettingsViewModel.DramaSourceChain)
                or nameof(SystemSettingsViewModel.PikachuDramaType)
                or nameof(SystemSettingsViewModel.TiktokSilenceAsrEngine)
                or nameof(SystemSettingsViewModel.TiktokSilenceRepairMode)
                or nameof(SystemSettingsViewModel.PosterMode)
                or nameof(SystemSettingsViewModel.ImageProvider)
                or nameof(SystemSettingsViewModel.PosterTitleVerifyMode)
                or nameof(SystemSettingsViewModel.TiktokProjectImageSubtitleAiMode)
                or nameof(SystemSettingsViewModel.TiktokProofPdfRenderer)
                or nameof(SystemSettingsViewModel.ManagementDedupScope))
            {
                SyncCombosFromVm();
            }
        };
    }

    private async void OnSettingsSaved(ClientSettings settings)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        await InfoDialog.ShowSaveSuccessAsync(owner, "系统设置已保存成功。");
    }

    private async void OnHgnewLoginProbeSucceeded(HongguoLoginProbeResult result)
    {
        var lines = new List<string> { $"token: {MaskToken(result.Token)}" };
        if (!string.IsNullOrWhiteSpace(result.Email))
        {
            lines.Add($"email: {result.Email}");
        }

        if (!string.IsNullOrWhiteSpace(result.VipExpiresAt))
        {
            lines.Add($"VIP 到期: {result.VipExpiresAt}");
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
        DramaSourceCombo.Items.Add(CreateItem("本地直连", "hglocal"));
        DramaSourceCombo.Items.Add(CreateItem("皮卡丘", "pikachu"));
        DramaSourceCombo.SelectionChanged += OnDramaSourceChanged;

        PikachuTypeCombo.Items.Clear();
        PikachuTypeCombo.Items.Add(CreateItem("红果短剧 (search_tab_id=10)", "short"));
        PikachuTypeCombo.Items.Add(CreateItem("红果漫画 (search_tab_id=13)", "manga"));
        PikachuTypeCombo.SelectionChanged += OnPikachuTypeChanged;

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

        PosterModeCombo.Items.Clear();
        PosterModeCombo.Items.Add(CreateItem("原始海报AI改标题并校验", "original"));
        PosterModeCombo.Items.Add(CreateItem("原始海报AI消除文字+PIL绘制标题", "poster_ai_erase_pil_title"));
        PosterModeCombo.Items.Add(CreateItem("视频抽帧AI生成封面", "video_frame"));
        PosterModeCombo.Items.Add(CreateItem("原始海报AI生成封面", "poster_ai_edit"));
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
        SelectComboItem(AsrEngineCombo, _vm.TiktokSilenceAsrEngine);
        SelectComboItem(SilenceRepairModeCombo, _vm.TiktokSilenceRepairMode);
        SelectComboItem(PosterModeCombo, _vm.PosterMode);
        SelectComboItem(ImageProviderCombo, _vm.ImageProvider);
        SelectComboItem(PosterTitleVerifyModeCombo, _vm.PosterTitleVerifyMode);
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
        PosterModeHintText.Text = mode switch
        {
            "original" => "仅替换原海报标题，并校验标题是否正确。",
            "poster_ai_erase_pil_title" => "先用 AI 消除原海报标题，再确定性绘制新剧名，标题文字更稳定。",
            "video_frame" => "从视频中抽取画面并交给 AI 生成封面，更贴近剧集实际内容。",
            "poster_ai_edit" => "基于原海报重新生成封面，允许整体视觉效果重做。",
            _ => string.Empty,
        };
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
