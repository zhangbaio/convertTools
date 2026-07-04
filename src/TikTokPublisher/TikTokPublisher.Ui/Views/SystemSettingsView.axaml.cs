using Avalonia.Controls;
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
            _vm.SettingsSaved -= OnSettingsSaved;

        _vm = vm;
        DataContext = vm;
        vm.SettingsSaved += OnSettingsSaved;
        InitializeComboBoxes();
        SyncCombosFromVm();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SystemSettingsViewModel.DramaSourceChain)
                or nameof(SystemSettingsViewModel.Hongguo52ApiSearchType)
                or nameof(SystemSettingsViewModel.PikachuDramaType)
                or nameof(SystemSettingsViewModel.TiktokSilenceAsrEngine)
                or nameof(SystemSettingsViewModel.TiktokSilenceRepairMode)
                or nameof(SystemSettingsViewModel.PosterMode)
                or nameof(SystemSettingsViewModel.ImageProvider)
                or nameof(SystemSettingsViewModel.PosterTitleVerifyMode)
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

    private void InitializeComboBoxes()
    {
        DramaSourceCombo.Items.Clear();
        DramaSourceCombo.Items.Add(CreateItem("红果新接口", "hgnew"));
        DramaSourceCombo.Items.Add(CreateItem("52API", "hg52api"));
        DramaSourceCombo.Items.Add(CreateItem("本地直连", "hglocal"));
        DramaSourceCombo.Items.Add(CreateItem("皮卡丘", "pikachu"));
        DramaSourceCombo.SelectionChanged += OnDramaSourceChanged;

        Api52SearchTypeCombo.Items.Clear();
        Api52SearchTypeCombo.Items.Add(CreateItem("综合搜索 (search)", "search"));
        Api52SearchTypeCombo.Items.Add(CreateItem("漫画搜索 (mj_search)", "mj_search"));
        Api52SearchTypeCombo.SelectionChanged += OnApi52SearchTypeChanged;

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
        PosterModeCombo.Items.Add(CreateItem("原始海报AI改标题", "original"));
        PosterModeCombo.Items.Add(CreateItem("AI去字+PIL重绘", "poster_ai_erase_pil_title"));
        PosterModeCombo.Items.Add(CreateItem("原图AI重绘", "poster_ai_edit"));
        PosterModeCombo.SelectionChanged += OnPosterModeChanged;

        ImageProviderCombo.Items.Clear();
        ImageProviderCombo.Items.Add(CreateItem("豆包", "doubao"));
        ImageProviderCombo.Items.Add(CreateItem("Ofox Image2", "ofox_image2"));
        ImageProviderCombo.SelectionChanged += OnImageProviderChanged;

        PosterTitleVerifyModeCombo.Items.Clear();
        PosterTitleVerifyModeCombo.Items.Add(CreateItem("失败后重绘", "fallback_repaint"));
        PosterTitleVerifyModeCombo.Items.Add(CreateItem("仅警告", "warn"));
        PosterTitleVerifyModeCombo.Items.Add(CreateItem("阻断失败", "blocking"));
        PosterTitleVerifyModeCombo.SelectionChanged += OnPosterTitleVerifyModeChanged;

        ManagementDedupScopeCombo.Items.Clear();
        ManagementDedupScopeCombo.Items.Add(CreateItem("按 TikTok 账号", "tiktok_username"));
        ManagementDedupScopeCombo.Items.Add(CreateItem("按软件账号", "software_user"));
        ManagementDedupScopeCombo.SelectionChanged += OnManagementDedupScopeChanged;
    }

    private static ComboBoxItem CreateItem(string label, string value) =>
        new() { Content = label, Tag = value };

    private void SyncCombosFromVm()
    {
        if (_vm is null) return;
        SelectComboItem(DramaSourceCombo, _vm.DramaSourceChain);
        SelectComboItem(Api52SearchTypeCombo, _vm.Hongguo52ApiSearchType);
        SelectComboItem(PikachuTypeCombo, _vm.PikachuDramaType);
        SelectComboItem(AsrEngineCombo, _vm.TiktokSilenceAsrEngine);
        SelectComboItem(SilenceRepairModeCombo, _vm.TiktokSilenceRepairMode);
        SelectComboItem(PosterModeCombo, _vm.PosterMode);
        SelectComboItem(ImageProviderCombo, _vm.ImageProvider);
        SelectComboItem(PosterTitleVerifyModeCombo, _vm.PosterTitleVerifyMode);
        SelectComboItem(ManagementDedupScopeCombo, _vm.ManagementDedupScope);
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

    private void OnApi52SearchTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || Api52SearchTypeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.Hongguo52ApiSearchType = item.Tag as string ?? "search";
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

    private void OnManagementDedupScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || ManagementDedupScopeCombo.SelectedItem is not ComboBoxItem item) return;
        _vm.ManagementDedupScope = item.Tag as string ?? "tiktok_username";
    }
}
