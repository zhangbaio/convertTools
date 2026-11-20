using System.Diagnostics;
using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public partial class AccountProfileEditor : UserControl
{
    private MainViewModel? _vm;
    private SystemServicesViewModel? _systemServices;
    private bool _isReloadingFromSelectedAccount;
    private bool _persistedRemoteDispatch;
    private bool _suppressUploadedSeriesSearch;
    private int _uploadedSeriesGeneration;
    private int _contractLookupGeneration;
    private int _profileReloadSuppressions;
    private IReadOnlyList<AccountUploadedSeriesRow> _uploadedSeriesRows = Array.Empty<AccountUploadedSeriesRow>();

    public event EventHandler? LoginRequested;
    public event EventHandler? ReloginRequested;
    public event EventHandler? LogoutRequested;

    public AccountProfileEditor()
    {
        InitializeComponent();
        PublishForm.Children.Remove(OriginalRightsHolderBox);
        PublishAccounts.UseInlineLayout(OriginalRightsHolderBox);
        PublishAccounts.FetchRequested += (_, _) => _ = FetchPublishAccountsAsync();
        VideoDedupEditor.ApplyToAllAccountsRequested += (_, _) => _ = ApplyDedupPresetToAllAccountsAsync();
        DataContextChanged += (_, _) => ReloadFromSelectedAccount();
    }

    public void Bind(MainViewModel vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.AccountProfileNetworkChanged -= OnAccountProfileChanged;
        }

        if (_systemServices is not null)
            _systemServices.PropertyChanged -= OnSystemServicesPropertyChanged;

        _vm = vm;
        _systemServices = vm.SystemServices;
        DataContext = vm;
        ReloadFromSelectedAccount();
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.AccountProfileNetworkChanged += OnAccountProfileChanged;
        _systemServices.PropertyChanged += OnSystemServicesPropertyChanged;
    }

    public void RefreshSelectedAccount() => ReloadFromSelectedAccount();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedAccount))
            ReloadFromSelectedAccount();
        else if (e.PropertyName == nameof(MainViewModel.WorkspacePath))
            SyncWorkspaceBoxFromSelectedAccount();
    }

    private void OnSystemServicesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(SystemServicesViewModel.XingeRemoteEnabled))
            ApplyRemoteDispatchAvailability();
    }

    private void ShowPersistedRemoteDispatch(TikTokAccountProfile? profile)
    {
        _persistedRemoteDispatch = profile?.ParticipatesInXingeRemoteDispatch == true;
        ApplyRemoteDispatchAvailability();
    }

    private void ApplyRemoteDispatchAvailability()
    {
        var xingeEnabled = _vm?.SystemServices.XingeRemoteEnabled == true;
        RemoteDispatchBox.IsEnabled = xingeEnabled;
        RemoteDispatchBox.IsChecked = xingeEnabled && _persistedRemoteDispatch;
    }

    private void RequestContractAutofill(TikTokAccountProfile profile)
    {
        var generation = ++_contractLookupGeneration;
        if (!string.IsNullOrWhiteSpace(profile.TiktokContractId))
            return;
        _ = FillEmptyContractAsync(profile, generation);
    }

    private async Task FillEmptyContractAsync(TikTokAccountProfile profile, int generation)
    {
        TikTokLicenseContractLookupResult result;
        try
        {
            result = await TikTokLicenseContractLookup.TryReadAsync(
                profile,
                message => _vm?.AppendLog(message),
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => ApplyContractLookup(profile, generation, result));
    }

    private void ApplyContractLookup(
        TikTokAccountProfile profile,
        int generation,
        TikTokLicenseContractLookupResult result)
    {
        if (generation != _contractLookupGeneration)
            return;
        if (_vm?.SelectedAccount?.Id != profile.Id)
            return;
        if (!string.IsNullOrWhiteSpace(ContractIdBox.Text) ||
            !string.IsNullOrWhiteSpace(profile.TiktokContractId))
            return;
        if (result.Status != TikTokLicenseContractLookupStatus.Single)
        {
            if (result.Status is TikTokLicenseContractLookupStatus.NotLoggedIn
                or TikTokLicenseContractLookupStatus.AlreadyConfigured)
                return;
            if (!string.IsNullOrWhiteSpace(result.Message))
                _ = InfoDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, result.Message, "授权合同", 520, 200);
            return;
        }

        if (!TikTokLicenseContractLookup.TryApply(profile, result.LicenseId))
            return;
        ContractIdBox.Text = profile.TiktokContractId;
        if (_vm is null)
            return;
        _profileReloadSuppressions++;
        try
        {
            _vm.SaveAccountProfile(profile);
        }
        finally
        {
            _profileReloadSuppressions--;
        }

        var saved = TikTokLicenseContractLookup.SavedLog(profile.TiktokContractId);
        _vm.AppendLog(saved);
        _vm.StatusMessage = saved;
    }

    private void OnAccountProfileChanged(TikTokAccountProfile profile)
    {
        if (_profileReloadSuppressions > 0)
            return;
        if (_vm?.SelectedAccount?.Id == profile.Id)
            ReloadFromSelectedAccount();
    }

    private void ReloadFromSelectedAccount()
    {
        if (_isReloadingFromSelectedAccount)
            return;

        _isReloadingFromSelectedAccount = true;
        try
        {
            ReloadFromSelectedAccountCore();
        }
        finally
        {
            _isReloadingFromSelectedAccount = false;
        }
    }

    private void ReloadFromSelectedAccountCore()
    {
        SetPasswordVisibility(false);
        var profile = _vm?.SelectedAccount?.Model;
        if (profile is null)
        {
            ClearFields();
            LoginStatusText.Text = "尚未登录";
            ShowPersistedRemoteDispatch(null);
            RequestUploadedSeriesReload();
            return;
        }

        LoginStatusText.Text = AccountLoginStatusHelper.Describe(profile);
        NicknameBox.Text = profile.TiktokAccountNickname;
        LoginEmailBox.Text = profile.TiktokLoginEmail;
        PasswordBox.Text = profile.TiktokLoginPassword;
        ShowPersistedRemoteDispatch(profile);
        SeriesUrlBox.Text = profile.TiktokSeriesUrl;
        WorkspaceBox.Text = !string.IsNullOrWhiteSpace(profile.TiktokUploadProfilePath)
            ? profile.TiktokUploadProfilePath
            : profile.LastWorkspace;
        ArchiveRootBox.Text = profile.TiktokArchiveRootDir;
        DownloadWorkspaceBox.Text = profile.LastDownloadWorkspace;
        ExcelReportBox.Text = profile.TiktokExcelReportPath;
        DeleteVideosOnArchiveBox.IsChecked = profile.TiktokDeleteVideosOnArchive;

        LegacyCdpSettings.IsVisible = string.Equals(profile.TiktokLoginBrowserMode, "cdp", StringComparison.OrdinalIgnoreCase);
        CdpEndpointBox.Text = profile.TiktokExternalBrowserCdpEndpoint;

        ContractIdBox.Text = profile.TiktokContractId;
        RequestContractAutofill(profile);
        var submitAction = NormalizeSubmitAction(profile.TiktokSubmitAction, profile.TiktokSubmitEnabled);
        SelectByTag(SubmitActionCombo, submitAction, "submit");
        var browserOption = string.Equals(profile.TiktokUploadBrowserMode, "embedded", StringComparison.OrdinalIgnoreCase)
            ? "embedded"
            : profile.TiktokPlaywrightUploadHeadless ? "playwright_headless" : "playwright";
        SelectByTag(UploadBrowserModeCombo, browserOption, "playwright_headless");
        SelectByTag(PublishModeCombo, profile.TiktokPublishMode, "auto_after_review");
        SelectByTag(AudienceCombo, profile.TiktokTargetAudienceMode, "ai_recommend");
        SelectByTag(DramaMarketCombo, TikTokAccountProfile.NormalizeDramaMarket(profile.TiktokDramaMarket), TikTokAccountProfile.DramaMarketLocal);
        SelectByTag(
            SourceLanguageCombo,
            TikTokAccountProfile.NormalizeStoredSourceLanguage(profile.TiktokSourceLanguage, profile.TiktokDramaMarket),
            "zh");
        var upload = TikTokUploadChannels.Resolve(profile.TiktokUploadStrategy, profile.TiktokPlaywrightVideoDelivery);
        SelectByTag(UploadStrategyCombo, upload.Channel, "api");
        SelectByTag(PlaywrightDeliveryCombo, upload.Delivery, "classic");
        UpdateUploadChannelVisibility();

        PaidEnabledBox.IsChecked = profile.TiktokPaidEnabled || profile.TiktokPaidRatioEnabled;
        PaidRatioModeBox.IsChecked = profile.TiktokPaidRatioEnabled || !profile.TiktokPaidEnabled;
        PaidAllModeBox.IsChecked = PaidRatioModeBox.IsChecked != true;
        var paidRatio = profile.TiktokPaidRatioPercent;
        PaidRatioPercentBox.Value = double.IsFinite(paidRatio) ? (decimal)Math.Clamp(paidRatio, 0, 100) : 0;
        AiDramaBox.IsChecked = profile.TiktokIsAiDrama;
        SelectByTag(ContentCreationTypeCombo, profile.TiktokContentCreationType, "original");
        OriginalRightsHolderBox.IsChecked = profile.TiktokIsOriginalRightsHolder;
        SelectByTag(ContentOriginalityCombo, profile.TiktokContentOriginalityType, "original");
        CopyrightMaterials.Load(
            profile.TiktokCopyrightMaterialTypes,
            profile.TiktokUploadAiScriptOutlineWithScreenshots,
            profile.TiktokUploadSourceInfoRoleVector,
            profile.TiktokUploadSourceInfoRoleSceneScreenshot);
        CopyrightMaterials.ProofTemplatePathBox.Text = profile.TiktokProofTemplateDocxPath;
        CopyrightMaterials.ProofDeclarantCompanyNameBox.Text = profile.TiktokProofDeclarantCompanyName;
        CopyrightMaterials.TimestampApplicantNameBox.Text = profile.TiktokTimestampApplicantName;
        CopyrightMaterials.ProofSealPathBox.Text = profile.TiktokProofSealPath;
        CopyrightMaterials.ProofCopyrightCompanyNameBox.Text = profile.TiktokProofCopyrightCompanyName;
        CopyrightMaterials.EpisodeScriptEpisodeCountBox.Value = Math.Clamp(
            profile.TiktokEpisodeScriptEpisodeCount > 0
                ? profile.TiktokEpisodeScriptEpisodeCount
                : TikTokAccountProfile.DefaultEpisodeScriptEpisodeCount,
            1,
            120);
        CopyrightMaterials.AiScriptOutlineEpisodeCountBox.Value = Math.Clamp(
            profile.TiktokAiScriptOutlineEpisodeCount > 0
                ? profile.TiktokAiScriptOutlineEpisodeCount
                : TikTokAccountProfile.DefaultAiScriptOutlineEpisodeCount,
            1,
            120);
        CopyrightMaterials.RoleVectorCharacterCountBox.Value = Math.Clamp(
            profile.TiktokRoleVectorCharacterCount > 0
                ? profile.TiktokRoleVectorCharacterCount
                : TikTokAccountProfile.DefaultRoleVectorCharacterCount,
            2,
            6);
        CopyrightMaterials.RoleVectorMinimumCharacterCountBox.Value = Math.Clamp(
            profile.TiktokRoleVectorMinimumCharacterCount > 0
                ? profile.TiktokRoleVectorMinimumCharacterCount
                : TikTokAccountProfile.DefaultRoleVectorMinimumCharacterCount,
            2,
            (int)(CopyrightMaterials.RoleVectorCharacterCountBox.Value ?? TikTokAccountProfile.DefaultRoleVectorCharacterCount));
        AiRewriteSynopsisBox.IsChecked = profile.TiktokAiRewriteSynopsis;
        SelectByTag(AiNewTitleRandomnessCombo, profile.TiktokAiNewTitleRandomness, ClientSettingsDefaults.AiNewTitleRandomness);
        ConsignmentBox.IsChecked = profile.TiktokConsignmentEnabled;
        ZeroCostAdsBox.IsChecked = profile.TiktokZeroCostAdsEnabled;
        DayZeroRoiBox.Value = (decimal)TikTokPublishOptions.NormalizeDayZeroRoi(profile.TiktokDayZeroRoi);
        AnchorPromotionBox.IsChecked = profile.TiktokAnchorPromotionEnabled;
        PublishAccounts.Load(profile);
        ProfilePreviewBox.Value = profile.TiktokProfilePreviewEpisodes > 0 ? profile.TiktokProfilePreviewEpisodes : 3;
        FreePreviewBox.Value = profile.TiktokFreePreviewEpisodes > 0 ? profile.TiktokFreePreviewEpisodes : 3;
        GenreCountBox.Value = TikTokPublishOptions.NormalizeGenreCount(profile.TiktokGenreCount);
        ProjectConcurrencyBox.Value = profile.TiktokProjectConcurrency;
        UploadBatchSizeBox.Value = profile.TiktokUploadBatchSize;
        UploadBatchStallBox.Value = profile.TiktokUploadBatchStallSeconds;
        UploadBatchRetriesBox.Value = profile.TiktokUploadBatchMaxRetries;

        ExpectedPriceValueBox.Text = TikTokApiSeriesDocument.ReadPrice(profile.TiktokExpectedFullPriceValue) ?? "";

        ProxyEnabledBox.IsChecked = profile.TiktokProxyEnabled;
        SelectByTag(ProxyTypeCombo, profile.TiktokProxyType, "http");
        ProxyHostBox.Text = profile.TiktokProxyHost;
        ProxyPortBox.Value = profile.TiktokProxyPort;
        ProxyUsernameBox.Text = profile.TiktokProxyUsername;
        ProxyPasswordBox.Text = profile.TiktokProxyPassword;
        ProxyLabelBox.Text = profile.TiktokProxyLabel;
        StaticIpNoteBox.Text = profile.TiktokStaticIpNote;
        VideoDedupEditor.Load(profile.VideoDedupPreset);
        RequestUploadedSeriesReload();
    }

    private void SyncWorkspaceBoxFromSelectedAccount()
    {
        var profile = _vm?.SelectedAccount?.Model;
        var workspace = profile is null
            ? ""
            : !string.IsNullOrWhiteSpace(profile.TiktokUploadProfilePath)
                ? profile.TiktokUploadProfilePath
                : profile.LastWorkspace;

        if (!string.Equals(WorkspaceBox.Text ?? "", workspace, StringComparison.Ordinal))
            WorkspaceBox.Text = workspace;
    }

    private bool SaveToProfile()
    {
        var profile = _vm?.SelectedAccount?.Model;
        if (profile is null || _vm is null)
        {
            if (_vm is not null)
                _vm.StatusMessage = "请先在左侧选择一个账号";
            return false;
        }

        try
        {
            profile.TiktokAccountNickname = NicknameBox.Text?.Trim() ?? "";
            profile.TiktokLoginEmail = LoginEmailBox.Text?.Trim() ?? "";
            profile.TiktokLoginPassword = PasswordBox.Text ?? "";
            if (_vm.SystemServices.XingeRemoteEnabled)
            {
                profile.XingeRemoteDispatchConfigured = true;
                profile.XingeRemoteDispatchEnabled = RemoteDispatchBox.IsChecked == true;
                _persistedRemoteDispatch = profile.XingeRemoteDispatchEnabled;
            }
            profile.TiktokApiSite = TikTokApiSites.Domestic;
            profile.TiktokSeriesUrl = TikTokApiSites.AlignConsoleUrl(
                string.IsNullOrWhiteSpace(SeriesUrlBox.Text)
                    ? TikTokApiSites.SeriesDraftUrl(profile.TiktokApiSite)
                    : SeriesUrlBox.Text.Trim(),
                profile.TiktokApiSite);

            var workspace = WorkspaceBox.Text?.Trim() ?? "";
            profile.TiktokUploadProfilePath = workspace;
            profile.LastWorkspace = workspace;
            profile.TiktokArchiveRootDir = ArchiveRootBox.Text?.Trim() ?? "";
            profile.TiktokArchiveRootConfigMigrated = true;
            profile.LastDownloadWorkspace = DownloadWorkspaceBox.Text?.Trim() ?? "";
            profile.TiktokExcelReportPath = ExcelReportBox.Text?.Trim() ?? "";
            profile.TiktokDeleteVideosOnArchive = DeleteVideosOnArchiveBox.IsChecked == true;
            profile.TiktokDeleteVideosOnArchiveConfigured = true;

            if (LegacyCdpSettings.IsVisible)
                profile.TiktokExternalBrowserCdpEndpoint = CdpEndpointBox.Text?.Trim() ?? "";

            profile.TiktokContractId = ContractIdBox.Text?.Trim() ?? "";
            profile.TiktokContractIdMode = TikTokPublishConstants.ContractIdModeManual;
            profile.TiktokSubmitAction = NormalizeSubmitAction(TagOf(SubmitActionCombo, "submit"));
            profile.TiktokSubmitEnabled = string.Equals(profile.TiktokSubmitAction, "submit", StringComparison.Ordinal);
            var browserOption = TagOf(UploadBrowserModeCombo, "playwright_headless");
            profile.TiktokUploadBrowserMode = browserOption == "embedded" ? "embedded" : "playwright";
            if (browserOption != "embedded")
                profile.TiktokPlaywrightUploadHeadless = browserOption == "playwright_headless";
            profile.TiktokPublishMode = TagOf(PublishModeCombo, "auto_after_review");
            profile.TiktokTargetAudienceMode = TagOf(AudienceCombo, "ai_recommend");
            profile.TiktokDramaMarket = TikTokAccountProfile.NormalizeDramaMarket(TagOf(DramaMarketCombo, TikTokAccountProfile.DramaMarketLocal));
            profile.TiktokSourceLanguage = TikTokAccountProfile.NormalizeStoredSourceLanguage(
                TagOf(SourceLanguageCombo, "zh"),
                profile.TiktokDramaMarket);
            var upload = TikTokUploadChannels.Resolve(TagOf(UploadStrategyCombo, "api"), TagOf(PlaywrightDeliveryCombo, "classic"));
            profile.TiktokUploadStrategy = upload.Channel;
            profile.TiktokPlaywrightVideoDelivery = upload.Delivery;
            profile.TiktokPaidEnabled = PaidEnabledBox.IsChecked == true;
            profile.TiktokPaidRatioEnabled = profile.TiktokPaidEnabled && PaidRatioModeBox.IsChecked == true;
            profile.TiktokPaidRatioPercent = (double)(PaidRatioPercentBox.Value ?? 20);
            profile.TiktokIsAiDrama = AiDramaBox.IsChecked == true;
            profile.TiktokContentCreationType = TagOf(ContentCreationTypeCombo, "original");
            profile.TiktokIsOriginalRightsHolder = OriginalRightsHolderBox.IsChecked == true;
            profile.TiktokContentOriginalityType = TagOf(ContentOriginalityCombo, "original");
            profile.TiktokCopyrightMaterialTypes = CopyrightMaterials.GetSelectedMaterialTypes();
            profile.TiktokUploadAiScriptOutlineWithScreenshots = CopyrightMaterials.UploadAiScriptOutlineWithScreenshots;
            profile.TiktokUploadSourceInfoRoleVector = CopyrightMaterials.UploadSourceInfoRoleVector;
            profile.TiktokUploadSourceInfoRoleSceneScreenshot = CopyrightMaterials.UploadSourceInfoRoleSceneScreenshot;
            profile.TiktokProofTemplateDocxPath = CopyrightMaterials.ProofTemplatePathBox.Text?.Trim() ?? "";
            profile.TiktokProofTemplateLanguage = TikTokProofMaterialTemplateProvider.ChineseLanguage;
            profile.TiktokProofDeclarantCompanyName = CopyrightMaterials.ProofDeclarantCompanyNameBox.Text?.Trim() ?? "";
            profile.TiktokTimestampApplicantName = CopyrightMaterials.TimestampApplicantNameBox.Text?.Trim() ?? "";
            profile.TiktokProofSealPath = CopyrightMaterials.ProofSealPathBox.Text?.Trim() ?? "";
            profile.TiktokProofCopyrightCompanyName = CopyrightMaterials.ProofCopyrightCompanyNameBox.Text?.Trim() ?? "";
            profile.TiktokEpisodeScriptEpisodeCount = Math.Clamp(
                (int)(CopyrightMaterials.EpisodeScriptEpisodeCountBox.Value ?? TikTokAccountProfile.DefaultEpisodeScriptEpisodeCount),
                1,
                120);
            profile.TiktokAiScriptOutlineEpisodeCount = Math.Clamp(
                (int)(CopyrightMaterials.AiScriptOutlineEpisodeCountBox.Value ?? TikTokAccountProfile.DefaultAiScriptOutlineEpisodeCount),
                1,
                120);
            profile.TiktokRoleVectorCharacterCount = Math.Clamp(
                (int)(CopyrightMaterials.RoleVectorCharacterCountBox.Value ?? TikTokAccountProfile.DefaultRoleVectorCharacterCount),
                2,
                6);
            profile.TiktokRoleVectorMinimumCharacterCount = Math.Clamp(
                (int)(CopyrightMaterials.RoleVectorMinimumCharacterCountBox.Value ??
                      TikTokAccountProfile.DefaultRoleVectorMinimumCharacterCount),
                2,
                profile.TiktokRoleVectorCharacterCount);
            profile.TiktokProofAccountConfigMigrated = true;
            profile.TiktokAiRewriteSynopsis = AiRewriteSynopsisBox.IsChecked == true;
            profile.TiktokRewriteFollowsSourceLanguage =
                TikTokAccountProfile.RewriteFollowsImportedLanguage(profile.TiktokDramaMarket);
            profile.TiktokAiNewTitleRandomness = TagOf(AiNewTitleRandomnessCombo, ClientSettingsDefaults.AiNewTitleRandomness);
            profile.TiktokConsignmentEnabled = ConsignmentBox.IsChecked == true;
            profile.TiktokZeroCostAdsEnabled = ZeroCostAdsBox.IsChecked == true;
            profile.TiktokDayZeroRoi = TikTokPublishOptions.NormalizeDayZeroRoi(
                (double)(DayZeroRoiBox.Value ?? (decimal)TikTokPublishOptions.DefaultDayZeroRoi));
            profile.TiktokAnchorPromotionEnabled = AnchorPromotionBox.IsChecked == true;
            PublishAccounts.WriteTo(profile);
            profile.TiktokProfilePreviewEpisodes = (int)(ProfilePreviewBox.Value ?? 3);
            profile.TiktokFreePreviewEpisodes = (int)(FreePreviewBox.Value ?? 3);
            profile.TiktokGenreCount = TikTokPublishOptions.NormalizeGenreCount((int)(GenreCountBox.Value ?? TikTokPublishOptions.DefaultGenreCount));
            profile.TiktokProjectConcurrency = (int)(ProjectConcurrencyBox.Value ?? 4);
            profile.TiktokUploadBatchSize = (int)(UploadBatchSizeBox.Value ?? 3);
            profile.TiktokUploadBatchStallSeconds = (int)(UploadBatchStallBox.Value ?? 75);
            profile.TiktokUploadBatchMaxRetries = (int)(UploadBatchRetriesBox.Value ?? 3);
            profile.TiktokUploadApiFallbackToBatch = false;
            profile.TiktokExpectedFullPriceMode = "manual";
            profile.TiktokExpectedFullPriceValue = TikTokApiSeriesDocument.ReadPrice(ExpectedPriceValueBox.Text) ?? "";
            profile.TiktokExpectedFullPriceLabel = "";

            profile.TiktokProxyEnabled = ProxyEnabledBox.IsChecked == true;
            profile.TiktokProxyType = TagOf(ProxyTypeCombo, "http");
            profile.TiktokProxyHost = ProxyHostBox.Text?.Trim() ?? "";
            profile.TiktokProxyPort = (int)(ProxyPortBox.Value ?? 0);
            profile.TiktokProxyUsername = ProxyUsernameBox.Text?.Trim() ?? "";
            profile.TiktokProxyPassword = ProxyPasswordBox.Text ?? "";
            profile.TiktokProxyLabel = ProxyLabelBox.Text?.Trim() ?? "";
            profile.TiktokStaticIpNote = StaticIpNoteBox.Text?.Trim() ?? "";
            profile.VideoDedupPreset = VideoDedupEditor.Read();

            _vm.SaveAccountProfile(profile);
            _vm.SelectedAccount?.RefreshFromModel();
            _vm.RefreshFilteredAccounts();
            _vm.StatusMessage = $"已保存账号「{profile.DisplayName}」配置";
            ReloadFromSelectedAccount();
            return true;
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"保存账号配置失败：{ex.Message}";
            _vm.AppendLog($"保存账号配置失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private async Task ApplyDedupPresetToAllAccountsAsync()
    {
        if (_vm is null)
            return;

        var accounts = _vm.Accounts.Select(item => item.Model).ToList();
        if (accounts.Count == 0)
        {
            _vm.StatusMessage = "没有账号可以同步视频去重方案。";
            return;
        }

        VideoDedupPreset preset;
        try
        {
            preset = VideoDedupEditor.Read();
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"读取视频去重方案失败：{ex.Message}";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await ConfirmDialog.ShowAsync(
            owner,
            "同步视频去重",
            $"把当前视频去重方案写到全部 {accounts.Count} 个账号？登录、代理、发布和上传配置不变。已经去重过的成片不会马上重做。",
            520,
            220);
        if (!confirmed)
            return;

        preset.CopyOnto(accounts);
        foreach (var account in accounts)
            _vm.SaveAccountProfile(account);

        _vm.StatusMessage = $"已把视频去重方案同步到 {accounts.Count} 个账号。";
    }

    private async void OnDramaMarketSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isReloadingFromSelectedAccount)
            return;
        if (!string.Equals(
                TagOf(DramaMarketCombo, TikTokAccountProfile.DramaMarketLocal),
                TikTokAccountProfile.DramaMarketOverseas,
                StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.Equals(TagOf(SourceLanguageCombo, "zh"), "zh", StringComparison.OrdinalIgnoreCase))
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await ConfirmDialog.ShowAsync(
            owner,
            "海外剧",
            "当前源语言是中文。海外剧一般使用非中文，是否改为英语？",
            480,
            180,
            confirmText: "改为英语");
        if (!confirmed)
            return;

        SelectByTag(SourceLanguageCombo, "en", "zh");
    }

    private async void OnSyncAccountsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_vm is null || !SaveToProfile())
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var direction = await AccountConfigSyncDirectionDialog.ShowAsync(owner);
        if (direction is null)
            return;

        _vm.AccountConfigSyncStatus = "正在读取管理系统中的账号配置…";
        var accounts = _vm.Accounts.Select(item => item.Model).ToArray();
        var state = LicenseStore.Load();
        var ttUser = FirstNonEmpty(state.AccountUsername, state.Email, state.LicenseKey);
        var context = TikTokAccountConfigManualSync.CreateMachineContext(
            _vm.SystemSettings.TiktokSyncWorkspaceRoot,
            _vm.IsAccountQueueBusy);
        var client = new TikTokAccountConfigSyncService();
        var (preview, error) = await TikTokAccountConfigManualSync.LoadPreviewAsync(
            accounts,
            direction.Value,
            ttUser,
            client,
            context,
            _vm.IsAccountQueueBusy,
            CancellationToken.None);
        if (preview is null)
        {
            _vm.AccountConfigSyncStatus = error;
            await InfoDialog.ShowAsync(owner, error, "同步账号配置", 460, 180);
            return;
        }

        var refreshed = false;
        while (true)
        {
            if (preview.Rows.Count == 0)
            {
                var sameMessage = $"当前 TT 用户「{(string.IsNullOrWhiteSpace(ttUser) ? "未登录" : ttUser)}」和本机有 {preview.UnchangedCount} 个一致的账号，没有需要处理的差异。";
                _vm.AccountConfigSyncStatus = "账号配置一致，未写入";
                await InfoDialog.ShowAsync(owner, sameMessage, "同步账号配置", 460, 180);
                return;
            }

            var accepted = await AccountConfigSyncDialog.ShowAsync(owner, preview, refreshed);
            if (!accepted)
            {
                _vm.AccountConfigSyncStatus = "已取消账号配置同步";
                return;
            }

            _vm.AccountConfigSyncStatus = "正在同步账号配置…";
            var result = await TikTokAccountConfigManualSync.ApplyAsync(
                _vm.Accounts.Select(item => item.Model).ToArray(),
                preview,
                client,
                context,
                _vm.IsAccountQueueBusy,
                CancellationToken.None);
            if (result.Stale && result.Preview is not null)
            {
                preview = result.Preview;
                refreshed = true;
                continue;
            }

            if (result.AccountsChanged)
                _vm.ApplySyncedAccounts(result.Accounts);
            _vm.AccountConfigSyncStatus = result.Message;
            _vm.AppendLog(result.Message);
            await InfoDialog.ShowAsync(owner, result.Message, "同步账号配置", 460, 180);
            return;
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!SaveToProfile()) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        await InfoDialog.ShowSaveSuccessAsync(owner, "账号配置已保存成功。");
    }

    private void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!SaveToProfile()) return;
        if (_vm is not null)
            _vm.StatusMessage = "已保存账号配置，正在打开内置浏览器…";
        LoginRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnReloginClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!SaveToProfile()) return;
        if (_vm is not null)
            _vm.StatusMessage = "已保存账号配置，正在重新打开内置浏览器…";
        ReloginRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!SaveToProfile()) return;
        LogoutRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnTestProxyClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;

        var enabled = ProxyEnabledBox.IsChecked == true;
        var type = TagOf(ProxyTypeCombo, "http");
        var host = ProxyHostBox.Text?.Trim() ?? "";
        var port = (int)(ProxyPortBox.Value ?? 0);
        var user = ProxyUsernameBox.Text?.Trim() ?? "";
        var pass = ProxyPasswordBox.Text ?? "";

        TestProxyButton.IsEnabled = false;
        ProxyTestResultText.Foreground = Brushes.Gray;
        ProxyTestResultText.Text = "正在测试，请稍候…";
        try
        {
            var (ok, message) = await TestProxyAsync(enabled, type, host, port, user, pass);
            ProxyTestResultText.Foreground = ok ? Brushes.SeaGreen : Brushes.IndianRed;
            ProxyTestResultText.Text = message;
        }
        catch (Exception ex)
        {
            ProxyTestResultText.Foreground = Brushes.IndianRed;
            ProxyTestResultText.Text = $"测试失败：{ex.Message}";
        }
        finally
        {
            TestProxyButton.IsEnabled = true;
        }
    }

    private static async Task<(bool Ok, string Message)> TestProxyAsync(
        bool enabled, string type, string host, int port, string user, string pass)
    {
        using var handler = new HttpClientHandler();
        string modeDesc;
        if (enabled)
        {
            if (string.IsNullOrEmpty(host))
                return (false, "已勾选「启用账号代理」，但未填写代理主机。");

            var scheme = TikTokProxyHelper.NormalizeProxyType(type);
            var server = host.Contains("://", StringComparison.Ordinal)
                ? host
                : port > 0 ? $"{scheme}://{host}:{port}" : $"{scheme}://{host}";

            var proxy = new WebProxy(server);
            if (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(pass))
                proxy.Credentials = new NetworkCredential(user, pass);
            handler.Proxy = proxy;
            handler.UseProxy = true;
            modeDesc = $"经代理 {server}";
        }
        else
        {
            handler.UseProxy = false;
            modeDesc = "直连（未启用代理）";
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 TikTokPublisher");

        var result = await LookupOutboundIpAsync(client);
        var ip = string.IsNullOrWhiteSpace(result.Ip) ? "未知" : result.Ip;
        var location = string.IsNullOrWhiteSpace(result.Location) ? "未知" : ToChineseIpLocation(result.Location);
        var org = string.IsNullOrWhiteSpace(result.Org) ? "未知" : ToChineseIpOrg(result.Org);
        var text = $"✓ 出口 IP：{ip}（归属地：{location}）\n"
                 + $"运营商：{org}\n"
                 + $"方式：{modeDesc}";
        return (true, text);
    }

    private static async Task<IpLookupResult> LookupOutboundIpAsync(HttpClient client)
    {
        Exception? lastError = null;
        var results = new List<IpLookupResult>();

        foreach (var url in new[] { "https://ip9.com.cn/get", "https://ipinfo.io/json", "https://ipwho.is/" })
        {
            try
            {
                using var resp = await client.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var result = url.Contains("ip9.com.cn", StringComparison.OrdinalIgnoreCase)
                    ? ParseIp9(root)
                    : url.Contains("ipwho.is", StringComparison.OrdinalIgnoreCase)
                        ? ParseIpWhoIs(root)
                        : ParseIpInfo(root);
                if (!string.IsNullOrWhiteSpace(result.Ip))
                    results.Add(result);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        var primary = results.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.Ip));
        if (primary is not null)
        {
            var matchingResults = results
                .Where(result => string.Equals(result.Ip, primary.Ip, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matchingResults.Count == 0)
                matchingResults = results;

            var location = FirstNonEmpty(matchingResults.Select(result => result.Location).ToArray());
            var org = FirstNonEmpty(
                matchingResults.Where(result => LooksLikeCarrierOrg(result.Org)).Select(result => result.Org)
                    .Concat(matchingResults.Select(result => result.Org))
                    .ToArray());
            return new IpLookupResult(primary.Ip, location, org);
        }

        throw new InvalidOperationException($"无法获取出口 IP 信息：{lastError?.Message ?? "未知错误"}");
    }

    private static bool LooksLikeCarrierOrg(string value)
    {
        var normalized = Regex.Replace(value, @"[\s._-]+", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        return normalized.Contains("chinatelecom")
            || normalized.Contains("telecom")
            || normalized.Contains("chinaunicom")
            || normalized.Contains("unicom")
            || normalized.Contains("chinamobile")
            || normalized.Contains("cmcc")
            || normalized.Contains("cmi");
    }

    private static IpLookupResult ParseIp9(JsonElement root)
    {
        if (root.TryGetProperty("ret", out var retValue) &&
            retValue.ValueKind == JsonValueKind.Number &&
            retValue.TryGetInt32(out var ret) &&
            ret != 200)
        {
            return new IpLookupResult("", "", "");
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new IpLookupResult("", "", "");

        var ip = GetJsonString(data, "ip");
        var country = GetJsonString(data, "country");
        var province = GetJsonString(data, "prov");
        var city = GetJsonString(data, "city");
        var area = GetJsonString(data, "area");
        var isp = GetJsonString(data, "isp");

        return new IpLookupResult(ip, JoinLocation(country, province, city, area), isp);
    }

    private static IpLookupResult ParseIpWhoIs(JsonElement root)
    {
        var success = !root.TryGetProperty("success", out var successValue)
            || successValue.ValueKind != JsonValueKind.False;
        if (!success)
            return new IpLookupResult("", "", "");

        var ip = GetJsonString(root, "ip");
        var country = GetJsonString(root, "country");
        var region = GetJsonString(root, "region");
        var city = GetJsonString(root, "city");
        var org = GetJsonString(root, "org");
        var isp = GetJsonString(root, "isp");

        if (root.TryGetProperty("connection", out var connection))
        {
            org = FirstNonEmpty(GetJsonString(connection, "org"), org);
            isp = FirstNonEmpty(GetJsonString(connection, "isp"), isp);
        }

        return new IpLookupResult(ip, JoinLocation(country, region, city), FirstNonEmpty(isp, org));
    }

    private static IpLookupResult ParseIpInfo(JsonElement root)
    {
        var ip = GetJsonString(root, "ip");
        var city = GetJsonString(root, "city");
        var region = GetJsonString(root, "region");
        var country = GetJsonString(root, "country");
        var org = GetJsonString(root, "org");

        return new IpLookupResult(ip, JoinLocation(country, region, city), org);
    }

    private static string GetJsonString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value))
            return "";
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.ToString();
    }

    private static string JoinLocation(params string[] parts) =>
        string.Join(" ", parts
            .Select(part => part.Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string ToChineseIpLocation(string value)
    {
        var text = value.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "未知";

        foreach (var (from, to) in IpLocationPhraseTranslations)
            text = Regex.Replace(text, Regex.Escape(from), to, RegexOptions.IgnoreCase);

        var parts = Regex.Split(text, @"[\s,/|]+")
            .Select(TranslateIpLocationToken)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parts.Count == 0 ? value.Trim() : string.Join(" ", parts);
    }

    private static string TranslateIpLocationToken(string value)
    {
        var token = value.Trim();
        return token.ToLowerInvariant() switch
        {
            "china" or "cn" or "chn" => "中国",
            "jiangsu" => "江苏",
            "suzhou" => "苏州",
            "nanjing" => "南京",
            "wuxi" => "无锡",
            "changzhou" => "常州",
            "nantong" => "南通",
            "beijing" => "北京",
            "shanghai" => "上海",
            "tianjin" => "天津",
            "chongqing" => "重庆",
            "zhejiang" => "浙江",
            "hangzhou" => "杭州",
            "ningbo" => "宁波",
            "guangdong" => "广东",
            "guangzhou" => "广州",
            "shenzhen" => "深圳",
            "dongguan" => "东莞",
            "fujian" => "福建",
            "fuzhou" => "福州",
            "xiamen" => "厦门",
            "shandong" => "山东",
            "jinan" => "济南",
            "qingdao" => "青岛",
            "henan" => "河南",
            "zhengzhou" => "郑州",
            "hebei" => "河北",
            "shijiazhuang" => "石家庄",
            "hubei" => "湖北",
            "wuhan" => "武汉",
            "hunan" => "湖南",
            "changsha" => "长沙",
            "sichuan" => "四川",
            "chengdu" => "成都",
            "anhui" => "安徽",
            "hefei" => "合肥",
            "jiangxi" => "江西",
            "nanchang" => "南昌",
            "liaoning" => "辽宁",
            "shenyang" => "沈阳",
            "dalian" => "大连",
            "jilin" => "吉林",
            "heilongjiang" => "黑龙江",
            "harbin" => "哈尔滨",
            "shanxi" => "山西",
            "taiyuan" => "太原",
            "shaanxi" => "陕西",
            "xian" or "xi'an" => "西安",
            "guangxi" => "广西",
            "nanning" => "南宁",
            "yunnan" => "云南",
            "kunming" => "昆明",
            "guizhou" => "贵州",
            "guiyang" => "贵阳",
            "hainan" => "海南",
            "haikou" => "海口",
            "gansu" => "甘肃",
            "lanzhou" => "兰州",
            "qinghai" => "青海",
            "ningxia" => "宁夏",
            "xinjiang" => "新疆",
            "tibet" or "xizang" => "西藏",
            "mongolia" => "内蒙古",
            "taiwan" => "中国台湾",
            "hongkong" => "中国香港",
            "macau" or "macao" => "中国澳门",
            "us" or "usa" or "america" => "美国",
            "canada" => "加拿大",
            "japan" => "日本",
            "korea" => "韩国",
            "singapore" => "新加坡",
            "thailand" => "泰国",
            "vietnam" => "越南",
            "malaysia" => "马来西亚",
            "indonesia" => "印度尼西亚",
            "philippines" => "菲律宾",
            "australia" => "澳大利亚",
            "germany" => "德国",
            "france" => "法国",
            "italy" => "意大利",
            "spain" => "西班牙",
            "netherlands" => "荷兰",
            "russia" => "俄罗斯",
            "india" => "印度",
            "brazil" => "巴西",
            "syracuse" => "锡拉丘兹",
            "province" or "city" or "prefecture" or "municipality" => "",
            _ => token,
        };
    }

    private static string ToChineseIpOrg(string value)
    {
        var text = value.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "未知";

        var normalized = Regex.Replace(text, @"[\s._-]+", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        if (normalized.Contains("chinatelecom") || normalized.Contains("telecom"))
            return "中国电信";
        if (normalized.Contains("chinaunicom") || normalized.Contains("unicom"))
            return "中国联通";
        if (normalized.Contains("chinamobile") || normalized.Contains("cmcc") || normalized.Contains("cmi"))
            return "中国移动";
        if (normalized.Contains("cernet"))
            return "中国教育网";
        if (normalized.Contains("alibaba") || normalized.Contains("aliyun"))
            return "阿里云";
        if (normalized.Contains("tencent"))
            return "腾讯云";
        if (normalized.Contains("huawei"))
            return "华为云";
        if (normalized.Contains("baidu"))
            return "百度云";

        foreach (var (from, to) in IpOrgPhraseTranslations)
            text = Regex.Replace(text, Regex.Escape(from), to, RegexOptions.IgnoreCase);
        return text;
    }

    private static readonly (string From, string To)[] IpLocationPhraseTranslations =
    [
        ("United States of America", "美国"),
        ("Hong Kong", "hongkong"),
        ("Macau", "macau"),
        ("Macao", "macau"),
        ("Inner Mongolia", "内蒙古"),
        ("United States", "美国"),
        ("United Kingdom", "英国"),
        ("South Korea", "韩国"),
        ("New York", "纽约州"),
        ("New Jersey", "新泽西州"),
        ("California", "加利福尼亚州"),
        ("Texas", "得克萨斯州"),
        ("Florida", "佛罗里达州"),
        ("Washington", "华盛顿州"),
        ("Illinois", "伊利诺伊州"),
        ("Virginia", "弗吉尼亚州"),
        ("Ohio", "俄亥俄州"),
        ("Georgia", "佐治亚州"),
        ("Pennsylvania", "宾夕法尼亚州"),
        ("North Carolina", "北卡罗来纳州"),
        ("South Carolina", "南卡罗来纳州"),
        ("Massachusetts", "马萨诸塞州"),
        ("Arizona", "亚利桑那州"),
        ("Nevada", "内华达州"),
        ("Oregon", "俄勒冈州"),
        ("Colorado", "科罗拉多州"),
        ("Michigan", "密歇根州"),
        ("Syracuse", "锡拉丘兹"),
        ("Los Angeles", "洛杉矶"),
        ("San Francisco", "旧金山"),
        ("San Jose", "圣何塞"),
        ("Seattle", "西雅图"),
        ("Chicago", "芝加哥"),
        ("Dallas", "达拉斯"),
        ("Houston", "休斯敦"),
        ("Miami", "迈阿密"),
        ("Atlanta", "亚特兰大"),
        ("Ashburn", "阿什本"),
    ];

    private static readonly (string From, string To)[] IpOrgPhraseTranslations =
    [
        ("China", "中国"),
        ("Telecom", "电信"),
        ("Unicom", "联通"),
        ("Mobile", "移动"),
    ];

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private sealed record IpLookupResult(string Ip, string Location, string Org);

    private void ClearFields()
    {
        NicknameBox.Text = "";
        LoginEmailBox.Text = "";
        PasswordBox.Text = "";
        SeriesUrlBox.Text = TikTokUrls.DefaultSeriesDraftUrl;
        WorkspaceBox.Text = "";
        ArchiveRootBox.Text = "";
        DownloadWorkspaceBox.Text = "";
        ExcelReportBox.Text = "";
        DeleteVideosOnArchiveBox.IsChecked = true;
        RemoteDispatchBox.IsChecked = false;
        LegacyCdpSettings.IsVisible = false;
        CdpEndpointBox.Text = "";
        ContractIdBox.Text = "";
        CopyrightMaterials.ProofTemplatePathBox.Text = "";
        CopyrightMaterials.ProofDeclarantCompanyNameBox.Text = "";
        CopyrightMaterials.ProofSealPathBox.Text = "";
        CopyrightMaterials.ProofCopyrightCompanyNameBox.Text = "";
        CopyrightMaterials.EpisodeScriptEpisodeCountBox.Value = TikTokAccountProfile.DefaultEpisodeScriptEpisodeCount;
        CopyrightMaterials.AiScriptOutlineEpisodeCountBox.Value = TikTokAccountProfile.DefaultAiScriptOutlineEpisodeCount;
        CopyrightMaterials.RoleVectorCharacterCountBox.Value = TikTokAccountProfile.DefaultRoleVectorCharacterCount;
        CopyrightMaterials.RoleVectorMinimumCharacterCountBox.Value = TikTokAccountProfile.DefaultRoleVectorMinimumCharacterCount;
        CopyrightMaterials.Clear();
        SelectByTag(DramaMarketCombo, TikTokAccountProfile.DramaMarketLocal, TikTokAccountProfile.DramaMarketLocal);
        ZeroCostAdsBox.IsChecked = false;
        DayZeroRoiBox.Value = (decimal)TikTokPublishOptions.DefaultDayZeroRoi;
        SelectByTag(SubmitActionCombo, "submit", "submit");
        ExpectedPriceValueBox.Text = "";
        VideoDedupEditor.Load(null);
        ProxyHostBox.Text = "";
        ProxyUsernameBox.Text = "";
        ProxyPasswordBox.Text = "";
        ProxyLabelBox.Text = "";
        StaticIpNoteBox.Text = "";
    }

    private void OnPasswordVisibilityClick(object? sender, RoutedEventArgs e) =>
        SetPasswordVisibility(!PasswordBox.RevealPassword);

    private void SetPasswordVisibility(bool visible)
    {
        PasswordBox.RevealPassword = visible;
        var label = visible ? "隐藏密码" : "显示密码";
        ToolTip.SetTip(PasswordVisibilityButton, label);
        Avalonia.Automation.AutomationProperties.SetName(PasswordVisibilityButton, label);
    }

    private async void OnBrowseWorkspaceClick(object? sender, RoutedEventArgs e) =>
        await PickFolderAsync(WorkspaceBox, "选择上传工作目录");

    private async void OnBrowseArchiveRootClick(object? sender, RoutedEventArgs e) =>
        await PickFolderAsync(ArchiveRootBox, "选择当前账号归档目录");

    private async void OnBrowseDownloadWorkspaceClick(object? sender, RoutedEventArgs e) =>
        await PickFolderAsync(DownloadWorkspaceBox, "选择下载工作目录");

    private async void OnBrowseExcelReportClick(object? sender, RoutedEventArgs e) =>
        await PickFileAsync(ExcelReportBox, "选择 Excel 报表文件", ["xlsx", "xls"]);

    private async Task FetchPublishAccountsAsync()
    {
        if (_vm?.SelectedAccount?.Model is not { } profile) return;
        PublishAccounts.WriteTo(profile);
        PublishAccounts.SetFetchEnabled(false);
        try
        {
            await _vm.SyncPublishAccountsAsync(profile, PublishAccounts.ReadSelectedKeys());
            PublishAccounts.Load(profile);
        }
        catch
        {
            // 状态已在 ViewModel 中更新
        }
        finally
        {
            PublishAccounts.SetFetchEnabled(true);
        }
    }

    private async Task PickFolderAsync(TextBox target, string title)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;
        target.Text = folder.Path.LocalPath;
    }

    private async Task PickFileAsync(TextBox target, string title, IReadOnlyList<string> extensions)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(title)
                {
                    Patterns = extensions.Select(ext => $"*.{ext}").ToList(),
                },
            },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;
        target.Text = file.Path.LocalPath;
    }

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

    private static string TagOf(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private void OnRefreshUploadedSeriesClick(object? sender, RoutedEventArgs e) =>
        RequestUploadedSeriesReload();

    private void OnUploadedSeriesSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressUploadedSeriesSearch)
            return;
        ApplyUploadedSeriesFilter();
    }

    private void OnOpenUploadedSeriesDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AccountUploadedSeriesRow row } || !row.CanOpenDirectory)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = row.ProjectDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowUploadedSeriesMessage($"打开项目目录失败：{ex.Message}", hasRows: true);
        }
    }

    private void RequestUploadedSeriesReload()
    {
        var generation = ++_uploadedSeriesGeneration;
        var profile = _vm?.SelectedAccount?.Model;
        _suppressUploadedSeriesSearch = true;
        UploadedSeriesSearchBox.Text = "";
        _suppressUploadedSeriesSearch = false;
        _uploadedSeriesRows = Array.Empty<AccountUploadedSeriesRow>();
        UploadedSeriesList.ItemsSource = null;

        if (profile is null)
        {
            ShowUploadedSeriesMessage("尚未选择账号");
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.ResolveWorkspacePath()))
        {
            ShowUploadedSeriesMessage("请先在基础设置里配置上传工作目录。");
            return;
        }

        ShowUploadedSeriesMessage("正在读取已上传剧集…");
        _ = LoadUploadedSeriesAsync(profile, generation);
    }

    private async Task LoadUploadedSeriesAsync(TikTokAccountProfile profile, int generation)
    {
        IReadOnlyList<AccountUploadedSeriesRow> rows;
        string? error = null;
        try
        {
            rows = await Task.Run(() => AccountUploadedSeriesCatalog.Load(profile));
        }
        catch (Exception ex)
        {
            rows = Array.Empty<AccountUploadedSeriesRow>();
            error = ex.Message;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _uploadedSeriesGeneration)
                return;
            if (error is not null)
            {
                ShowUploadedSeriesMessage($"读取已上传剧集失败：{error}");
                return;
            }

            _uploadedSeriesRows = rows;
            ApplyUploadedSeriesFilter();
        });
    }

    private void ApplyUploadedSeriesFilter()
    {
        var keyword = (UploadedSeriesSearchBox.Text ?? "").Trim();
        var rows = keyword.Length == 0
            ? _uploadedSeriesRows
            : _uploadedSeriesRows.Where(row =>
                row.OriginalTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.NewTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToArray();
        UploadedSeriesList.ItemsSource = rows;
        if (rows.Count == 0)
        {
            ShowUploadedSeriesMessage(keyword.Length == 0
                ? "该账号还没有已上传剧集。"
                : "没有匹配的剧集。");
            return;
        }

        UploadedSeriesStatusText.IsVisible = false;
        UploadedSeriesStatusText.Text = "";
    }

    private void ShowUploadedSeriesMessage(string message, bool hasRows = false)
    {
        UploadedSeriesStatusText.Text = message;
        UploadedSeriesStatusText.IsVisible = true;
        if (!hasRows)
            UploadedSeriesList.ItemsSource = null;
    }

    private void UploadStrategyCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateUploadChannelVisibility();

    private void OnBasicFormSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
            BasicForm.Width = Math.Min(e.NewSize.Width, BasicForm.MaxWidth);
    }

    private void OnPublishFormSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        if (width <= 0)
            return;

        PublishForm.Width = Math.Min(width, PublishForm.MaxWidth);
        foreach (var row in PublishForm.Children.Concat(ProxySettings.Children).OfType<UniformGrid>())
        {
            var columns = width < 580 ? 1 : 2;
            if (row.Columns != columns)
                row.Columns = columns;
        }
    }

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
