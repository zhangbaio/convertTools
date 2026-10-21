using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ChannelsAccount = ChannelsPublisher.Core.Models.PublishAccount;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Views;

namespace PlatformPublisher.Desktop.Views;

public partial class KuaishouPersonalConfigDialog : UserControl
{
    private KuaishouPersonalConfig _config = new();
    private PublishPlatform _platform = PublishPlatform.KuaishouPersonalRevenue;
    private ChannelsAccount? _account;
    private string _cleanJson = "";
    private bool _suspendDirty;
    public event Func<string, PublishPlatform, string, Task>? ConfigSaved;
    private readonly ObservableCollection<DistributorAccountEditorRow> _distributorAccounts = [];

    public KuaishouPersonalConfigDialog()
    {
        DataContext = this;
        InitializeComponent();
        DistributorAccountList.ItemsSource = _distributorAccounts;
        LoadValues();
        CaptureClean();
        UpdateHeading();
    }

    public async Task<bool> TryLeaveAsync(Window owner)
    {
        if (!HasUnsavedChanges()) return true;
        var save = await ConfirmDialog.ShowAsync(owner, "保存快手分账配置",
            $"「{_account?.Name ?? "当前账号"}」的{_platform.DisplayName()}配置已修改，是否保存？");
        if (!save)
        {
            LoadAccount(_account, _platform);
            return true;
        }
        return await SaveCurrentAsync();
    }

    public void LoadAccount(ChannelsAccount? account, PublishPlatform platform)
    {
        _account = account;
        _platform = platform is PublishPlatform.KuaishouEnterpriseRevenue
            ? PublishPlatform.KuaishouEnterpriseRevenue
            : PublishPlatform.KuaishouPersonalRevenue;
        _config = LoadStoredConfig();
        _suspendDirty = true;
        LoadValues();
        _suspendDirty = false;
        CaptureClean();
        UpdateHeading();
    }

    private void LoadValues()
    {
        ApiBaseUrlBox.Text = _config.ApiBaseUrl;
        AdvertiserIdBox.Text = FirstNonEmpty(_config.AdvertiserId, _config.KuaishouId);
        NicknameBox.Text = _config.KuaishouNickname;
        AppNameBox.Text = _config.AppName;
        AppIdBox.Text = _config.AppId;
        AppSecretBox.Text = _config.AppSecret;
        AuthorizeBaseUrlBox.Text = _config.AuthorizeBaseUrl;
        AuthCodeBox.Text = _config.AuthCode;
        RemoteTokenBox.IsChecked = _config.RemoteTokenEnabled;
        AccessTokenBox.Text = _config.AccessToken;
        AccessTokenExpiresAtBox.Text = _config.AccessTokenExpiresAt;
        RefreshTokenBox.Text = _config.RefreshToken;
        RefreshTokenExpiresAtBox.Text = _config.RefreshTokenExpiresAt;
        TokenHeaderBox.Text = _config.TokenHeader;

        CommitmentRecipientBox.Text = _config.CommitmentRecipientCompanyName;
        CommitmentSealBox.Text = _config.CommitmentSealPath;
        CommitmentTemplateBox.Text = _config.CommitmentTemplateDocxPath;

        FillChoice(ContentTypeBox, KuaishouFormChoices.ContentTypes, _config.ContentType, "漫剧");
        FillChoice(ComicMethodBox, KuaishouFormChoices.ComicMethods, _config.ProductionMethod, "3");
        FillChoice(AuthorDeclarationBox, KuaishouFormChoices.AuthorDeclarations, _config.AuthorDeclaration, "1");
        FillChoice(ProductionFormBox, KuaishouFormChoices.ProductionForms, _config.ProductionForm, "auto");
        ProductionYearBox.Text = _config.ProductionYear;
        AverageDurationBox.Text = _config.AverageEpisodeMinutes;
        ProductionCostBox.Text = _config.ProductionCost;
        FillChoice(SaleTypeBox, KuaishouFormChoices.SaleTypes, NormalizeSaleType(_config.SaleType), "3");
        FreeEpisodeCountBox.Text = _config.FreeEpisodeCount.ToString();
        UnlockEpisodeCountBox.Text = _config.UnlockEpisodeCount.ToString();
        SmallAmountUnlockBox.IsChecked = _config.SmallAmountUnlock;
        EpisodePriceBox.Text = _config.EpisodePrice;
        SeriesPriceBox.Text = _config.SeriesPrice;
        HasCopyrightProofBox.IsChecked = _config.HasCopyrightProof;
        FillChoice(CopyrightProofTypeBox, KuaishouFormChoices.CopyrightProofTypes, _config.CopyrightProofType, "1");
        FinishedBox.IsChecked = _config.Finished;
        FullSceneDisplayBox.IsChecked = _config.FullSceneDisplay;
        HasRecordNumberBox.IsChecked = _config.HasRecordNumber;
        SpecialThemeBox.IsChecked = IsTruthy(_config.SpecialTheme);
        PlotLabelsBox.Text = _config.PlotLabels;
        UpdateSaleTypeRows();

        BroadcastPlatformBox.Text = _config.BroadcastPlatform;
        FillChoice(BroadcastChannelBox, KuaishouFormChoices.BroadcastChannels, _config.BroadcastChannel, "小屏APP");
        var broadcastMode = string.IsNullOrWhiteSpace(_config.BroadcastTimeMode) ? "today" : _config.BroadcastTimeMode.Trim();
        var fixedDate = !string.Equals(broadcastMode, "today", StringComparison.OrdinalIgnoreCase);
        FillChoice(BroadcastTimeModeBox, KuaishouFormChoices.BroadcastTimeModes, fixedDate ? "fixed" : "today", "today");
        BroadcastDateBox.Text = fixedDate ? broadcastMode : _config.BroadcastDate;
        BroadcastDateBox.IsVisible = fixedDate;
        ActorLibraryBox.Text = _config.ActorLibraryText;
        ActorInfoBox.Text = _config.ActorInfoJson;
        DirectorInfoBox.Text = _config.DirectorInfoJson;
        ScreenwriterInfoBox.Text = _config.ScreenwriterInfoJson;

        NewTitleAliasEnabledBox.IsChecked = _config.NewTitleAliasEnabled;
        FillChoice(NewTitleAliasPositionBox, KuaishouFormChoices.AliasPositions, _config.NewTitleAliasPosition, "suffix");
        NewTitleAliasValueBox.Text = _config.NewTitleAliasValue;
        QueueParallelBox.Text = _config.QueueMaxParallelProjects.ToString();
        MaterialUploadPathBox.Text = _config.MaterialUploadPath;
        SeriesCreatePathBox.Text = _config.SeriesCreatePath;
        SeriesBaseInfoPathBox.Text = _config.SeriesBaseInfoPath;
        OnlineOfflinePathBox.Text = _config.OnlineOfflinePath;
        EpisodeUploadPathBox.Text = _config.EpisodeUploadPath;
        EpisodeCoverPathBox.Text = _config.EpisodeCoverUpdatePath;
        SeriesSubmitPathBox.Text = _config.SeriesSubmitPath;
        FillChoice(SubmitPreCheckBox, KuaishouFormChoices.SubmitPreWaits, _config.SubmitPreCheckWaitSeconds.ToString(), "0");
        SubmitReadyMaxBox.Text = _config.SubmitReadyCheckMax.ToString();
        SubmitReadyIntervalBox.Text = _config.SubmitReadyCheckIntervalSeconds.ToString();
        SubmitRetryMaxBox.Text = _config.SubmitRetryMax.ToString();
        SubmitRetryIntervalBox.Text = _config.SubmitRetryIntervalSeconds.ToString();

        ProjectImageRootBox.Text = _config.ProjectImageTemplateRoot;
        FillTemplate(UploadTemplateBox, FirstNonEmpty(_config.UploadProjectImageTemplateId, _config.ProjectImageTemplateId));
        FillTemplate(PersonalTemplateBox, _platform == PublishPlatform.KuaishouPersonalRevenue
            ? FirstNonEmpty(_config.PersonalProjectImageTemplateId, _config.ProjectImageTemplateId)
            : _config.PersonalProjectImageTemplateId);
        FillTemplate(EnterpriseTemplateBox, _platform == PublishPlatform.KuaishouEnterpriseRevenue
            ? FirstNonEmpty(_config.EnterpriseProjectImageTemplateId, _config.ProjectImageTemplateId)
            : _config.EnterpriseProjectImageTemplateId);

        FillChoice(VideoStorageProviderBox, KuaishouFormChoices.VideoStorageProviders, _config.VideoStorageProvider, "aliyun_oss");
        StorageConcurrencyBox.Text = _config.PublicStorageUploadConcurrency.ToString();
        StorageRetryBox.Text = _config.PublicStorageUploadRetryCount.ToString();
        OssCleanupBox.IsChecked = _config.OssCleanupEnabled;
        OssRetentionHoursBox.Text = _config.OssCleanupRetentionHours.ToString();
        OssIntervalBox.Text = _config.OssCleanupIntervalMinutes.ToString();
        OssMaxAttemptsBox.Text = _config.OssCleanupMaxAttempts.ToString();
        OssDeleteOnProjectDeleteBox.IsChecked = _config.OssCleanupDeleteOnProjectDelete;

        AutoOnlineBox.IsChecked = _config.AutoOnlineEnabled;
        AutoOnlineIntervalBox.Text = _config.AutoOnlineIntervalMinutes.ToString();
        AutoOnlineMaxItemsBox.Text = _config.AutoOnlineMaxItemsPerRound.ToString();
        AutoOnlineMaxWaitBox.Text = _config.AutoOnlineMaxWaitDays.ToString();
        AutoOnlineOnlyWhenIdleBox.IsChecked = _config.AutoOnlineOnlyWhenIdle;
        OnlineAutoDistributionBox.IsChecked = _config.OnlineAutoDistributionEnabled;
        OnlineShowResultDialogBox.IsChecked = _config.OnlineShowResultDialog;
        OnlineNotifyBox.IsChecked = _config.OnlineNotifyEnabled || NoticeContains("online_success");
        RejectedNotifyBox.IsChecked = _config.OnlineNotifyRejectedEnabled || NoticeContains("audit_rejected");
        RejectedNotifyOnceBox.IsChecked = _config.OnlineNotifyRejectedOnce;
        OnlineCleanupBox.IsChecked = _config.OnlineCleanupEnabled;
        KeepOnlineDaysBox.Text = _config.OnlineKeepOnlineDays.ToString();
        KeepManualDaysBox.Text = _config.OnlineKeepManualOnlineDays.ToString();
        KeepRejectedDaysBox.Text = _config.OnlineKeepRejectedDays.ToString();

        DistributionEnabledBox.IsChecked = _config.DistributionEnabled;
        FillChoice(DistributionModeBox, KuaishouFormChoices.DistributionModes, _config.DistributionMode, "browser");
        DistributionApiPathBox.Text = _config.DistributionApiPath;
        DistributionOnlineTimeBox.Text = string.IsNullOrWhiteSpace(_config.DistributionAllowOnlineTimeText)
            ? (_config.DistributionAllowOnlineTime ? "1" : "0")
            : _config.DistributionAllowOnlineTimeText;
        LoadDistributorAccounts(_config.DistributionDistributorAccountsJson);
        DistributionJuxingBox.IsChecked = _config.DistributionAllowJuxing;
        DistributionSubmitBox.IsChecked = _config.DistributionSubmitEnabled;
        DistributionHeadlessBox.IsChecked = _config.DistributionHeadlessEnabled;
        DistributionManualBox.IsChecked = _config.DistributionManualInterventionEnabled;
        DistributionFeishuBox.IsChecked = _config.DistributionFeishuNotifyEnabled;
        DistributionLoginNotifyBox.IsChecked = _config.DistributionLoginNotifyEnabled;
        DistributionLoginTimeoutBox.Text = _config.DistributionLoginTimeoutSeconds.ToString();
        UpdateDistributionModeRows();

        EntryUrlBox.Text = _config.EntryUrl;
        UsernameBox.Text = FirstNonEmpty(_config.Username, _config.RealName);
        FillChoice(GenderBox, KuaishouFormChoices.Genders, _config.Gender, "");
        CommitmentPdfBox.Text = _config.CommitmentPdfPath;
        LoginTimeoutBox.Text = _config.LoginTimeoutSeconds.ToString();
        FillChoice(RevenueShareBrowserModeBox, KuaishouFormChoices.BrowserModes, _config.RevenueShareHeadless ? "true" : "false", "false");
        RevenueShareFullSceneBox.IsChecked = _config.RevenueShareFullSceneDisplay;
        KeepBrowserBox.IsChecked = _config.KeepBrowserOpenOnFailure;
        FillChoice(FirstPageActionBox, KuaishouFormChoices.FirstPageActions, _config.FirstPageAction, "next");
        FillChoice(FinalActionBox, KuaishouFormChoices.FinalActions, _config.FinalAction, "submit_review");

        AiComplianceBox.IsChecked = _config.AiComplianceReviewEnabled;
        SynopsisRewriteBox.IsChecked = _config.SynopsisAiRewriteEnabled;
        LoadContentPolicy();
    }

    private bool NoticeContains(string token) =>
        (_config.OnlineNoticeConditions ?? string.Empty).Contains(token, StringComparison.OrdinalIgnoreCase);

    private void LoadContentPolicy()
    {
        TitlePolicySummary.Text = KuaishouContentPolicyText.BaselineSummary(
            KuaishouContentPolicyDefaults.TitlePolicyJson, "categories");
        SynopsisTermsSummary.Text = KuaishouContentPolicyText.BaselineSummary(
            KuaishouContentPolicyDefaults.SynopsisPolicyJson, "categories");
        ConceptGroupsSummary.Text = KuaishouContentPolicyText.BaselineSummary(
            KuaishouContentPolicyDefaults.SynopsisPolicyJson, "concept_groups");
        TitlePolicyBox.Text = KuaishouContentPolicyText.FormatCategories(
            _config.DramaTitlePolicyJson, KuaishouContentPolicyDefaults.TitlePolicyJson, "categories", "disabled");
        SynopsisTermsBox.Text = KuaishouContentPolicyText.FormatCategories(
            _config.SynopsisPolicyJson, KuaishouContentPolicyDefaults.SynopsisPolicyJson, "categories", "disabled");
        ConceptGroupsBox.Text = KuaishouContentPolicyText.FormatCategories(
            _config.SynopsisPolicyJson, KuaishouContentPolicyDefaults.SynopsisPolicyJson, "concept_groups", "disabled_concepts");
        CombinationRulesBox.Text = KuaishouContentPolicyText.FormatRules(
            _config.SynopsisPolicyJson, KuaishouContentPolicyDefaults.SynopsisPolicyJson);
    }

    private void RestoreContentPolicy_Click(object? sender, RoutedEventArgs e)
    {
        _config.DramaTitlePolicyJson = KuaishouContentPolicyDefaults.TitlePolicyJson;
        _config.SynopsisPolicyJson = KuaishouContentPolicyDefaults.SynopsisPolicyJson;
        LoadContentPolicy();
    }

    private async void PickCommitmentPdf_Click(object? sender, RoutedEventArgs e)
    {
        var files = await Files.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择承诺函 PDF",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PDF 文件") { Patterns = ["*.pdf"] }],
        });
        if (files.Count > 0) CommitmentPdfBox.Text = files[0].Path.LocalPath;
    }

    private async void PickCommitmentTemplate_Click(object? sender, RoutedEventArgs e)
    {
        var files = await Files.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择快手承诺函 Word 模板",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Word 模板") { Patterns = ["*.docx"] }],
        });
        if (files.Count > 0) CommitmentTemplateBox.Text = files[0].Path.LocalPath;
    }

    private async void PickCommitmentSeal_Click(object? sender, RoutedEventArgs e)
    {
        var files = await Files.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择承诺函印章图片",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }],
        });
        if (files.Count > 0) CommitmentSealBox.Text = files[0].Path.LocalPath;
    }

    private async void PickProjectImageRoot_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await Files.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择快手工程图模板根目录", AllowMultiple = false });
        if (folders.Count == 0) return;
        ProjectImageRootBox.Text = folders[0].Path.LocalPath;
    }

    private async void ImportLegacy_Click(object? sender, RoutedEventArgs e)
    {
        var path = KuaishouLegacyConfigImporter.DefaultSettingsPath;
        if (!File.Exists(path))
        {
            var files = await Files.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择旧版短剧助手 settings.json",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON 配置") { Patterns = ["*.json"] }],
            });
            if (files.Count == 0) return;
            path = files[0].Path.LocalPath;
        }

        try
        {
            var result = KuaishouLegacyConfigImporter.Import(path, _config, _platform);
            LoadValues();
            ValidationText.Foreground = Avalonia.Media.Brushes.ForestGreen;
            ValidationText.Text = $"已从旧版配置导入 {result.ImportedFields} 个字段，其中 {result.ImportedSensitiveFields} 个凭据字段将在保存时加密。";
        }
        catch (Exception ex)
        {
            ValidationText.Foreground = Avalonia.Media.Brushes.Red;
            ValidationText.Text = "旧版配置导入失败：" + ex.Message;
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e) => await SaveCurrentAsync();

    private bool ApplyToConfig()
    {
        if (!int.TryParse(FreeEpisodeCountBox.Text, out var free) || free < 0 ||
            !int.TryParse(UnlockEpisodeCountBox.Text, out var unlock) || unlock < 0)
        {
            ValidationText.Text = "免费集数和解锁集数必须为非负整数。";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(CommitmentPdfBox.Text) &&
            (!File.Exists(CommitmentPdfBox.Text) || !string.Equals(Path.GetExtension(CommitmentPdfBox.Text), ".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            ValidationText.Text = "承诺函必须是存在的 PDF 文件。";
            return false;
        }

        _config.ApiBaseUrl = ApiBaseUrlBox.Text?.Trim() ?? string.Empty;
        _config.AdvertiserId = AdvertiserIdBox.Text?.Trim() ?? string.Empty;
        _config.KuaishouId = _config.AdvertiserId;
        _config.KuaishouNickname = NicknameBox.Text?.Trim() ?? string.Empty;
        _config.AppName = AppNameBox.Text?.Trim() ?? string.Empty;
        _config.AppId = AppIdBox.Text?.Trim() ?? string.Empty;
        _config.AppSecret = AppSecretBox.Text ?? string.Empty;
        _config.AuthorizeBaseUrl = AuthorizeBaseUrlBox.Text?.Trim() ?? string.Empty;
        _config.AuthCode = AuthCodeBox.Text ?? string.Empty;
        _config.RemoteTokenEnabled = RemoteTokenBox.IsChecked == true;
        _config.AccessToken = AccessTokenBox.Text ?? string.Empty;
        _config.AccessTokenExpiresAt = AccessTokenExpiresAtBox.Text?.Trim() ?? string.Empty;
        _config.RefreshToken = RefreshTokenBox.Text ?? string.Empty;
        _config.RefreshTokenExpiresAt = RefreshTokenExpiresAtBox.Text?.Trim() ?? string.Empty;
        _config.TokenHeader = TokenHeaderBox.Text?.Trim() ?? "Access-Token";

        _config.CommitmentRecipientCompanyName = CommitmentRecipientBox.Text?.Trim() ?? string.Empty;
        _config.CommitmentSealPath = CommitmentSealBox.Text?.Trim() ?? string.Empty;
        _config.CommitmentTemplateDocxPath = CommitmentTemplateBox.Text?.Trim() ?? string.Empty;

        _config.ContentType = ChoiceValue(ContentTypeBox, "漫剧");
        _config.ProductionMethod = ChoiceValue(ComicMethodBox, "3");
        _config.AuthorDeclaration = ChoiceValue(AuthorDeclarationBox, "1");
        _config.ProductionForm = ChoiceValue(ProductionFormBox, "auto");
        _config.ProductionYear = ProductionYearBox.Text?.Trim() ?? string.Empty;
        _config.AverageEpisodeMinutes = AverageDurationBox.Text?.Trim() ?? string.Empty;
        _config.ProductionCost = ProductionCostBox.Text?.Trim() ?? string.Empty;
        _config.SaleType = ChoiceValue(SaleTypeBox, "3");
        _config.FreeEpisodeCount = free;
        _config.UnlockEpisodeCount = unlock;
        _config.SmallAmountUnlock = SmallAmountUnlockBox.IsChecked == true;
        _config.EpisodePrice = EpisodePriceBox.Text?.Trim() ?? string.Empty;
        _config.SeriesPrice = SeriesPriceBox.Text?.Trim() ?? string.Empty;
        _config.HasCopyrightProof = HasCopyrightProofBox.IsChecked == true;
        _config.CopyrightProofType = ChoiceValue(CopyrightProofTypeBox, "1");
        _config.Finished = FinishedBox.IsChecked == true;
        _config.FullSceneDisplay = FullSceneDisplayBox.IsChecked == true;
        _config.HasRecordNumber = HasRecordNumberBox.IsChecked == true;
        _config.SpecialTheme = SpecialThemeBox.IsChecked == true ? "true" : "false";
        _config.PlotLabels = PlotLabelsBox.Text?.Trim() ?? string.Empty;

        _config.BroadcastPlatform = BroadcastPlatformBox.Text?.Trim() ?? string.Empty;
        _config.BroadcastChannel = ChoiceValue(BroadcastChannelBox, "小屏APP");
        var timeMode = ChoiceValue(BroadcastTimeModeBox, "today");
        _config.BroadcastTimeMode = timeMode == "fixed"
            ? (BroadcastDateBox.Text?.Trim() ?? string.Empty)
            : "today";
        if (timeMode == "fixed" && !string.IsNullOrWhiteSpace(BroadcastDateBox.Text))
            _config.BroadcastDate = BroadcastDateBox.Text.Trim();
        _config.ActorLibraryText = ActorLibraryBox.Text ?? string.Empty;
        _config.ActorInfoJson = ActorInfoBox.Text ?? string.Empty;
        _config.DirectorInfoJson = DirectorInfoBox.Text ?? string.Empty;
        _config.ScreenwriterInfoJson = ScreenwriterInfoBox.Text ?? string.Empty;

        _config.NewTitleAliasEnabled = NewTitleAliasEnabledBox.IsChecked == true;
        _config.NewTitleAliasPosition = ChoiceValue(NewTitleAliasPositionBox, "suffix");
        _config.NewTitleAliasValue = NewTitleAliasValueBox.Text?.Trim() ?? string.Empty;
        _config.QueueMaxParallelProjects = Math.Clamp(Positive(QueueParallelBox.Text, 1), 1, 5);
        _config.MaterialUploadPath = MaterialUploadPathBox.Text?.Trim() ?? string.Empty;
        _config.SeriesCreatePath = SeriesCreatePathBox.Text?.Trim() ?? string.Empty;
        _config.SeriesBaseInfoPath = SeriesBaseInfoPathBox.Text?.Trim() ?? string.Empty;
        _config.OnlineOfflinePath = OnlineOfflinePathBox.Text?.Trim() ?? string.Empty;
        _config.EpisodeUploadPath = EpisodeUploadPathBox.Text?.Trim() ?? string.Empty;
        _config.EpisodeCoverUpdatePath = EpisodeCoverPathBox.Text?.Trim() ?? string.Empty;
        _config.SeriesSubmitPath = SeriesSubmitPathBox.Text?.Trim() ?? string.Empty;
        _config.SubmitPreCheckWaitSeconds = int.TryParse(ChoiceValue(SubmitPreCheckBox, "0"), out var preWait) ? preWait : 0;
        _config.SubmitReadyCheckMax = Positive(SubmitReadyMaxBox.Text, 3);
        _config.SubmitReadyCheckIntervalSeconds = Positive(SubmitReadyIntervalBox.Text, 30);
        _config.SubmitRetryMax = NonNegative(SubmitRetryMaxBox.Text, 3);
        _config.SubmitRetryIntervalSeconds = Positive(SubmitRetryIntervalBox.Text, 30);

        _config.ProjectImageTemplateRoot = ProjectImageRootBox.Text?.Trim() ?? string.Empty;
        _config.UploadProjectImageTemplateId = TemplateValue(UploadTemplateBox);
        _config.PersonalProjectImageTemplateId = TemplateValue(PersonalTemplateBox);
        _config.EnterpriseProjectImageTemplateId = TemplateValue(EnterpriseTemplateBox);
        _config.ProjectImageTemplateId = _platform == PublishPlatform.KuaishouEnterpriseRevenue
            ? _config.EnterpriseProjectImageTemplateId
            : _config.PersonalProjectImageTemplateId;

        _config.VideoStorageProvider = ChoiceValue(VideoStorageProviderBox, "aliyun_oss");
        _config.PublicStorageUploadConcurrency = Math.Clamp(Positive(StorageConcurrencyBox.Text, 2), 1, 8);
        _config.PublicStorageUploadRetryCount = Math.Clamp(NonNegative(StorageRetryBox.Text, 5), 0, 10);
        _config.OssCleanupEnabled = OssCleanupBox.IsChecked == true;
        _config.OssCleanupRetentionHours = NonNegative(OssRetentionHoursBox.Text, 24);
        _config.OssCleanupIntervalMinutes = Positive(OssIntervalBox.Text, 60);
        _config.OssCleanupMaxAttempts = Positive(OssMaxAttemptsBox.Text, 5);
        _config.OssCleanupDeleteOnProjectDelete = OssDeleteOnProjectDeleteBox.IsChecked == true;

        _config.AutoOnlineEnabled = AutoOnlineBox.IsChecked == true;
        _config.AutoOnlineIntervalMinutes = Positive(AutoOnlineIntervalBox.Text, 30);
        _config.AutoOnlineMaxItemsPerRound = Positive(AutoOnlineMaxItemsBox.Text, 30);
        _config.AutoOnlineMaxWaitDays = Positive(AutoOnlineMaxWaitBox.Text, 14);
        _config.AutoOnlineOnlyWhenIdle = AutoOnlineOnlyWhenIdleBox.IsChecked == true;
        _config.OnlineAutoDistributionEnabled = OnlineAutoDistributionBox.IsChecked == true;
        _config.OnlineShowResultDialog = OnlineShowResultDialogBox.IsChecked == true;
        _config.OnlineNotifyEnabled = OnlineNotifyBox.IsChecked == true;
        _config.OnlineNotifyRejectedEnabled = RejectedNotifyBox.IsChecked == true;
        _config.OnlineNotifyRejectedOnce = RejectedNotifyOnceBox.IsChecked == true;
        _config.OnlineNoticeConditions = JsonSerializer.Serialize(NoticeConditions());
        _config.OnlineCleanupEnabled = OnlineCleanupBox.IsChecked == true;
        _config.OnlineKeepOnlineDays = Positive(KeepOnlineDaysBox.Text, 7);
        _config.OnlineKeepManualOnlineDays = Positive(KeepManualDaysBox.Text, 3);
        _config.OnlineKeepRejectedDays = Positive(KeepRejectedDaysBox.Text, 30);

        _config.DistributionEnabled = DistributionEnabledBox.IsChecked == true;
        _config.DistributionMode = ChoiceValue(DistributionModeBox, "browser");
        _config.DistributionApiPath = DistributionApiPathBox.Text?.Trim() ?? string.Empty;
        _config.DistributionAllowOnlineTimeText = DistributionOnlineTimeBox.Text?.Trim() ?? "0";
        _config.DistributionAllowOnlineTime = _config.DistributionAllowOnlineTimeText is not "" and not "0";
        _config.DistributionDistributorAccountsJson = SerializeDistributorAccounts();
        _config.DistributionAllowJuxing = DistributionJuxingBox.IsChecked == true;
        _config.DistributionSubmitEnabled = DistributionSubmitBox.IsChecked == true;
        _config.DistributionHeadlessEnabled = DistributionHeadlessBox.IsChecked == true;
        _config.DistributionManualInterventionEnabled = DistributionManualBox.IsChecked == true;
        _config.DistributionFeishuNotifyEnabled = DistributionFeishuBox.IsChecked == true;
        _config.DistributionLoginNotifyEnabled = DistributionLoginNotifyBox.IsChecked == true;
        _config.DistributionLoginTimeoutSeconds = Positive(DistributionLoginTimeoutBox.Text, 300);

        _config.EntryUrl = EntryUrlBox.Text?.Trim() ?? string.Empty;
        _config.Username = UsernameBox.Text?.Trim() ?? string.Empty;
        _config.RealName = _config.Username;
        _config.Gender = ChoiceValue(GenderBox, "");
        _config.CommitmentPdfPath = CommitmentPdfBox.Text?.Trim() ?? string.Empty;
        _config.LoginTimeoutSeconds = Positive(LoginTimeoutBox.Text, 300);
        _config.RevenueShareHeadless = ChoiceValue(RevenueShareBrowserModeBox, "false") == "true";
        _config.Headless = _config.RevenueShareHeadless;
        _config.RevenueShareFullSceneDisplay = RevenueShareFullSceneBox.IsChecked == true;
        _config.KeepBrowserOpenOnFailure = KeepBrowserBox.IsChecked == true;
        _config.FirstPageAction = ChoiceValue(FirstPageActionBox, "next");
        _config.FinalAction = ChoiceValue(FinalActionBox, "submit_review");

        _config.AiComplianceReviewEnabled = AiComplianceBox.IsChecked == true;
        _config.SynopsisAiRewriteEnabled = SynopsisRewriteBox.IsChecked == true;
        _config.DramaTitlePolicyJson = KuaishouContentPolicyText.BuildTitlePolicy(
            TitlePolicyBox.Text ?? string.Empty, KuaishouContentPolicyDefaults.TitlePolicyJson);
        _config.SynopsisPolicyJson = KuaishouContentPolicyText.BuildSynopsisPolicy(
            SynopsisTermsBox.Text ?? string.Empty, ConceptGroupsBox.Text ?? string.Empty,
            CombinationRulesBox.Text ?? string.Empty, KuaishouContentPolicyDefaults.SynopsisPolicyJson);
        _config.ContentPolicyMigrated = true;
        return true;
    }

    private void SaleTypeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSaleTypeRows();

    private void UpdateSaleTypeRows()
    {
        var code = KuaishouFormChoices.SaleTypeCode(ChoiceValue(SaleTypeBox, "3"));
        var paidSeries = code is 0 or 2;
        var singleEpisode = code == 2;
        var adUnlock = code == 3;
        SetRowVisible(UnlockCountLabel, UnlockEpisodeCountBox, adUnlock);
        SmallAmountLabel.IsVisible = adUnlock;
        SmallAmountUnlockBox.IsVisible = adUnlock;
        SetRowVisible(EpisodePriceLabel, EpisodePriceBox, singleEpisode);
        SetRowVisible(SeriesPriceLabel, SeriesPriceBox, paidSeries);
    }

    private void BroadcastTimeModeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (BroadcastDateBox is null || BroadcastTimeModeBox is null) return;
        BroadcastDateBox.IsVisible = ChoiceValue(BroadcastTimeModeBox, "today") == "fixed";
    }

    private void DistributionModeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateDistributionModeRows();

    private void UpdateDistributionModeRows()
    {
        if (DistributionModeBox is null) return;
        var api = ChoiceValue(DistributionModeBox, "browser") == "api";
        DistributionApiPathLabel.IsEnabled = api;
        DistributionApiPathBox.IsEnabled = api;
        DistributionOnlineTimeLabel.IsEnabled = api;
        DistributionOnlineTimeBox.IsEnabled = api;
        DistributionHeadlessLabel.IsEnabled = !api;
        DistributionHeadlessBox.IsEnabled = !api;
        DistributionManualLabel.IsEnabled = !api;
        DistributionManualBox.IsEnabled = !api;
        DistributionLoginNotifyLabel.IsEnabled = !api;
        DistributionLoginNotifyBox.IsEnabled = !api;
        DistributionLoginTimeoutLabel.IsEnabled = !api;
        DistributionLoginTimeoutBox.IsEnabled = !api;
        DistributionSubmitBox.Content = api ? "调用官方接口创建分销配置" : "点击创建分销短剧按钮";
    }

    private void AddDistributorAccount_Click(object? sender, RoutedEventArgs e) =>
        _distributorAccounts.Add(new DistributorAccountEditorRow());

    private void RemoveDistributorAccount_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in DistributorAccountList.SelectedItems?.OfType<DistributorAccountEditorRow>().ToArray() ?? [])
            _distributorAccounts.Remove(row);
    }

    private async void ImportDistributorAccounts_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "批量导入分销商账号",
            Width = 640,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var editor = new TextBox { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Watermark = "建幸苦酱,1743291153\n大西瓜漫剧（UID:1542483952）" };
        var ok = new Button { Content = "导入", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "每行一个账号，支持：名称,UID；名称 UID；名称（UID:123456）；名称,UID,分成比例。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(editor);
        panel.Children.Add(ok);
        dialog.Content = panel;
        ok.Click += (_, _) => dialog.Close(editor.Text);
        var text = await dialog.ShowDialog<string?>(TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException("无法打开导入窗口。"));
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var row in ParseDistributorLines(text)) _distributorAccounts.Add(row);
    }

    private void LoadDistributorAccounts(string? json)
    {
        _distributorAccounts.Clear();
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                _distributorAccounts.Add(new DistributorAccountEditorRow
                {
                    Enabled = !item.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False,
                    Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    Uid = item.TryGetProperty("uid", out var uid) ? uid.ToString() : "",
                    Rate = item.TryGetProperty("rate_percent", out var rate) ? rate.ToString() : "",
                });
            }
        }
        catch
        {
            foreach (var row in ParseDistributorLines(json)) _distributorAccounts.Add(row);
        }
    }

    private string SerializeDistributorAccounts()
    {
        var rows = _distributorAccounts
            .Where(row => !string.IsNullOrWhiteSpace(row.Name) || !string.IsNullOrWhiteSpace(row.Uid))
            .Select(row =>
            {
                var payload = new Dictionary<string, object?>
                {
                    ["enabled"] = row.Enabled,
                    ["name"] = row.Name.Trim(),
                    ["uid"] = row.Uid.Trim(),
                };
                if (double.TryParse(row.Rate, out var rate)) payload["rate_percent"] = rate;
                return payload;
            })
            .ToArray();
        return rows.Length == 0 ? string.Empty : JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
    }

    private static IEnumerable<DistributorAccountEditorRow> ParseDistributorLines(string text)
    {
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            string name;
            string uid;
            string rate = "";
            var uidMatch = System.Text.RegularExpressions.Regex.Match(line, @"UID\s*[:：]\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (uidMatch.Success)
            {
                uid = uidMatch.Groups[1].Value;
                name = line[..uidMatch.Index].Trim().Trim('（', '(', '，', ',', ' ');
            }
            else
            {
                var parts = line.Split([',', '，', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                name = parts.ElementAtOrDefault(0) ?? "";
                uid = parts.ElementAtOrDefault(1) ?? "";
                rate = parts.ElementAtOrDefault(2) ?? "";
            }
            yield return new DistributorAccountEditorRow { Name = name, Uid = uid, Rate = rate };
        }
    }

    private IEnumerable<string> NoticeConditions()
    {
        if (OnlineNotifyBox.IsChecked == true) yield return "online_success";
        if (RejectedNotifyBox.IsChecked == true) yield return "audit_rejected";
    }

    private static void FillChoice(ComboBox box, IReadOnlyList<KuaishouFormChoice> choices, string? raw, string fallback)
    {
        var value = KuaishouFormChoices.CanonicalValue(choices, raw, fallback);
        var items = choices.ToList();
        if (items.All(choice => !string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase)))
            items.Add(new KuaishouFormChoice(string.IsNullOrWhiteSpace(raw) ? value : raw!.Trim(), value));
        box.ItemsSource = items;
        box.SelectedItem = items.First(choice => string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase));
    }

    private static string ChoiceValue(ComboBox box, string fallback) =>
        box.SelectedItem is KuaishouFormChoice choice && !string.IsNullOrWhiteSpace(choice.Value)
            ? choice.Value
            : fallback;

    private static void FillTemplate(ComboBox box, string? templateId)
    {
        var options = TikTokProjectImageTemplateCatalog.BuiltInOptions
            .Select(option => new KuaishouFormChoice(option.SelectionLabel, option.Id))
            .ToList();
        var value = string.IsNullOrWhiteSpace(templateId) ? options[0].Value : templateId.Trim();
        if (options.All(option => !string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)))
            options.Add(new KuaishouFormChoice($"已保存：{value}", value));
        box.ItemsSource = options;
        box.SelectedItem = options.First(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase));
    }

    private static string TemplateValue(ComboBox box) =>
        box.SelectedItem is KuaishouFormChoice choice ? choice.Value : string.Empty;

    private static string NormalizeSaleType(string? raw)
    {
        var text = raw?.Trim() ?? "";
        if (text.Contains("广告", StringComparison.Ordinal)) return "3";
        if (text.Contains("单集", StringComparison.Ordinal)) return "2";
        if (text.Contains("全剧", StringComparison.Ordinal)) return "0";
        return text;
    }

    private static bool IsTruthy(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "是";

    private static void SetRowVisible(TextBlock label, TextBox box, bool visible)
    {
        label.IsVisible = visible;
        box.IsVisible = visible;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static int Positive(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static int NonNegative(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;

    private void Cancel_Click(object? sender, RoutedEventArgs e) => LoadAccount(_account, _platform);

    private async void OnPersonalEditionClick(object? sender, RoutedEventArgs e) =>
        await SwitchEditionAsync(PublishPlatform.KuaishouPersonalRevenue);

    private async void OnEnterpriseEditionClick(object? sender, RoutedEventArgs e) =>
        await SwitchEditionAsync(PublishPlatform.KuaishouEnterpriseRevenue);

    private async Task SwitchEditionAsync(PublishPlatform platform)
    {
        if (platform == _platform) return;
        if (TopLevel.GetTopLevel(this) is Window owner && !await TryLeaveAsync(owner)) return;
        LoadAccount(_account, platform);
    }

    private async Task<bool> SaveCurrentAsync()
    {
        if (_account is null)
        {
            ValidationText.Text = "请先在左侧选择账号。";
            return false;
        }
        if (!ApplyToConfig()) return false;
        var issues = KuaishouConfigurationValidator.Validate(_config);
        if (issues.Count > 0)
        {
            ValidationText.Foreground = Avalonia.Media.Brushes.Red;
            ValidationText.Text = string.Join("；", issues);
            return false;
        }
        var path = ResolveConfigPath(createDefault: true);
        await _config.SaveAsync(path);
        CaptureClean();
        ValidationText.Foreground = Avalonia.Media.Brushes.ForestGreen;
        ValidationText.Text = "已保存。";
        if (ConfigSaved is not null) await ConfigSaved(_account.Id, _platform, path);
        return true;
    }

    private KuaishouPersonalConfig LoadStoredConfig()
    {
        if (_account is null) return new KuaishouPersonalConfig();
        var path = ResolveConfigPath(createDefault: false);
        return KuaishouPersonalConfig.Load(new PublishJob
        {
            Platform = _platform,
            AccountId = _account.Id,
            ConfigPath = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : string.Empty,
        });
    }

    private string ResolveConfigPath(bool createDefault)
    {
        var configured = _platform == PublishPlatform.KuaishouEnterpriseRevenue
            ? _account?.KuaishouEnterpriseConfigPath
            : _account?.KuaishouPersonalConfigPath;
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        return createDefault && _account is not null
            ? KuaishouPersonalConfig.DefaultConfigPath(_account.Id, _platform)
            : string.Empty;
    }

    public string? AccountId => _account?.Id;
    public PublishPlatform CurrentPlatform => _platform;

    private bool HasUnsavedChanges()
    {
        if (_suspendDirty) return false;
        if (!ApplyToConfig()) return true;
        return JsonSerializer.Serialize(_config) != _cleanJson;
    }

    private void CaptureClean() => _cleanJson = JsonSerializer.Serialize(_config);

    private void UpdateHeading()
    {
        var edition = _platform == PublishPlatform.KuaishouEnterpriseRevenue ? "企业版" : "个人版";
        AccountHeading.Text = _account is null ? "快手分账配置" : $"{_account.Name} · {edition}";
        PersonalEditionButton.Classes.Set("navActive", _platform != PublishPlatform.KuaishouEnterpriseRevenue);
        EnterpriseEditionButton.Classes.Set("navActive", _platform == PublishPlatform.KuaishouEnterpriseRevenue);
    }

    private IStorageProvider Files =>
        TopLevel.GetTopLevel(this)?.StorageProvider
        ?? throw new InvalidOperationException("当前页面无法选择文件。");
}

public sealed class DistributorAccountEditorRow
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = string.Empty;
    public string Uid { get; set; } = string.Empty;
    public string Rate { get; set; } = string.Empty;
}
