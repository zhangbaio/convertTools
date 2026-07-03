using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Ui.Views;

public partial class AccountSettingsDialog : Window
{
    private readonly TikTokAccountProfile _profile;

    public AccountSettingsDialog(TikTokAccountProfile profile)
    {
        InitializeComponent();
        _profile = profile;
        LoadToUi();
    }

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private void LoadToUi()
    {
        var p = _profile;
        NameBox.Text = p.Name;
        NicknameBox.Text = p.TiktokAccountNickname;
        LoginEmailBox.Text = p.TiktokLoginEmail;
        SeriesUrlBox.Text = p.TiktokSeriesUrl;
        StorageStateBox.Text = p.TiktokStorageStatePath;
        WorkspaceBox.Text = !string.IsNullOrWhiteSpace(p.TiktokUploadProfilePath) ? p.TiktokUploadProfilePath : p.LastWorkspace;
        DownloadWorkspaceBox.Text = p.LastDownloadWorkspace;

        ContractIdBox.Text = p.TiktokContractId;
        SelectByTag(ContractModeCombo, p.TiktokContractIdMode, "manual");
        SelectByTag(SubmitActionCombo, p.TiktokSubmitAction, "draft");
        SelectByTag(PublishModeCombo, p.TiktokPublishMode, "auto_after_review");
        SelectByTag(AudienceCombo, p.TiktokTargetAudienceMode, "female");
        SelectByTag(SourceLanguageCombo, p.TiktokSourceLanguage, "zh");
        SelectByTag(UploadStrategyCombo, p.TiktokUploadStrategy, "classic");

        PaidEnabledBox.IsChecked = p.TiktokPaidEnabled;
        AiDramaBox.IsChecked = p.TiktokIsAiDrama;
        ConsignmentBox.IsChecked = p.TiktokConsignmentEnabled;
        AnchorPromotionBox.IsChecked = p.TiktokAnchorPromotionEnabled;
        ProfilePreviewBox.Value = p.TiktokProfilePreviewEpisodes;
        FreePreviewBox.Value = p.TiktokFreePreviewEpisodes;
        UploadStallBox.Value = p.TiktokUploadStallSeconds;
        ProjectConcurrencyBox.Value = p.TiktokProjectConcurrency;
        UploadBatchSizeBox.Value = p.TiktokUploadBatchSize;
        UploadBatchStallBox.Value = p.TiktokUploadBatchStallSeconds;
        UploadBatchRetriesBox.Value = p.TiktokUploadBatchMaxRetries;
        ExpectedPriceValueBox.Text = p.TiktokExpectedFullPriceValue;

        ProxyEnabledBox.IsChecked = p.TiktokProxyEnabled;
        SelectByTag(ProxyTypeCombo, p.TiktokProxyType, "http");
        ProxyHostBox.Text = p.TiktokProxyHost;
        ProxyPortBox.Value = p.TiktokProxyPort;
        ProxyUsernameBox.Text = p.TiktokProxyUsername;
        ProxyPasswordBox.Text = p.TiktokProxyPassword;
    }

    private void SaveFromUi()
    {
        var p = _profile;
        p.Name = NameBox.Text?.Trim() ?? p.Name;
        p.TiktokAccountNickname = NicknameBox.Text?.Trim() ?? "";
        p.TiktokLoginEmail = LoginEmailBox.Text?.Trim() ?? "";
        p.TiktokSeriesUrl = string.IsNullOrWhiteSpace(SeriesUrlBox.Text) ? TikTokUrls.DefaultSeriesDraftUrl : SeriesUrlBox.Text.Trim();
        p.TiktokStorageStatePath = StorageStateBox.Text?.Trim() ?? p.TiktokStorageStatePath;
        var workspace = WorkspaceBox.Text?.Trim() ?? "";
        p.TiktokUploadProfilePath = workspace;
        p.LastWorkspace = workspace;
        p.LastDownloadWorkspace = DownloadWorkspaceBox.Text?.Trim() ?? "";

        p.TiktokContractId = ContractIdBox.Text?.Trim() ?? "";
        p.TiktokContractIdMode = TagOf(ContractModeCombo, "manual");
        p.TiktokSubmitAction = TagOf(SubmitActionCombo, "draft");
        p.TiktokPublishMode = TagOf(PublishModeCombo, "auto_after_review");
        p.TiktokTargetAudienceMode = TagOf(AudienceCombo, "female");
        p.TiktokSourceLanguage = TagOf(SourceLanguageCombo, "zh");
        p.TiktokUploadStrategy = TagOf(UploadStrategyCombo, "classic");
        p.TiktokPaidEnabled = PaidEnabledBox.IsChecked == true;
        p.TiktokIsAiDrama = AiDramaBox.IsChecked == true;
        p.TiktokConsignmentEnabled = ConsignmentBox.IsChecked == true;
        p.TiktokAnchorPromotionEnabled = AnchorPromotionBox.IsChecked == true;
        p.TiktokProfilePreviewEpisodes = (int)(ProfilePreviewBox.Value ?? 1);
        p.TiktokFreePreviewEpisodes = (int)(FreePreviewBox.Value ?? 1);
        p.TiktokUploadStallSeconds = (int)(UploadStallBox.Value ?? 180);
        p.TiktokProjectConcurrency = (int)(ProjectConcurrencyBox.Value ?? 1);
        p.TiktokUploadBatchSize = (int)(UploadBatchSizeBox.Value ?? 3);
        p.TiktokUploadBatchStallSeconds = (int)(UploadBatchStallBox.Value ?? 75);
        p.TiktokUploadBatchMaxRetries = (int)(UploadBatchRetriesBox.Value ?? 3);
        p.TiktokExpectedFullPriceValue = ExpectedPriceValueBox.Text?.Trim() ?? "";

        p.TiktokProxyEnabled = ProxyEnabledBox.IsChecked == true;
        p.TiktokProxyType = TagOf(ProxyTypeCombo, "http");
        p.TiktokProxyHost = ProxyHostBox.Text?.Trim() ?? "";
        p.TiktokProxyPort = (int)(ProxyPortBox.Value ?? 0);
        p.TiktokProxyUsername = ProxyUsernameBox.Text?.Trim() ?? "";
        p.TiktokProxyPassword = ProxyPasswordBox.Text ?? "";
    }

    private async void OnBrowseWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (Storage is null) return;
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择上传工作目录",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;
        WorkspaceBox.Text = folder.Path.LocalPath;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        SaveFromUi();
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private static void SelectByTag(ComboBox combo, string? tag, string fallback)
    {
        var key = string.IsNullOrWhiteSpace(tag) ? fallback : tag.Trim();
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem ci && string.Equals(ci.Tag as string, key, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = ci;
                return;
            }
        }
        if (combo.ItemCount > 0) combo.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox combo, string fallback)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
}
