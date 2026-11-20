using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Ui.Views;

public partial class AccountSettingsDialog : Window
{
    private readonly TikTokAccountProfile _profile;
    private readonly Func<TikTokAccountProfile, Task>? _persist;
    private bool _suppressDramaMarketPrompt;
    private int _contractLookupGeneration;

    public AccountSettingsDialog(
        TikTokAccountProfile profile,
        Func<TikTokAccountProfile, Task>? persist = null)
    {
        InitializeComponent();
        _profile = profile;
        _persist = persist;
        PublishAccounts.FetchRequested += (_, _) => _ = FetchPublishAccountsAsync();
        LoadToUi();
    }

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private void LoadToUi()
    {
        _suppressDramaMarketPrompt = true;
        try
        {
            LoadToUiCore();
        }
        finally
        {
            _suppressDramaMarketPrompt = false;
        }
    }

    private void LoadToUiCore()
    {
        var p = _profile;
        NameBox.Text = p.Name;
        NicknameBox.Text = p.TiktokAccountNickname;
        LoginEmailBox.Text = p.TiktokLoginEmail;
        SeriesUrlBox.Text = p.TiktokSeriesUrl;
        WorkspaceBox.Text = !string.IsNullOrWhiteSpace(p.TiktokUploadProfilePath) ? p.TiktokUploadProfilePath : p.LastWorkspace;
        ArchiveRootBox.Text = p.TiktokArchiveRootDir;
        DownloadWorkspaceBox.Text = p.LastDownloadWorkspace;
        DeleteVideosOnArchiveBox.IsChecked = p.TiktokDeleteVideosOnArchive;
        var xingeEnabled = ClientSettingsStore.Load().XingeRemoteEnabled;
        RemoteDispatchBox.IsEnabled = xingeEnabled;
        RemoteDispatchBox.IsChecked = xingeEnabled && p.ParticipatesInXingeRemoteDispatch;

        ContractIdBox.Text = p.TiktokContractId;
        RequestContractAutofill();
        var submitAction = NormalizeSubmitAction(p.TiktokSubmitAction, p.TiktokSubmitEnabled);
        SelectByTag(SubmitActionCombo, submitAction, "submit");
        var browserOption = string.Equals(p.TiktokUploadBrowserMode, "embedded", StringComparison.OrdinalIgnoreCase)
            ? "embedded"
            : p.TiktokPlaywrightUploadHeadless ? "playwright_headless" : "playwright";
        SelectByTag(UploadBrowserModeCombo, browserOption, "playwright_headless");
        SelectByTag(PublishModeCombo, p.TiktokPublishMode, "auto_after_review");
        SelectByTag(AudienceCombo, p.TiktokTargetAudienceMode, "ai_recommend");
        SelectByTag(DramaMarketCombo, TikTokAccountProfile.NormalizeDramaMarket(p.TiktokDramaMarket), TikTokAccountProfile.DramaMarketLocal);
        SelectByTag(
            SourceLanguageCombo,
            TikTokAccountProfile.NormalizeStoredSourceLanguage(p.TiktokSourceLanguage, p.TiktokDramaMarket),
            "zh");
        var upload = TikTokUploadChannels.Resolve(p.TiktokUploadStrategy, p.TiktokPlaywrightVideoDelivery);
        SelectByTag(UploadStrategyCombo, upload.Channel, "api");
        SelectByTag(PlaywrightDeliveryCombo, upload.Delivery, "classic");
        UpdateUploadChannelVisibility();

        PaidEnabledBox.IsChecked = p.TiktokPaidEnabled || p.TiktokPaidRatioEnabled;
        PaidRatioModeBox.IsChecked = p.TiktokPaidRatioEnabled || !p.TiktokPaidEnabled;
        PaidAllModeBox.IsChecked = PaidRatioModeBox.IsChecked != true;
        var paidRatio = p.TiktokPaidRatioPercent;
        PaidRatioPercentBox.Value = double.IsFinite(paidRatio) ? (decimal)Math.Clamp(paidRatio, 0, 100) : 0;
        AiDramaBox.IsChecked = p.TiktokIsAiDrama;
        SelectByTag(ContentCreationTypeCombo, p.TiktokContentCreationType, "original");
        OriginalRightsHolderBox.IsChecked = p.TiktokIsOriginalRightsHolder;
        SelectByTag(ContentOriginalityCombo, p.TiktokContentOriginalityType, "original");
        CopyrightMaterials.Load(
            p.TiktokCopyrightMaterialTypes,
            p.TiktokUploadAiScriptOutlineWithScreenshots,
            p.TiktokUploadSourceInfoRoleVector,
            p.TiktokUploadSourceInfoRoleSceneScreenshot);
        CopyrightMaterials.ProofTemplatePathBox.Text = p.TiktokProofTemplateDocxPath;
        CopyrightMaterials.ProofDeclarantCompanyNameBox.Text = p.TiktokProofDeclarantCompanyName;
        CopyrightMaterials.TimestampApplicantNameBox.Text = p.TiktokTimestampApplicantName;
        CopyrightMaterials.ProofSealPathBox.Text = p.TiktokProofSealPath;
        CopyrightMaterials.ProofCopyrightCompanyNameBox.Text = p.TiktokProofCopyrightCompanyName;
        CopyrightMaterials.EpisodeScriptEpisodeCountBox.Value = Math.Clamp(
            p.TiktokEpisodeScriptEpisodeCount > 0
                ? p.TiktokEpisodeScriptEpisodeCount
                : TikTokAccountProfile.DefaultEpisodeScriptEpisodeCount,
            1,
            120);
        CopyrightMaterials.AiScriptOutlineEpisodeCountBox.Value = Math.Clamp(
            p.TiktokAiScriptOutlineEpisodeCount > 0
                ? p.TiktokAiScriptOutlineEpisodeCount
                : TikTokAccountProfile.DefaultAiScriptOutlineEpisodeCount,
            1,
            120);
        CopyrightMaterials.RoleVectorCharacterCountBox.Value = Math.Clamp(
            p.TiktokRoleVectorCharacterCount > 0
                ? p.TiktokRoleVectorCharacterCount
                : TikTokAccountProfile.DefaultRoleVectorCharacterCount,
            2,
            6);
        CopyrightMaterials.RoleVectorMinimumCharacterCountBox.Value = Math.Clamp(
            p.TiktokRoleVectorMinimumCharacterCount > 0
                ? p.TiktokRoleVectorMinimumCharacterCount
                : TikTokAccountProfile.DefaultRoleVectorMinimumCharacterCount,
            2,
            (int)(CopyrightMaterials.RoleVectorCharacterCountBox.Value ?? TikTokAccountProfile.DefaultRoleVectorCharacterCount));
        AiRewriteSynopsisBox.IsChecked = p.TiktokAiRewriteSynopsis;
        SelectNewTitleRandomness(AiNewTitleRandomnessCombo, p.TiktokAiNewTitleRandomness);
        ConsignmentBox.IsChecked = p.TiktokConsignmentEnabled;
        ZeroCostAdsBox.IsChecked = p.TiktokZeroCostAdsEnabled;
        DayZeroRoiBox.Value = (decimal)TikTokPublishOptions.NormalizeDayZeroRoi(p.TiktokDayZeroRoi);
        AnchorPromotionBox.IsChecked = p.TiktokAnchorPromotionEnabled;
        PublishAccounts.Load(p);
        ProfilePreviewBox.Value = p.TiktokProfilePreviewEpisodes > 0 ? p.TiktokProfilePreviewEpisodes : 3;
        FreePreviewBox.Value = p.TiktokFreePreviewEpisodes > 0 ? p.TiktokFreePreviewEpisodes : 3;
        GenreCountBox.Value = TikTokPublishOptions.NormalizeGenreCount(p.TiktokGenreCount);
        ProjectConcurrencyBox.Value = p.TiktokProjectConcurrency;
        UploadBatchSizeBox.Value = p.TiktokUploadBatchSize;
        UploadBatchStallBox.Value = p.TiktokUploadBatchStallSeconds;
        UploadBatchRetriesBox.Value = p.TiktokUploadBatchMaxRetries;
        ExpectedPriceValueBox.Text = TikTokApiSeriesDocument.ReadPrice(p.TiktokExpectedFullPriceValue) ?? "";
        ProxyEnabledBox.IsChecked = p.TiktokProxyEnabled;
        SelectByTag(ProxyTypeCombo, p.TiktokProxyType, "http");
        ProxyHostBox.Text = p.TiktokProxyHost;
        ProxyPortBox.Value = p.TiktokProxyPort;
        ProxyUsernameBox.Text = p.TiktokProxyUsername;
        ProxyPasswordBox.Text = p.TiktokProxyPassword;
    }

    private void RequestContractAutofill()
    {
        var generation = ++_contractLookupGeneration;
        if (!string.IsNullOrWhiteSpace(_profile.TiktokContractId))
            return;
        _ = FillEmptyContractAsync(generation);
    }

    private async Task FillEmptyContractAsync(int generation)
    {
        TikTokLicenseContractLookupResult result;
        try
        {
            result = await TikTokLicenseContractLookup.TryReadAsync(_profile, null, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => ApplyContractLookup(generation, result));
    }

    private async Task ApplyContractLookup(int generation, TikTokLicenseContractLookupResult result)
    {
        if (generation != _contractLookupGeneration)
            return;
        if (!string.IsNullOrWhiteSpace(ContractIdBox.Text) ||
            !string.IsNullOrWhiteSpace(_profile.TiktokContractId))
            return;
        if (result.Status != TikTokLicenseContractLookupStatus.Single)
        {
            if (result.Status is TikTokLicenseContractLookupStatus.NotLoggedIn
                or TikTokLicenseContractLookupStatus.AlreadyConfigured)
                return;
            if (!string.IsNullOrWhiteSpace(result.Message))
                await InfoDialog.ShowAsync(this, result.Message, "授权合同", 520, 200);
            return;
        }

        if (!TikTokLicenseContractLookup.TryApply(_profile, result.LicenseId))
            return;
        ContractIdBox.Text = _profile.TiktokContractId;
        if (_persist is not null)
            await _persist(_profile);
    }

    protected override void OnClosed(EventArgs e)
    {
        _contractLookupGeneration++;
        base.OnClosed(e);
    }

    private void SaveFromUi()
    {
        var p = _profile;
        p.Name = NameBox.Text?.Trim() ?? p.Name;
        p.TiktokAccountNickname = NicknameBox.Text?.Trim() ?? "";
        p.TiktokLoginEmail = LoginEmailBox.Text?.Trim() ?? "";
        p.TiktokApiSite = TikTokApiSites.Domestic;
        p.TiktokSeriesUrl = TikTokApiSites.AlignConsoleUrl(
            string.IsNullOrWhiteSpace(SeriesUrlBox.Text)
                ? TikTokApiSites.SeriesDraftUrl(p.TiktokApiSite)
                : SeriesUrlBox.Text.Trim(),
            p.TiktokApiSite);
        var workspace = WorkspaceBox.Text?.Trim() ?? "";
        p.TiktokUploadProfilePath = workspace;
        p.LastWorkspace = workspace;
        p.TiktokArchiveRootDir = ArchiveRootBox.Text?.Trim() ?? "";
        p.TiktokArchiveRootConfigMigrated = true;
        p.LastDownloadWorkspace = DownloadWorkspaceBox.Text?.Trim() ?? "";
        p.TiktokDeleteVideosOnArchive = DeleteVideosOnArchiveBox.IsChecked == true;
        if (ClientSettingsStore.Load().XingeRemoteEnabled)
        {
            p.XingeRemoteDispatchConfigured = true;
            p.XingeRemoteDispatchEnabled = RemoteDispatchBox.IsChecked == true;
        }
        p.TiktokDeleteVideosOnArchiveConfigured = true;

        p.TiktokContractId = ContractIdBox.Text?.Trim() ?? "";
        p.TiktokContractIdMode = TikTokPublishConstants.ContractIdModeManual;
        p.TiktokSubmitAction = NormalizeSubmitAction(TagOf(SubmitActionCombo, "submit"));
        p.TiktokSubmitEnabled = string.Equals(p.TiktokSubmitAction, "submit", StringComparison.Ordinal);
        var browserOption = TagOf(UploadBrowserModeCombo, "playwright_headless");
        p.TiktokUploadBrowserMode = browserOption == "embedded" ? "embedded" : "playwright";
        if (browserOption != "embedded")
            p.TiktokPlaywrightUploadHeadless = browserOption == "playwright_headless";
        p.TiktokPublishMode = TagOf(PublishModeCombo, "auto_after_review");
        p.TiktokTargetAudienceMode = TagOf(AudienceCombo, "ai_recommend");
        p.TiktokDramaMarket = TikTokAccountProfile.NormalizeDramaMarket(TagOf(DramaMarketCombo, TikTokAccountProfile.DramaMarketLocal));
        p.TiktokSourceLanguage = TikTokAccountProfile.NormalizeStoredSourceLanguage(
            TagOf(SourceLanguageCombo, "zh"),
            p.TiktokDramaMarket);
        var upload = TikTokUploadChannels.Resolve(TagOf(UploadStrategyCombo, "api"), TagOf(PlaywrightDeliveryCombo, "classic"));
        p.TiktokUploadStrategy = upload.Channel;
        p.TiktokPlaywrightVideoDelivery = upload.Delivery;
        p.TiktokPaidEnabled = PaidEnabledBox.IsChecked == true;
        p.TiktokPaidRatioEnabled = p.TiktokPaidEnabled && PaidRatioModeBox.IsChecked == true;
        p.TiktokPaidRatioPercent = (double)(PaidRatioPercentBox.Value ?? 20);
        p.TiktokIsAiDrama = AiDramaBox.IsChecked == true;
        p.TiktokContentCreationType = TagOf(ContentCreationTypeCombo, "original");
        p.TiktokIsOriginalRightsHolder = OriginalRightsHolderBox.IsChecked == true;
        p.TiktokContentOriginalityType = TagOf(ContentOriginalityCombo, "original");
        p.TiktokCopyrightMaterialTypes = CopyrightMaterials.GetSelectedMaterialTypes();
        p.TiktokUploadAiScriptOutlineWithScreenshots = CopyrightMaterials.UploadAiScriptOutlineWithScreenshots;
        p.TiktokUploadSourceInfoRoleVector = CopyrightMaterials.UploadSourceInfoRoleVector;
        p.TiktokUploadSourceInfoRoleSceneScreenshot = CopyrightMaterials.UploadSourceInfoRoleSceneScreenshot;
        p.TiktokProofTemplateDocxPath = CopyrightMaterials.ProofTemplatePathBox.Text?.Trim() ?? "";
        p.TiktokProofTemplateLanguage = TikTokProofMaterialTemplateProvider.ChineseLanguage;
        p.TiktokProofDeclarantCompanyName = CopyrightMaterials.ProofDeclarantCompanyNameBox.Text?.Trim() ?? "";
        p.TiktokTimestampApplicantName = CopyrightMaterials.TimestampApplicantNameBox.Text?.Trim() ?? "";
        p.TiktokProofSealPath = CopyrightMaterials.ProofSealPathBox.Text?.Trim() ?? "";
        p.TiktokProofCopyrightCompanyName = CopyrightMaterials.ProofCopyrightCompanyNameBox.Text?.Trim() ?? "";
        p.TiktokEpisodeScriptEpisodeCount = Math.Clamp(
            (int)(CopyrightMaterials.EpisodeScriptEpisodeCountBox.Value ?? TikTokAccountProfile.DefaultEpisodeScriptEpisodeCount),
            1,
            120);
        p.TiktokAiScriptOutlineEpisodeCount = Math.Clamp(
            (int)(CopyrightMaterials.AiScriptOutlineEpisodeCountBox.Value ?? TikTokAccountProfile.DefaultAiScriptOutlineEpisodeCount),
            1,
            120);
        p.TiktokRoleVectorCharacterCount = Math.Clamp(
            (int)(CopyrightMaterials.RoleVectorCharacterCountBox.Value ?? TikTokAccountProfile.DefaultRoleVectorCharacterCount),
            2,
            6);
        p.TiktokRoleVectorMinimumCharacterCount = Math.Clamp(
            (int)(CopyrightMaterials.RoleVectorMinimumCharacterCountBox.Value ??
                  TikTokAccountProfile.DefaultRoleVectorMinimumCharacterCount),
            2,
            p.TiktokRoleVectorCharacterCount);
        p.TiktokProofAccountConfigMigrated = true;
        p.TiktokAiRewriteSynopsis = AiRewriteSynopsisBox.IsChecked == true;
        p.TiktokRewriteFollowsSourceLanguage =
            TikTokAccountProfile.RewriteFollowsImportedLanguage(p.TiktokDramaMarket);
        p.TiktokAiNewTitleRandomness = ReadNewTitleRandomness(AiNewTitleRandomnessCombo);
        p.TiktokConsignmentEnabled = ConsignmentBox.IsChecked == true;
        p.TiktokZeroCostAdsEnabled = ZeroCostAdsBox.IsChecked == true;
        p.TiktokDayZeroRoi = TikTokPublishOptions.NormalizeDayZeroRoi(
            (double)(DayZeroRoiBox.Value ?? (decimal)TikTokPublishOptions.DefaultDayZeroRoi));
        p.TiktokAnchorPromotionEnabled = AnchorPromotionBox.IsChecked == true;
        PublishAccounts.WriteTo(p);
        p.TiktokProfilePreviewEpisodes = (int)(ProfilePreviewBox.Value ?? 3);
        p.TiktokFreePreviewEpisodes = (int)(FreePreviewBox.Value ?? 3);
        p.TiktokGenreCount = TikTokPublishOptions.NormalizeGenreCount((int)(GenreCountBox.Value ?? TikTokPublishOptions.DefaultGenreCount));
        p.TiktokProjectConcurrency = (int)(ProjectConcurrencyBox.Value ?? 4);
        p.TiktokUploadBatchSize = (int)(UploadBatchSizeBox.Value ?? 3);
        p.TiktokUploadBatchStallSeconds = (int)(UploadBatchStallBox.Value ?? 75);
        p.TiktokUploadBatchMaxRetries = (int)(UploadBatchRetriesBox.Value ?? 3);
        p.TiktokUploadApiFallbackToBatch = false;
        p.TiktokExpectedFullPriceMode = "manual";
        p.TiktokExpectedFullPriceValue = TikTokApiSeriesDocument.ReadPrice(ExpectedPriceValueBox.Text) ?? "";
        p.TiktokExpectedFullPriceLabel = "";
        p.TiktokProxyEnabled = ProxyEnabledBox.IsChecked == true;
        p.TiktokProxyType = TagOf(ProxyTypeCombo, "http");
        p.TiktokProxyHost = ProxyHostBox.Text?.Trim() ?? "";
        p.TiktokProxyPort = (int)(ProxyPortBox.Value ?? 0);
        p.TiktokProxyUsername = ProxyUsernameBox.Text?.Trim() ?? "";
        p.TiktokProxyPassword = ProxyPasswordBox.Text ?? "";
    }

    private async Task FetchPublishAccountsAsync()
    {
        PublishAccounts.WriteTo(_profile);
        PublishAccounts.SetFetchEnabled(false);
        try
        {
            var accounts = await TikTokPublishAccountSyncService.FetchAsync(_profile, null, CancellationToken.None);
            var retention = TikTokPublishAccountCatalog.Retain(PublishAccounts.ReadSelectedKeys(), accounts);
            _profile.TiktokPublishAccountCatalogJson = TikTokPublishAccountCatalog.Serialize(accounts);
            _profile.TiktokSelectedPublishAccountKeys = retention.KeptKeys.ToList();
            _profile.TiktokCustomPublishAccountsEnabled = true;
            PublishAccounts.Load(_profile);
            if (_persist is not null)
                await _persist(_profile);
            var dropped = retention.DroppedKeys.Count == 0
                ? ""
                : $"{Environment.NewLine}已取消 {retention.DroppedKeys.Count} 个不在最新列表中的勾选。";
            await InfoDialog.ShowAsync(this, $"已获取 {accounts.Count} 个发布账号。{dropped}", "发布账号");
        }
        catch (Exception ex)
        {
            await InfoDialog.ShowAsync(this, ex.Message, "获取发布账号失败", width: 460, height: 180);
        }
        finally
        {
            PublishAccounts.SetFetchEnabled(true);
        }
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

    private async void OnDramaMarketSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressDramaMarketPrompt)
            return;
        if (!string.Equals(
                TagOf(DramaMarketCombo, TikTokAccountProfile.DramaMarketLocal),
                TikTokAccountProfile.DramaMarketOverseas,
                StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.Equals(TagOf(SourceLanguageCombo, "zh"), "zh", StringComparison.OrdinalIgnoreCase))
            return;

        var confirmed = await ConfirmDialog.ShowAsync(
            this,
            "海外剧",
            "当前源语言是中文。海外剧一般使用非中文，是否改为英语？",
            480,
            180,
            confirmText: "改为英语");
        if (!confirmed)
            return;

        SelectByTag(SourceLanguageCombo, "en", "zh");
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

    private static void SelectNewTitleRandomness(ComboBox combo, string? value)
    {
        var key = ClientSettingsDefaults.NormalizeAiNewTitleRandomness(value);
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, key, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        if (combo.ItemCount > 0)
            combo.SelectedIndex = 1;
    }

    private static string ReadNewTitleRandomness(ComboBox combo) =>
        ClientSettingsDefaults.NormalizeAiNewTitleRandomness((combo.SelectedItem as ComboBoxItem)?.Tag as string);

    private void UploadStrategyCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateUploadChannelVisibility();

    private void UpdateUploadChannelVisibility()
    {
        if (PlaywrightDeliveryPanel is null)
            return;

        var api = string.Equals(TagOf(UploadStrategyCombo, "api"), TikTokUploadChannels.Api, StringComparison.OrdinalIgnoreCase);
        PlaywrightDeliveryPanel.IsVisible = !api;
        BatchSizeLabel.IsVisible = !api;
        UploadBatchSizeBox.IsVisible = !api;
        BatchStallLabel.IsVisible = !api;
        UploadBatchStallBox.IsVisible = !api;
        BatchRetriesLabel.IsVisible = !api;
        UploadBatchRetriesBox.IsVisible = !api;
    }
}
