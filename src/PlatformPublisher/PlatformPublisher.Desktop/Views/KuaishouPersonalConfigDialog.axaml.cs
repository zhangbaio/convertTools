using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Desktop.Views;

public partial class KuaishouPersonalConfigDialog : Window
{
    private readonly KuaishouPersonalConfig _config;
    private readonly PublishPlatform _platform;
    public IReadOnlyList<string> GenderOptions { get; } = ["男", "女"];
    public IReadOnlyList<string> FirstPageActions { get; } = ["draft", "next"];
    public IReadOnlyList<string> FinalActions { get; } = ["keep", "submit_review"];
    public IReadOnlyList<string> RunModes { get; } = ["auto", "create", "edit"];
    public IReadOnlyList<string> VideoStorageProviders { get; } = ["browser", "aliyun_oss"];

    public KuaishouPersonalConfigDialog() : this(new KuaishouPersonalConfig(), PublishPlatform.KuaishouPersonalRevenue) { }

    private KuaishouPersonalConfigDialog(KuaishouPersonalConfig config, PublishPlatform platform)
    {
        _config = config;
        _platform = platform;
        DataContext = this;
        InitializeComponent();
        Title = platform.DisplayName() + "配置";
        LoadValues();
    }

    public static Task<KuaishouPersonalConfig?> ShowAsync(
        Window owner,
        KuaishouPersonalConfig config,
        PublishPlatform platform = PublishPlatform.KuaishouPersonalRevenue) =>
        new KuaishouPersonalConfigDialog(config, platform).ShowDialog<KuaishouPersonalConfig?>(owner);

    private void LoadValues()
    {
        EntryUrlBox.Text = _config.EntryUrl;
        ApiBaseUrlBox.Text = _config.ApiBaseUrl;
        AppNameBox.Text = _config.AppName;
        AppIdBox.Text = _config.AppId;
        AppSecretBox.Text = _config.AppSecret;
        AuthorizeBaseUrlBox.Text = _config.AuthorizeBaseUrl;
        AuthCodeBox.Text = _config.AuthCode;
        AccessTokenBox.Text = _config.AccessToken;
        AccessTokenExpiresAtBox.Text = _config.AccessTokenExpiresAt;
        RefreshTokenBox.Text = _config.RefreshToken;
        RefreshTokenExpiresAtBox.Text = _config.RefreshTokenExpiresAt;
        TokenHeaderBox.Text = _config.TokenHeader;
        RemoteTokenBox.IsChecked = _config.RemoteTokenEnabled;
        RealNameBox.Text = _config.RealName;
        GenderBox.SelectedItem = _config.Gender;
        NicknameBox.Text = _config.KuaishouNickname;
        KuaishouIdBox.Text = _config.KuaishouId;
        HeadlessBox.IsChecked = _config.Headless;
        KeepBrowserBox.IsChecked = _config.KeepBrowserOpenOnFailure;
        CommitmentPdfBox.Text = _config.CommitmentPdfPath;
        CommitmentTemplateBox.Text = _config.CommitmentTemplateDocxPath;
        CommitmentSealBox.Text = _config.CommitmentSealPath;
        CommitmentRecipientBox.Text = _config.CommitmentRecipientCompanyName;
        CategoryBox.Text = _config.Category;
        ContentTypeBox.Text = _config.ContentType;
        ProductionMethodBox.Text = _config.ProductionMethod;
        ProductionFormBox.Text = _config.ProductionForm;
        ProductionYearBox.Text = _config.ProductionYear;
        ProductionCostBox.Text = _config.ProductionCost;
        AverageDurationBox.Text = _config.AverageEpisodeMinutes;
        BroadcastPlatformBox.Text = _config.BroadcastPlatform;
        BroadcastChannelBox.Text = _config.BroadcastChannel;
        BroadcastDateBox.Text = _config.BroadcastDate;
        FinishedBox.IsChecked = _config.Finished;
        HasRecordNumberBox.IsChecked = _config.HasRecordNumber;
        ActorsBox.Text = _config.Actors;
        PeopleActorsBox.Text = _config.Actors;
        DirectorsBox.Text = _config.Directors;
        ScreenwritersBox.Text = _config.Screenwriters;
        ProductionOrganizationBox.Text = _config.ProductionOrganization;
        AudienceGenderBox.Text = _config.AudienceGender;
        PlotLabelsBox.Text = _config.PlotLabels;
        TagLabelsBox.Text = _config.TagLabels;
        AuthorDeclarationBox.Text = _config.AuthorDeclaration;
        HasCopyrightProofBox.IsChecked = _config.HasCopyrightProof;
        HasSubAuthorizationBox.IsChecked = _config.HasSubAuthorizationRight;
        FullSceneDisplayBox.IsChecked = _config.FullSceneDisplay;
        SaleTypeBox.Text = _config.SaleType;
        EpisodePriceBox.Text = _config.EpisodePrice;
        FreeEpisodeCountBox.Text = _config.FreeEpisodeCount.ToString();
        UnlockEpisodeCountBox.Text = _config.UnlockEpisodeCount.ToString();
        FirstPageActionBox.SelectedItem = _config.FirstPageAction;
        FinalActionBox.SelectedItem = _config.FinalAction;
        RunModeBox.SelectedItem = _config.RunMode;
        UploadTimeoutBox.Text = _config.UploadTimeoutMinutes.ToString();
        ForceRerunBox.IsChecked = _config.ForceRerun;
        LoginTimeoutBox.Text = _config.LoginTimeoutSeconds.ToString();
        QueueParallelBox.Text = _config.QueueMaxParallelProjects.ToString();
        SubmitPreCheckBox.Text = _config.SubmitPreCheckWaitSeconds.ToString();
        SubmitReadyIntervalBox.Text = _config.SubmitReadyCheckIntervalSeconds.ToString();
        SubmitReadyMaxBox.Text = _config.SubmitReadyCheckMax.ToString();
        SubmitRetryMaxBox.Text = _config.SubmitRetryMax.ToString();
        SubmitRetryIntervalBox.Text = _config.SubmitRetryIntervalSeconds.ToString();
        VideoStorageProviderBox.SelectedItem = _config.VideoStorageProvider;
        StorageConcurrencyBox.Text = _config.PublicStorageUploadConcurrency.ToString();
        StorageRetryBox.Text = _config.PublicStorageUploadRetryCount.ToString();
        OssCleanupBox.IsChecked = _config.OssCleanupEnabled;
        OssDeleteOnProjectDeleteBox.IsChecked = _config.OssCleanupDeleteOnProjectDelete;
        ProjectImageRootBox.Text = _config.ProjectImageTemplateRoot;
        ProjectImageTemplateIdBox.Text = _config.ProjectImageTemplateId;
        PrepareDownloadBox.IsChecked = _config.PrepareDownload;
        PrepareRewriteBox.IsChecked = _config.PrepareRewriteInfo;
        PreparePosterBox.IsChecked = _config.PrepareGeneratePoster;
        PrepareGuaranteeBox.IsChecked = _config.PrepareGenerateGuaranteeLetter;
        PrepareProjectImagesBox.IsChecked = _config.PrepareGenerateProjectImages;
        PrepareAutoFillBox.IsChecked = _config.PrepareAutoFillInfo;
        PrepareForceRerunBox.IsChecked = _config.PrepareForceRerunCompletedSteps;
        AutoOnlineBox.IsChecked = _config.AutoOnlineEnabled;
        AutoOnlineIntervalBox.Text = _config.AutoOnlineIntervalMinutes.ToString();
        AutoOnlineMaxItemsBox.Text = _config.AutoOnlineMaxItemsPerRound.ToString();
        AutoOnlineMaxWaitBox.Text = _config.AutoOnlineMaxWaitDays.ToString();
        OnlineCleanupBox.IsChecked = _config.OnlineCleanupEnabled;
        OnlineAutoDistributionBox.IsChecked = _config.OnlineAutoDistributionEnabled;
        KeepOnlineDaysBox.Text = _config.OnlineKeepOnlineDays.ToString();
        KeepRejectedDaysBox.Text = _config.OnlineKeepRejectedDays.ToString();
        KeepInvalidDaysBox.Text = _config.OnlineKeepInvalidDays.ToString();
        KeepManualDaysBox.Text = _config.OnlineKeepManualOnlineDays.ToString();
        OnlineNotifyBox.IsChecked = _config.OnlineNotifyEnabled;
        RejectedNotifyBox.IsChecked = _config.OnlineNotifyRejectedEnabled;
        RejectedNotifyOnceBox.IsChecked = _config.OnlineNotifyRejectedOnce;
        SeriesCreatePathBox.Text = _config.SeriesCreatePath;
        SeriesBaseInfoPathBox.Text = _config.SeriesBaseInfoPath;
        EpisodeUploadPathBox.Text = _config.EpisodeUploadPath;
        EpisodeCoverPathBox.Text = _config.EpisodeCoverUpdatePath;
        SeriesSubmitPathBox.Text = _config.SeriesSubmitPath;
        EpisodeFileFieldBox.Text = _config.EpisodeFileFieldName;
        StepCreateSeriesBox.IsChecked = _config.StepCreateSeries;
        StepUploadImagesBox.IsChecked = _config.StepUploadImages;
        StepUploadVideosBox.IsChecked = _config.StepUploadVideos;
        StepSubmitSeriesBox.IsChecked = _config.StepSubmitSeries;
        StepOnlineSeriesBox.IsChecked = _config.StepOnlineSeries;
        StepDistributionSeriesBox.IsChecked = _config.StepDistributionSeries;
        DistributionEnabledBox.IsChecked = _config.DistributionEnabled;
        DistributionApiPathBox.Text = _config.DistributionApiPath;
        DistributionModeBox.Text = _config.DistributionMode;
        DistributionRateBox.Text = _config.DistributionDefaultRatePercent.ToString();
        DistributorAccountsBox.Text = _config.DistributionDistributorAccountsJson;
        DistributionSubmitBox.IsChecked = _config.DistributionSubmitEnabled;
        DistributionJuxingBox.IsChecked = _config.DistributionAllowJuxing;
        DistributionOnlineTimeBox.IsChecked = _config.DistributionAllowOnlineTime;
        AiComplianceBox.IsChecked = _config.AiComplianceReviewEnabled;
        SynopsisRewriteBox.IsChecked = _config.SynopsisAiRewriteEnabled;
        CopyrightProofTypeBox.Text = _config.CopyrightProofType;
        CopyrightStartBox.Text = _config.CopyrightValidStartTime;
        CopyrightEndBox.Text = _config.CopyrightValidEndTime;
        SynopsisPolicyBox.Text = _config.SynopsisPolicyJson;
    }

    private async void PickCommitmentPdf_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择快手分账个人版承诺函 PDF",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PDF 文件") { Patterns = ["*.pdf"] }],
        });
        if (files.Count > 0) CommitmentPdfBox.Text = files[0].Path.LocalPath;
    }

    private async void PickCommitmentTemplate_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择快手承诺函 Word 模板",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Word 模板") { Patterns = ["*.docx"] }],
        });
        if (files.Count > 0) CommitmentTemplateBox.Text = files[0].Path.LocalPath;
    }

    private async void PickCommitmentSeal_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择承诺函印章图片",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }],
        });
        if (files.Count > 0) CommitmentSealBox.Text = files[0].Path.LocalPath;
    }

    private async void ImportLegacy_Click(object? sender, RoutedEventArgs e)
    {
        var path = KuaishouLegacyConfigImporter.DefaultSettingsPath;
        if (!File.Exists(path))
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FreeEpisodeCountBox.Text, out var free) || free < 0 ||
            !int.TryParse(UnlockEpisodeCountBox.Text, out var unlock) || unlock < 0 ||
            !int.TryParse(UploadTimeoutBox.Text, out var timeout) || timeout is < 5 or > 240)
        {
            ValidationText.Text = "免费集数、解锁集数必须为非负整数；上传超时必须为 5–240 分钟。";
            return;
        }
        if (!string.IsNullOrWhiteSpace(CommitmentPdfBox.Text) &&
            (!File.Exists(CommitmentPdfBox.Text) || !string.Equals(Path.GetExtension(CommitmentPdfBox.Text), ".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            ValidationText.Text = "承诺函必须是存在的 PDF 文件。";
            return;
        }

        _config.EntryUrl = EntryUrlBox.Text?.Trim() ?? string.Empty;
        _config.ApiBaseUrl = ApiBaseUrlBox.Text?.Trim() ?? string.Empty;
        _config.AppName = AppNameBox.Text?.Trim() ?? string.Empty;
        _config.AppId = AppIdBox.Text?.Trim() ?? string.Empty;
        _config.AppSecret = AppSecretBox.Text ?? string.Empty;
        _config.AuthorizeBaseUrl = AuthorizeBaseUrlBox.Text?.Trim() ?? string.Empty;
        _config.AuthCode = AuthCodeBox.Text ?? string.Empty;
        _config.AccessToken = AccessTokenBox.Text ?? string.Empty;
        _config.AccessTokenExpiresAt = AccessTokenExpiresAtBox.Text?.Trim() ?? string.Empty;
        _config.RefreshToken = RefreshTokenBox.Text ?? string.Empty;
        _config.RefreshTokenExpiresAt = RefreshTokenExpiresAtBox.Text?.Trim() ?? string.Empty;
        _config.TokenHeader = TokenHeaderBox.Text?.Trim() ?? "Access-Token";
        _config.RemoteTokenEnabled = RemoteTokenBox.IsChecked == true;
        _config.RealName = RealNameBox.Text?.Trim() ?? string.Empty;
        _config.Gender = GenderBox.SelectedItem?.ToString() ?? "男";
        _config.KuaishouNickname = NicknameBox.Text?.Trim() ?? string.Empty;
        _config.KuaishouId = KuaishouIdBox.Text?.Trim() ?? string.Empty;
        _config.Headless = HeadlessBox.IsChecked == true;
        _config.KeepBrowserOpenOnFailure = KeepBrowserBox.IsChecked == true;
        _config.CommitmentPdfPath = CommitmentPdfBox.Text?.Trim() ?? string.Empty;
        _config.CommitmentTemplateDocxPath = CommitmentTemplateBox.Text?.Trim() ?? string.Empty;
        _config.CommitmentSealPath = CommitmentSealBox.Text?.Trim() ?? string.Empty;
        _config.CommitmentRecipientCompanyName = CommitmentRecipientBox.Text?.Trim() ?? string.Empty;
        _config.Category = CategoryBox.Text?.Trim() ?? string.Empty;
        _config.ContentType = ContentTypeBox.Text?.Trim() ?? string.Empty;
        _config.ProductionMethod = ProductionMethodBox.Text?.Trim() ?? string.Empty;
        _config.ProductionForm = ProductionFormBox.Text?.Trim() ?? string.Empty;
        _config.ProductionYear = ProductionYearBox.Text?.Trim() ?? string.Empty;
        _config.ProductionCost = ProductionCostBox.Text?.Trim() ?? string.Empty;
        _config.AverageEpisodeMinutes = AverageDurationBox.Text?.Trim() ?? string.Empty;
        _config.BroadcastPlatform = BroadcastPlatformBox.Text?.Trim() ?? string.Empty;
        _config.BroadcastChannel = BroadcastChannelBox.Text?.Trim() ?? string.Empty;
        _config.BroadcastDate = BroadcastDateBox.Text?.Trim() ?? string.Empty;
        _config.Finished = FinishedBox.IsChecked == true;
        _config.HasRecordNumber = HasRecordNumberBox.IsChecked == true;
        _config.Actors = FirstNonEmpty(PeopleActorsBox.Text, ActorsBox.Text);
        _config.Directors = DirectorsBox.Text?.Trim() ?? string.Empty;
        _config.Screenwriters = ScreenwritersBox.Text?.Trim() ?? string.Empty;
        _config.ProductionOrganization = ProductionOrganizationBox.Text?.Trim() ?? string.Empty;
        _config.AudienceGender = AudienceGenderBox.Text?.Trim() ?? string.Empty;
        _config.PlotLabels = PlotLabelsBox.Text?.Trim() ?? string.Empty;
        _config.TagLabels = TagLabelsBox.Text?.Trim() ?? string.Empty;
        _config.AuthorDeclaration = AuthorDeclarationBox.Text?.Trim() ?? string.Empty;
        _config.HasCopyrightProof = HasCopyrightProofBox.IsChecked == true;
        _config.HasSubAuthorizationRight = HasSubAuthorizationBox.IsChecked == true;
        _config.FullSceneDisplay = FullSceneDisplayBox.IsChecked == true;
        _config.SaleType = SaleTypeBox.Text?.Trim() ?? string.Empty;
        _config.EpisodePrice = EpisodePriceBox.Text?.Trim() ?? string.Empty;
        _config.FreeEpisodeCount = free;
        _config.UnlockEpisodeCount = unlock;
        _config.FirstPageAction = FirstPageActionBox.SelectedItem?.ToString() ?? "draft";
        _config.FinalAction = FinalActionBox.SelectedItem?.ToString() ?? "keep";
        _config.RunMode = RunModeBox.SelectedItem?.ToString() ?? "auto";
        _config.UploadTimeoutMinutes = timeout;
        _config.ForceRerun = ForceRerunBox.IsChecked == true;
        _config.LoginTimeoutSeconds = Positive(LoginTimeoutBox.Text, 180);
        _config.QueueMaxParallelProjects = Positive(QueueParallelBox.Text, 1);
        _config.SubmitPreCheckWaitSeconds = NonNegative(SubmitPreCheckBox.Text, 3);
        _config.SubmitReadyCheckIntervalSeconds = Positive(SubmitReadyIntervalBox.Text, 5);
        _config.SubmitReadyCheckMax = Positive(SubmitReadyMaxBox.Text, 60);
        _config.SubmitRetryMax = NonNegative(SubmitRetryMaxBox.Text, 3);
        _config.SubmitRetryIntervalSeconds = Positive(SubmitRetryIntervalBox.Text, 5);
        _config.VideoStorageProvider = VideoStorageProviderBox.SelectedItem?.ToString() ?? "browser";
        _config.PublicStorageUploadConcurrency = Positive(StorageConcurrencyBox.Text, 3);
        _config.PublicStorageUploadRetryCount = NonNegative(StorageRetryBox.Text, 3);
        _config.OssCleanupEnabled = OssCleanupBox.IsChecked == true;
        _config.OssCleanupDeleteOnProjectDelete = OssDeleteOnProjectDeleteBox.IsChecked == true;
        _config.ProjectImageTemplateRoot = ProjectImageRootBox.Text?.Trim() ?? string.Empty;
        _config.ProjectImageTemplateId = ProjectImageTemplateIdBox.Text?.Trim() ?? string.Empty;
        _config.PrepareDownload = PrepareDownloadBox.IsChecked == true;
        _config.PrepareRewriteInfo = PrepareRewriteBox.IsChecked == true;
        _config.PrepareGeneratePoster = PreparePosterBox.IsChecked == true;
        _config.PrepareGenerateGuaranteeLetter = PrepareGuaranteeBox.IsChecked == true;
        _config.PrepareGenerateProjectImages = PrepareProjectImagesBox.IsChecked == true;
        _config.PrepareAutoFillInfo = PrepareAutoFillBox.IsChecked == true;
        _config.PrepareForceRerunCompletedSteps = PrepareForceRerunBox.IsChecked == true;
        _config.AutoOnlineEnabled = AutoOnlineBox.IsChecked == true;
        _config.AutoOnlineIntervalMinutes = Positive(AutoOnlineIntervalBox.Text, 30);
        _config.AutoOnlineMaxItemsPerRound = Positive(AutoOnlineMaxItemsBox.Text, 20);
        _config.AutoOnlineMaxWaitDays = Positive(AutoOnlineMaxWaitBox.Text, 7);
        _config.OnlineCleanupEnabled = OnlineCleanupBox.IsChecked == true;
        _config.OnlineAutoDistributionEnabled = OnlineAutoDistributionBox.IsChecked == true;
        _config.OnlineKeepOnlineDays = NonNegative(KeepOnlineDaysBox.Text, 30);
        _config.OnlineKeepRejectedDays = NonNegative(KeepRejectedDaysBox.Text, 30);
        _config.OnlineKeepInvalidDays = NonNegative(KeepInvalidDaysBox.Text, 30);
        _config.OnlineKeepManualOnlineDays = NonNegative(KeepManualDaysBox.Text, 30);
        _config.OnlineNotifyEnabled = OnlineNotifyBox.IsChecked == true;
        _config.OnlineNotifyRejectedEnabled = RejectedNotifyBox.IsChecked == true;
        _config.OnlineNotifyRejectedOnce = RejectedNotifyOnceBox.IsChecked == true;
        _config.SeriesCreatePath = SeriesCreatePathBox.Text?.Trim() ?? string.Empty;
        _config.SeriesBaseInfoPath = SeriesBaseInfoPathBox.Text?.Trim() ?? string.Empty;
        _config.EpisodeUploadPath = EpisodeUploadPathBox.Text?.Trim() ?? string.Empty;
        _config.EpisodeCoverUpdatePath = EpisodeCoverPathBox.Text?.Trim() ?? string.Empty;
        _config.SeriesSubmitPath = SeriesSubmitPathBox.Text?.Trim() ?? string.Empty;
        _config.EpisodeFileFieldName = EpisodeFileFieldBox.Text?.Trim() ?? string.Empty;
        _config.StepCreateSeries = StepCreateSeriesBox.IsChecked == true;
        _config.StepUploadImages = StepUploadImagesBox.IsChecked == true;
        _config.StepUploadVideos = StepUploadVideosBox.IsChecked == true;
        _config.StepSubmitSeries = StepSubmitSeriesBox.IsChecked == true;
        _config.StepOnlineSeries = StepOnlineSeriesBox.IsChecked == true;
        _config.StepDistributionSeries = StepDistributionSeriesBox.IsChecked == true;
        _config.DistributionEnabled = DistributionEnabledBox.IsChecked == true;
        _config.DistributionApiPath = DistributionApiPathBox.Text?.Trim() ?? string.Empty;
        _config.DistributionMode = DistributionModeBox.Text?.Trim() ?? "api";
        _config.DistributionDefaultRatePercent = NonNegative(DistributionRateBox.Text, 0);
        _config.DistributionDistributorAccountsJson = DistributorAccountsBox.Text?.Trim() ?? string.Empty;
        _config.DistributionSubmitEnabled = DistributionSubmitBox.IsChecked == true;
        _config.DistributionAllowJuxing = DistributionJuxingBox.IsChecked == true;
        _config.DistributionAllowOnlineTime = DistributionOnlineTimeBox.IsChecked == true;
        _config.AiComplianceReviewEnabled = AiComplianceBox.IsChecked == true;
        _config.SynopsisAiRewriteEnabled = SynopsisRewriteBox.IsChecked == true;
        _config.CopyrightProofType = CopyrightProofTypeBox.Text?.Trim() ?? string.Empty;
        _config.CopyrightValidStartTime = CopyrightStartBox.Text?.Trim() ?? string.Empty;
        _config.CopyrightValidEndTime = CopyrightEndBox.Text?.Trim() ?? string.Empty;
        _config.SynopsisPolicyJson = SynopsisPolicyBox.Text?.Trim() ?? string.Empty;
        var issues = KuaishouConfigurationValidator.Validate(_config);
        if (issues.Count > 0)
        {
            ValidationText.Foreground = Avalonia.Media.Brushes.Red;
            ValidationText.Text = string.Join("；", issues);
            return;
        }
        Close(_config);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static int Positive(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static int NonNegative(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
