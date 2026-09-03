using System.Text.Json;
using System.Text.Json.Serialization;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouPersonalConfig
{
    private static AccountJsonSettingStore? DatabaseStore;
    private static KuaishouCredentialStore? CredentialStore;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    public string EntryUrl { get; set; } = "https://kdj.kuaishou.com/home/content/content-management";
    public string ApiBaseUrl { get; set; } = "https://ad.e.kuaishou.com";
    public string AppName { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    [JsonIgnore] public string AppSecret { get; set; } = string.Empty;
    public string AuthorizeBaseUrl { get; set; } = "https://developers.e.kuaishou.com/tools/authorize";
    public string AuthorizeRedirectUri { get; set; } = string.Empty;
    public string AuthorizeScope { get; set; } = string.Empty;
    public string AuthorizeState { get; set; } = string.Empty;
    public string AuthorizeOauthType { get; set; } = string.Empty;
    [JsonIgnore] public string AuthCode { get; set; } = string.Empty;
    [JsonIgnore] public string AccessToken { get; set; } = string.Empty;
    public string AccessTokenExpiresAt { get; set; } = string.Empty;
    [JsonIgnore] public string RefreshToken { get; set; } = string.Empty;
    public string RefreshTokenExpiresAt { get; set; } = string.Empty;
    public string TokenHeader { get; set; } = "Access-Token";
    public bool RemoteTokenEnabled { get; set; }
    public string AdvertiserId { get; set; } = string.Empty;
    public string AuthStatePath { get; set; } = string.Empty;
    public string BrowserProfileDirectory { get; set; } = string.Empty;
    public bool Headless { get; set; }
    public bool KeepBrowserOpenOnFailure { get; set; } = true;
    public string CommitmentPdfPath { get; set; } = string.Empty;
    public string CommitmentTemplateDocxPath { get; set; } = string.Empty;
    public string CommitmentSealPath { get; set; } = string.Empty;
    public string CommitmentRecipientCompanyName { get; set; } = string.Empty;
    public string RealName { get; set; } = string.Empty;
    public string Gender { get; set; } = "男";
    public string KuaishouNickname { get; set; } = string.Empty;
    public string KuaishouId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string LastWorkspace { get; set; } = string.Empty;
    public string ArchiveRootDirectory { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string CategoryLevel1 { get; set; } = string.Empty;
    public string CategoryLevel2 { get; set; } = string.Empty;
    public string CategoryLevel3 { get; set; } = string.Empty;
    public string ContentType { get; set; } = "漫剧";
    public string ProductionMethod { get; set; } = "AIGC剧";
    public string ProductMethod { get; set; } = string.Empty;
    public string SeriesContentType { get; set; } = string.Empty;
    public string ProductionForm { get; set; } = "竖屏";
    public string ProductionYear { get; set; } = DateTime.Now.Year.ToString();
    public string ProductionCost { get; set; } = "10";
    public string AverageEpisodeMinutes { get; set; } = "2";
    public bool Finished { get; set; } = true;
    public bool HasRecordNumber { get; set; }
    public string BroadcastPlatform { get; set; } = "快手";
    public string BroadcastChannel { get; set; } = "小屏小程序";
    public string BroadcastDate { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public string SaleType { get; set; } = "观看广告解锁";
    public int FreeEpisodeCount { get; set; } = 3;
    public int UnlockEpisodeCount { get; set; } = 1;
    public string EpisodePrice { get; set; } = "1";
    public string SeriesPrice { get; set; } = string.Empty;
    public string Actors { get; set; } = "演员A:男:男主;演员B:女:女主";
    public string ActorInfoJson { get; set; } = string.Empty;
    public string ActorLibraryText { get; set; } = string.Empty;
    public string Directors { get; set; } = string.Empty;
    public string DirectorInfoJson { get; set; } = string.Empty;
    public string Screenwriters { get; set; } = string.Empty;
    public string ScreenwriterInfoJson { get; set; } = string.Empty;
    public string ProductionOrganization { get; set; } = string.Empty;
    public string AudienceGender { get; set; } = "男频";
    public string PlotLabels { get; set; } = string.Empty;
    public string TagLabels { get; set; } = string.Empty;
    public string AuthorDeclaration { get; set; } = string.Empty;
    public bool HasCopyrightProof { get; set; }
    public string CopyrightProofType { get; set; } = string.Empty;
    public string CopyrightValidStartTime { get; set; } = string.Empty;
    public string CopyrightValidEndTime { get; set; } = string.Empty;
    public bool HasSubAuthorizationRight { get; set; }
    public bool FullSceneDisplay { get; set; }
    public bool RevenueShareFullSceneDisplay { get; set; }
    public bool RevenueShareHeadless { get; set; }
    public string BroadcastTimeMode { get; set; } = string.Empty;
    public string SpecialTheme { get; set; } = string.Empty;
    public bool SmallAmountUnlock { get; set; }
    public bool NewTitleAliasEnabled { get; set; }
    public string NewTitleAliasPosition { get; set; } = string.Empty;
    public string NewTitleAliasValue { get; set; } = string.Empty;
    public int AuthorBackfillConcurrent { get; set; } = 1;
    public string FormOptionsJson { get; set; } = string.Empty;
    public string DefaultFieldsJson { get; set; } = string.Empty;
    public bool AuditRecordIncludeDate { get; set; }
    public bool AuditRecordOnlyReviewing { get; set; }
    public bool AuditRecordHeadless { get; set; }
    public int AuditRecordPages { get; set; } = 1;
    public string SettlementFullInfoSystemPrompt { get; set; } = string.Empty;
    public string SettlementFullInfoBatchPrompt { get; set; } = string.Empty;
    public string SettlementFullInfoRetryPrompt { get; set; } = string.Empty;
    public string FirstPageAction { get; set; } = "draft";
    public string FinalAction { get; set; } = "keep";
    public int UploadTimeoutMinutes { get; set; } = 60;
    public bool ForceRerun { get; set; }
    public string RunMode { get; set; } = "auto";
    public int LoginTimeoutSeconds { get; set; } = 180;
    public int QueueMaxParallelProjects { get; set; } = 1;
    public int SubmitPreCheckWaitSeconds { get; set; } = 3;
    public int SubmitReadyCheckIntervalSeconds { get; set; } = 5;
    public int SubmitReadyCheckMax { get; set; } = 60;
    public int SubmitRetryIntervalSeconds { get; set; } = 5;
    public int SubmitRetryMax { get; set; } = 3;
    public string VideoStorageProvider { get; set; } = "browser";
    public int PublicStorageUploadConcurrency { get; set; } = 3;
    public int PublicStorageUploadRetryCount { get; set; } = 3;
    public string ProjectImageTemplateRoot { get; set; } = string.Empty;
    public string ProjectImageTemplateId { get; set; } = string.Empty;
    public bool PrepareDownload { get; set; }
    public bool PrepareRewriteInfo { get; set; } = true;
    public bool PrepareGeneratePoster { get; set; } = true;
    public bool PrepareGenerateGuaranteeLetter { get; set; } = true;
    public bool PrepareGenerateProjectImages { get; set; } = true;
    public bool PrepareAutoFillInfo { get; set; } = true;
    public bool PrepareForceRerunCompletedSteps { get; set; }
    public string SeriesCreatePath { get; set; } = string.Empty;
    public string SeriesBaseInfoPath { get; set; } = string.Empty;
    public string EpisodeUploadPath { get; set; } = string.Empty;
    public string EpisodeCoverUpdatePath { get; set; } = string.Empty;
    public string SeriesSubmitPath { get; set; } = string.Empty;
    public string MaterialUploadPath { get; set; } = string.Empty;
    public string EpisodeFileFieldName { get; set; } = string.Empty;
    public bool StepCreateSeries { get; set; } = true;
    public bool StepUploadImages { get; set; } = true;
    public bool StepUploadVideos { get; set; } = true;
    public bool StepUploadVideosOss { get; set; }
    public bool StepSubmitSeries { get; set; } = true;
    public bool StepOnlineSeries { get; set; }
    public bool StepDistributionSeries { get; set; }
    public bool StepDeleteOssVideos { get; set; }
    public bool DistributionEnabled { get; set; }
    public string DistributionApiPath { get; set; } = "/rest/openapi/gw/dsp/series/distribution/supplier/create/config";
    public string DistributionMode { get; set; } = "api";
    public int DistributionDefaultRatePercent { get; set; }
    public string DistributionDistributorAccountsJson { get; set; } = string.Empty;
    public bool DistributionSubmitEnabled { get; set; }
    public bool DistributionAllowJuxing { get; set; }
    public bool DistributionAllowOnlineTime { get; set; }
    public bool DistributionHeadlessEnabled { get; set; }
    public int DistributionLoginTimeoutSeconds { get; set; } = 180;
    public bool DistributionManualInterventionEnabled { get; set; } = true;
    public bool DistributionLoginNotifyEnabled { get; set; }
    public bool DistributionFeishuNotifyEnabled { get; set; }
    public bool AutoOnlineEnabled { get; set; }
    public int AutoOnlineIntervalMinutes { get; set; } = 30;
    public int AutoOnlineMaxItemsPerRound { get; set; } = 20;
    public int AutoOnlineMaxWaitDays { get; set; } = 7;
    public bool AutoOnlineOnlyWhenIdle { get; set; } = true;
    public bool OnlineAutoDistributionEnabled { get; set; }
    public bool OnlineCleanupEnabled { get; set; }
    public int OnlineKeepOnlineDays { get; set; } = 30;
    public int OnlineKeepRejectedDays { get; set; } = 30;
    public int OnlineKeepInvalidDays { get; set; } = 30;
    public int OnlineKeepManualOnlineDays { get; set; } = 30;
    public bool OnlineNotifyEnabled { get; set; }
    public bool OnlineNotifyRejectedEnabled { get; set; }
    public bool OnlineNotifyRejectedOnce { get; set; } = true;
    public bool OnlineShowResultDialog { get; set; } = true;
    public string OnlineNoticeConditions { get; set; } = string.Empty;
    public string OnlineOfflinePath { get; set; } = string.Empty;
    public bool OssCleanupEnabled { get; set; }
    public bool OssCleanupDeleteOnProjectDelete { get; set; }
    public int OssCleanupIntervalMinutes { get; set; } = 30;
    public int OssCleanupMaxAttempts { get; set; } = 3;
    public int OssCleanupRetentionHours { get; set; } = 24;
    public bool AiComplianceReviewEnabled { get; set; }
    public bool SynopsisAiRewriteEnabled { get; set; }
    public string SynopsisPolicyJson { get; set; } = string.Empty;
    public bool LegacyImportCompleted { get; set; }
    [JsonIgnore] public string StorageAccountId { get; private set; } = string.Empty;
    [JsonIgnore] public PublishPlatform StoragePlatform { get; private set; } = PublishPlatform.KuaishouPersonalRevenue;

    public static void ConfigureDatabase(AccountJsonSettingStore store, KuaishouCredentialStore credentialStore)
    {
        DatabaseStore = store;
        CredentialStore = credentialStore;
    }

    public static KuaishouPersonalConfig Load(PublishJob job)
    {
        var platform = job.Platform == PublishPlatform.KuaishouEnterpriseRevenue
            ? PublishPlatform.KuaishouEnterpriseRevenue
            : PublishPlatform.KuaishouPersonalRevenue;
        var accountKey = string.IsNullOrWhiteSpace(job.AccountId) ? "default" : Safe(job.AccountId);
        var rootName = platform == PublishPlatform.KuaishouEnterpriseRevenue ? "kuaishou-enterprise" : "kuaishou-personal";
        var databaseKey = platform == PublishPlatform.KuaishouEnterpriseRevenue
            ? "kuaishou.enterprise.config"
            : "kuaishou.personal.config";
        var accountRoot = Path.Combine(PlatformPublisherPaths.DataRoot, rootName, "accounts", accountKey);
        Directory.CreateDirectory(accountRoot);
        var configuredPath = !string.IsNullOrWhiteSpace(job.ConfigPath) && File.Exists(job.ConfigPath)
            ? Path.GetFullPath(job.ConfigPath)
            : DefaultConfigPath(job.AccountId, platform);
        KuaishouPersonalConfig config;
        if (DatabaseStore?.TryLoad<KuaishouPersonalConfig>(job.AccountId, databaseKey, out var stored) == true && stored is not null)
            config = stored;
        else if (File.Exists(configuredPath))
        {
            try
            {
                config = JsonSerializer.Deserialize<KuaishouPersonalConfig>(
                             File.ReadAllText(configuredPath),
                             JsonOptions)
                         ?? new KuaishouPersonalConfig();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{platform.DisplayName()}配置文件格式错误：{ex.Message}", ex);
            }
        }
        else config = new KuaishouPersonalConfig();
        config.StorageAccountId = job.AccountId;
        config.StoragePlatform = platform;
        if (CredentialStore is not null && !string.IsNullOrWhiteSpace(job.AccountId))
        {
            var credentials = CredentialStore.Load(job.AccountId, platform);
            config.AppSecret = credentials.AppSecret;
            config.AuthCode = credentials.AuthCode;
            config.AccessToken = credentials.AccessToken;
            config.RefreshToken = credentials.RefreshToken;
        }
        if (DatabaseStore is not null) DatabaseStore.Save(job.AccountId, databaseKey, config);

        config.EntryUrl = string.IsNullOrWhiteSpace(config.EntryUrl)
            ? "https://kdj.kuaishou.com/home/content/content-management"
            : config.EntryUrl.Trim();
        config.AuthStatePath = Resolve(config.AuthStatePath, accountRoot, "kuaishou_personal_kdj_auth_state.json");
        config.BrowserProfileDirectory = Resolve(config.BrowserProfileDirectory, accountRoot, "browser-profile");
        if (!string.IsNullOrWhiteSpace(config.CommitmentPdfPath))
            config.CommitmentPdfPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(config.CommitmentPdfPath));
        Directory.CreateDirectory(Path.GetDirectoryName(config.AuthStatePath)!);
        Directory.CreateDirectory(config.BrowserProfileDirectory);
        return config;
    }

    public static string DefaultConfigPath(string? accountId, PublishPlatform platform = PublishPlatform.KuaishouPersonalRevenue)
    {
        var accountKey = string.IsNullOrWhiteSpace(accountId) ? "default" : Safe(accountId);
        return Path.Combine(
            PlatformPublisherPaths.DataRoot,
            platform == PublishPlatform.KuaishouEnterpriseRevenue ? "kuaishou-enterprise" : "kuaishou-personal",
            "accounts",
            accountKey,
            platform == PublishPlatform.KuaishouEnterpriseRevenue ? "kuaishou-enterprise-config.json" : "kuaishou-personal-config.json");
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, this, JsonOptions, cancellationToken);
        File.Move(temporaryPath, fullPath, true);
        var accountId = string.IsNullOrWhiteSpace(StorageAccountId)
            ? Path.GetFileName(Path.GetDirectoryName(fullPath))
            : StorageAccountId;
        var databaseKey = StoragePlatform == PublishPlatform.KuaishouEnterpriseRevenue
            ? "kuaishou.enterprise.config"
            : "kuaishou.personal.config";
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            DatabaseStore?.Save(accountId, databaseKey, this);
            CredentialStore?.Save(accountId, StoragePlatform,
                new KuaishouCredentials(AppSecret, AuthCode, AccessToken, RefreshToken));
        }
    }

    private static string Resolve(string value, string root, string fallback) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(value)
            ? Path.Combine(root, fallback)
            : Path.IsPathRooted(value) ? value : Path.Combine(root, value));

    private static string Safe(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) value = value.Replace(ch, '_');
        return value;
    }
}
