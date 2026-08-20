using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;

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
        ArchiveRootBox.Text = p.TiktokArchiveRootDir;
        DownloadWorkspaceBox.Text = p.LastDownloadWorkspace;
        DeleteVideosOnArchiveBox.IsChecked = p.TiktokDeleteVideosOnArchive;

        ContractIdBox.Text = p.TiktokContractId;
        SelectByTag(ContractModeCombo, p.TiktokContractIdMode, "manual");
        var submitAction = NormalizeSubmitAction(p.TiktokSubmitAction, p.TiktokSubmitEnabled);
        SelectByTag(SubmitActionCombo, submitAction, "submit");
        SubmitEnabledBox.IsChecked = string.Equals(submitAction, "submit", StringComparison.Ordinal);
        SelectByTag(UploadBrowserModeCombo, p.TiktokUploadBrowserMode, "embedded");
        PlaywrightHeadlessBox.IsChecked = p.TiktokPlaywrightUploadHeadless;
        SelectByTag(PublishModeCombo, p.TiktokPublishMode, "auto_after_review");
        SelectByTag(AudienceCombo, p.TiktokTargetAudienceMode, "ai_recommend");
        SelectByTag(SourceLanguageCombo, p.TiktokSourceLanguage, "zh");
        SelectByTag(UploadStrategyCombo, p.TiktokUploadStrategy, "classic");

        PaidEnabledBox.IsChecked = p.TiktokPaidEnabled;
        PaidRatioEnabledBox.IsChecked = p.TiktokPaidRatioEnabled;
        PaidRatioPercentBox.Value = (decimal)(p.TiktokPaidRatioPercent > 0
            ? p.TiktokPaidRatioPercent
            : 20);
        AiDramaBox.IsChecked = p.TiktokIsAiDrama;
        OriginalRightsHolderBox.IsChecked = p.TiktokIsOriginalRightsHolder;
        SelectByTag(ContentOriginalityCombo, p.TiktokContentOriginalityType, "original");
        CopyrightMaterials.Load(
            p.TiktokCopyrightMaterialTypes,
            p.TiktokUploadAiScriptOutlineWithScreenshots);
        ProofDeclarantCompanyNameBox.Text = p.TiktokProofDeclarantCompanyName;
        TimestampApplicantNameBox.Text = p.TiktokTimestampApplicantName;
        ProofSealPathBox.Text = p.TiktokProofSealPath;
        ProofCopyrightCompanyNameBox.Text = p.TiktokProofCopyrightCompanyName;
        AiScriptOutlineEpisodeCountBox.Value = Math.Clamp(
            p.TiktokAiScriptOutlineEpisodeCount > 0
                ? p.TiktokAiScriptOutlineEpisodeCount
                : TikTokAccountProfile.DefaultAiScriptOutlineEpisodeCount,
            1,
            120);
        AiRewriteSynopsisBox.IsChecked = p.TiktokAiRewriteSynopsis;
        ConsignmentBox.IsChecked = p.TiktokConsignmentEnabled;
        ZeroCostAdsBox.IsChecked = p.TiktokZeroCostAdsEnabled;
        DayZeroRoiBox.Value = (decimal)TikTokPublishOptions.NormalizeDayZeroRoi(p.TiktokDayZeroRoi);
        AnchorPromotionBox.IsChecked = p.TiktokAnchorPromotionEnabled;
        ProfilePreviewBox.Value = p.TiktokProfilePreviewEpisodes > 0 ? p.TiktokProfilePreviewEpisodes : 3;
        FreePreviewBox.Value = p.TiktokFreePreviewEpisodes > 0 ? p.TiktokFreePreviewEpisodes : 3;
        GenreCountBox.Value = TikTokPublishOptions.NormalizeGenreCount(p.TiktokGenreCount);
        UploadStallBox.Value = p.TiktokUploadStallSeconds;
        ProjectConcurrencyBox.Value = p.TiktokProjectConcurrency;
        UploadBatchSizeBox.Value = p.TiktokUploadBatchSize;
        UploadBatchStallBox.Value = p.TiktokUploadBatchStallSeconds;
        UploadBatchRetriesBox.Value = p.TiktokUploadBatchMaxRetries;
        SelectByTag(ExpectedPriceModeCombo, p.TiktokExpectedFullPriceMode, "manual");
        ExpectedPriceOptionIndexBox.Value = p.TiktokExpectedFullPriceOptionIndex;
        ExpectedPriceValueBox.Text = !string.IsNullOrWhiteSpace(p.TiktokExpectedFullPriceLabel)
            ? p.TiktokExpectedFullPriceLabel
            : p.TiktokExpectedFullPriceValue;
        SilenceValidationBox.IsChecked = p.TiktokSilenceValidationEnabled;
        SilenceThresholdBox.Value = (decimal)p.TiktokSilenceThresholdDb;
        MaxContinuousSilenceSecondsBox.Value = p.TiktokMaxContinuousSilenceSeconds;
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
        p.TiktokArchiveRootDir = ArchiveRootBox.Text?.Trim() ?? "";
        p.TiktokArchiveRootConfigMigrated = true;
        p.LastDownloadWorkspace = DownloadWorkspaceBox.Text?.Trim() ?? "";
        p.TiktokDeleteVideosOnArchive = DeleteVideosOnArchiveBox.IsChecked == true;
        p.TiktokDeleteVideosOnArchiveConfigured = true;

        p.TiktokContractId = ContractIdBox.Text?.Trim() ?? "";
        p.TiktokContractIdMode = TagOf(ContractModeCombo, "manual");
        p.TiktokSubmitAction = NormalizeSubmitAction(TagOf(SubmitActionCombo, "submit"), SubmitEnabledBox.IsChecked == true);
        p.TiktokSubmitEnabled = string.Equals(p.TiktokSubmitAction, "submit", StringComparison.Ordinal);
        p.TiktokUploadBrowserMode = TagOf(UploadBrowserModeCombo, "embedded");
        p.TiktokPlaywrightUploadHeadless = PlaywrightHeadlessBox.IsChecked == true;
        p.TiktokPublishMode = TagOf(PublishModeCombo, "auto_after_review");
        p.TiktokTargetAudienceMode = TagOf(AudienceCombo, "ai_recommend");
        p.TiktokSourceLanguage = TagOf(SourceLanguageCombo, "zh");
        p.TiktokUploadStrategy = TagOf(UploadStrategyCombo, "classic");
        p.TiktokPaidEnabled = PaidEnabledBox.IsChecked == true;
        p.TiktokPaidRatioEnabled = PaidRatioEnabledBox.IsChecked == true;
        p.TiktokPaidRatioPercent = (double)(PaidRatioPercentBox.Value ?? 0);
        p.TiktokIsAiDrama = AiDramaBox.IsChecked == true;
        p.TiktokIsOriginalRightsHolder = OriginalRightsHolderBox.IsChecked == true;
        p.TiktokContentOriginalityType = TagOf(ContentOriginalityCombo, "original");
        p.TiktokCopyrightMaterialTypes = CopyrightMaterials.GetSelectedMaterialTypes();
        p.TiktokUploadAiScriptOutlineWithScreenshots = CopyrightMaterials.UploadAiScriptOutlineWithScreenshots;
        p.TiktokProofDeclarantCompanyName = ProofDeclarantCompanyNameBox.Text?.Trim() ?? "";
        p.TiktokTimestampApplicantName = TimestampApplicantNameBox.Text?.Trim() ?? "";
        p.TiktokProofSealPath = ProofSealPathBox.Text?.Trim() ?? "";
        p.TiktokProofCopyrightCompanyName = ProofCopyrightCompanyNameBox.Text?.Trim() ?? "";
        p.TiktokAiScriptOutlineEpisodeCount = Math.Clamp(
            (int)(AiScriptOutlineEpisodeCountBox.Value ?? TikTokAccountProfile.DefaultAiScriptOutlineEpisodeCount),
            1,
            120);
        p.TiktokProofAccountConfigMigrated = true;
        p.TiktokAiRewriteSynopsis = AiRewriteSynopsisBox.IsChecked == true;
        p.TiktokConsignmentEnabled = ConsignmentBox.IsChecked == true;
        p.TiktokZeroCostAdsEnabled = ZeroCostAdsBox.IsChecked == true;
        p.TiktokDayZeroRoi = TikTokPublishOptions.NormalizeDayZeroRoi(
            (double)(DayZeroRoiBox.Value ?? (decimal)TikTokPublishOptions.DefaultDayZeroRoi));
        p.TiktokAnchorPromotionEnabled = AnchorPromotionBox.IsChecked == true;
        p.TiktokProfilePreviewEpisodes = (int)(ProfilePreviewBox.Value ?? 3);
        p.TiktokFreePreviewEpisodes = (int)(FreePreviewBox.Value ?? 3);
        p.TiktokGenreCount = TikTokPublishOptions.NormalizeGenreCount((int)(GenreCountBox.Value ?? TikTokPublishOptions.DefaultGenreCount));
        p.TiktokUploadStallSeconds = (int)(UploadStallBox.Value ?? 180);
        p.TiktokProjectConcurrency = (int)(ProjectConcurrencyBox.Value ?? 4);
        p.TiktokUploadBatchSize = (int)(UploadBatchSizeBox.Value ?? 3);
        p.TiktokUploadBatchStallSeconds = (int)(UploadBatchStallBox.Value ?? 75);
        p.TiktokUploadBatchMaxRetries = (int)(UploadBatchRetriesBox.Value ?? 3);
        p.TiktokExpectedFullPriceMode = TagOf(ExpectedPriceModeCombo, "manual");
        p.TiktokExpectedFullPriceOptionIndex = (int)(ExpectedPriceOptionIndexBox.Value ?? 1);
        var (priceValue, priceLabel) = NormalizeExpectedPriceInput(ExpectedPriceValueBox.Text);
        p.TiktokExpectedFullPriceValue = priceValue;
        p.TiktokExpectedFullPriceLabel = priceLabel;
        p.TiktokSilenceValidationEnabled = SilenceValidationBox.IsChecked == true;
        p.TiktokSilenceThresholdDb = (double)(SilenceThresholdBox.Value ?? -45);
        p.TiktokMaxContinuousSilenceSeconds = (int)(MaxContinuousSilenceSecondsBox.Value ?? 20);
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

    private async void OnBrowseArchiveRootClick(object? sender, RoutedEventArgs e)
    {
        if (Storage is null) return;
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择当前账号归档目录",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;
        ArchiveRootBox.Text = folder.Path.LocalPath;
    }

    private async void OnBrowseProofSealClick(object? sender, RoutedEventArgs e)
    {
        if (Storage is null) return;
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择公司印章图片",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("印章图片")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"],
                },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            ProofSealPathBox.Text = path;
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

    private static string NormalizeSubmitAction(string? value, bool? legacyEnabled = null)
    {
        var action = (value ?? "").Trim().ToLowerInvariant();
        return action switch
        {
            "none" => "none",
            "submit" => "submit",
            "save" => "save",
            _ => legacyEnabled.HasValue && !legacyEnabled.Value ? "none" : "submit",
        };
    }

    private static (string Value, string Label) NormalizeExpectedPriceInput(string? text)
    {
        var raw = (text ?? "").Trim();
        if (string.IsNullOrEmpty(raw)) return ("", "");

        var totalMatch = Regex.Match(raw, @"\$?(\d+(?:\.\d+)?)");
        var value = totalMatch.Success ? totalMatch.Groups[1].Value : raw;
        var label = raw.Contains("每集", StringComparison.Ordinal) || raw.Contains("/EP", StringComparison.OrdinalIgnoreCase)
            ? raw
            : (raw.StartsWith('$') ? raw : $"${value}");
        return (value, label);
    }
}
