using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Remote;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class SystemServicesViewModel : ViewModelBase
{
    public event Action<string>? StatusRequested;
    public event Func<TikTokRemoteCommand, Task<TikTokRemoteCommandResult>>? RemoteCommandRequested;
    public event Action? SettingsSaved;

    [ObservableProperty] private string _authServerUrl = "";
    [ObservableProperty] private string _licenseSummary = "未登录";
    [ObservableProperty] private string _loginAccount = "";
    [ObservableProperty] private string _loginPassword = "";
    [ObservableProperty] private string _loginStatus = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _feishuCommandEnabled;
    [ObservableProperty] private string _feishuCommandAppId = "";
    [ObservableProperty] private string _feishuCommandAppSecret = "";
    [ObservableProperty] private string _feishuCommandBotName = "";
    [ObservableProperty] private string _feishuCommandBotAliases = "";
    [ObservableProperty] private bool _feishuCommandRequireBotMention = true;
    [ObservableProperty] private string _feishuCommandAllowedChatIds = "";
    [ObservableProperty] private bool _feishuCommandDirectMessageEnabled = true;
    [ObservableProperty] private string _feishuCommandAllowedUserIds = "";
    [ObservableProperty] private string _feishuCommandDefaultWorkspace = "";
    [ObservableProperty] private bool _feishuCommandReplyEnabled = true;
    [ObservableProperty] private int _feishuCommandCommandTtlSeconds = 60;
    [ObservableProperty] private string _feishuCommandHelpText = ClientSettingsDefaults.FeishuCommandHelpText;
    [ObservableProperty] private string _feishuCommandStatus = "未启用";
    [ObservableProperty] private bool _xingeRemoteEnabled;
    [ObservableProperty] private string _xingeServerUrl = "";
    [ObservableProperty] private string _xingeAccount = "";
    [ObservableProperty] private string _xingePassword = "";
    [ObservableProperty] private string _xingeClientId = "";
    [ObservableProperty] private string _xingeClientToken = "";
    [ObservableProperty] private string _xingeClientName = "TikTokPublisher";
    [ObservableProperty] private int _xingePollIntervalSeconds = 3;
    [ObservableProperty] private string _xingeRemoteStatus = "未启用";
    [ObservableProperty] private string _remoteCommandPreviewText = "";
    [ObservableProperty] private string _remoteCommandPreviewResult = "";
    [ObservableProperty] private bool _remoteDownloadEnabled;
    [ObservableProperty] private bool _remoteRewriteEnabled;
    [ObservableProperty] private bool _remoteGeneratePosterEnabled;
    [ObservableProperty] private bool _remoteGenerateProofMaterialEnabled;
    [ObservableProperty] private bool _remoteSmallVideoRepairEnabled;
    [ObservableProperty] private bool _remoteRemoveSubtitlesEnabled;
    [ObservableProperty] private bool _remoteDubVideoEnabled;
    [ObservableProperty] private bool _remoteAddSubtitlesEnabled;
    [ObservableProperty] private bool _remoteSilenceDetectEnabled;
    [ObservableProperty] private bool _remoteSilenceRepairEnabled;
    [ObservableProperty] private bool _remoteMaterialValidateEnabled;
    [ObservableProperty] private bool _remoteDeleteSourceVideosEnabled;
    [ObservableProperty] private bool _remoteUploadEnabled;
    [ObservableProperty] private bool _remoteAutoArchiveAfterUpload;
    [ObservableProperty] private bool _remoteForceRerunCompletedSteps;
    [ObservableProperty] private bool _remotePreferUploadWhenReady;
    [ObservableProperty] private string _videoTranslateEngine = "volc";
    [ObservableProperty] private string _videoTranslateSourceLanguage = "en";
    [ObservableProperty] private string _videoTranslateTargetLanguage = "zh";
    [ObservableProperty] private string _videoTranslateVolcAccessKeyId = "";
    [ObservableProperty] private string _videoTranslateVolcSecretAccessKey = "";
    [ObservableProperty] private string _videoTranslateLlmBaseUrl = "https://api.deepseek.com/v1";
    [ObservableProperty] private string _videoTranslateLlmApiKey = "";
    [ObservableProperty] private string _videoTranslateLlmModel = "deepseek-chat";
    [ObservableProperty] private string _videoTranslateFont = "微软雅黑";
    [ObservableProperty] private int _videoTranslateFontSize = 50;
    [ObservableProperty] private int _videoTranslateMarginV = 160;
    [ObservableProperty] private bool _videoTranslateBilingual;
    [ObservableProperty] private bool _volcVideoTranslateVoice = true;
    [ObservableProperty] private bool _volcVideoTranslateFacial;
    [ObservableProperty] private int _volcVideoTranslateSubtitleSourceIndex;
    [ObservableProperty] private string _volcVideoTranslateSubtitleFileName = "";
    [ObservableProperty] private bool _volcVideoTranslateHardSubtitle = true;
    [ObservableProperty] private bool _volcVideoTranslateEraseSourceSubtitle;
    [ObservableProperty] private int _volcVideoTranslateFontSize = 30;
    [ObservableProperty] private int _volcVideoTranslateUploadConcurrency = 4;
    [ObservableProperty] private string _subtitleRemovalProvider = "volc";
    [ObservableProperty] private int _subtitleRemovalProviderIndex;
    [ObservableProperty] private int _volcSubtitleEraseModeIndex;
    [ObservableProperty] private string _volcSubtitleAccessKeyId = "";
    [ObservableProperty] private string _volcSubtitleSecretAccessKey = "";
    [ObservableProperty] private string _volcSubtitleRegion = "cn-north-1";
    [ObservableProperty] private string _volcSubtitleSpaceName = "";
    [ObservableProperty] private string _volcSubtitlePlaybackDomain = "";
    [ObservableProperty] private string _volcSubtitleVidManifestPath = "";
    [ObservableProperty] private int _volcSubtitlePollSeconds = 10;
    [ObservableProperty] private string _baiduSubtitleAccessKeyId = "";
    [ObservableProperty] private string _baiduSubtitleSecretAccessKey = "";
    [ObservableProperty] private string _baiduSubtitleEndpoint = "https://vod.bj.baidubce.com";
    [ObservableProperty] private string _baiduSubtitleModelType = "v4";
    [ObservableProperty] private string _baiduSubtitleType = "dialog";
    [ObservableProperty] private int _baiduSubtitleModelTypeIndex;
    [ObservableProperty] private int _baiduSubtitleTypeIndex;
    [ObservableProperty] private string _baiduSubtitleMediaManifestPath = "";
    [ObservableProperty] private int _baiduSubtitlePollSeconds = 10;
    [ObservableProperty] private string _aliyunSubtitleAccessKeyId = "";
    [ObservableProperty] private string _aliyunSubtitleAccessKeySecret = "";
    [ObservableProperty] private string _aliyunSubtitleRegion = "cn-shanghai";
    [ObservableProperty] private string _aliyunSubtitleOssBucket = "";
    [ObservableProperty] private string _aliyunSubtitleOssPrefix = "tiktok-subtitle-removal";
    [ObservableProperty] private bool _aliyunSubtitleAdvancedModel = true;
    [ObservableProperty] private string _aliyunSubtitleLimitRegion = "";
    [ObservableProperty] private string _aliyunSubtitleObjectManifestPath = "";
    [ObservableProperty] private int _aliyunSubtitlePollSeconds = 10;
    [ObservableProperty] private int _videoDubbingProviderIndex;
    [ObservableProperty] private string _elevenLabsApiKey = "";
    [ObservableProperty] private string _elevenLabsSourceLanguage = "zh";
    [ObservableProperty] private string _elevenLabsTargetLanguage = "en";
    [ObservableProperty] private int _elevenLabsPollSeconds = 8;
    [ObservableProperty] private bool _elevenLabsDropBackgroundAudio;
    [ObservableProperty] private bool _elevenLabsDisableVoiceCloning;
    [ObservableProperty] private bool _elevenLabsSpeechRateRepairEnabled;
    [ObservableProperty] private double _elevenLabsSpeechRateTarget = 3.2;
    [ObservableProperty] private double _elevenLabsSpeechRateThreshold = 3.6;
    [ObservableProperty] private double _elevenLabsSpeechRateMaxStretch = 1.2;

    public void Load()
    {
        var settings = ClientSettingsStore.Load();
        var state = LicenseStore.Load();
        AuthServerUrl = FirstNonEmpty(settings.AuthServerUrl, state.ServerUrl);
        LoginAccount = settings.AuthAccount ?? "";
        LoginPassword = settings.AuthPassword ?? "";
        FeishuCommandEnabled = settings.FeishuCommandEnabled;
        FeishuCommandAppId = settings.FeishuCommandAppId ?? "";
        FeishuCommandAppSecret = settings.FeishuCommandAppSecret ?? "";
        FeishuCommandBotName = settings.FeishuCommandBotName ?? "";
        FeishuCommandBotAliases = settings.FeishuCommandBotAliases ?? "";
        FeishuCommandRequireBotMention = settings.FeishuCommandRequireBotMention;
        FeishuCommandAllowedChatIds = settings.FeishuCommandAllowedChatIds ?? "";
        FeishuCommandDirectMessageEnabled = settings.FeishuCommandDirectMessageEnabled;
        FeishuCommandAllowedUserIds = settings.FeishuCommandAllowedUserIds ?? "";
        FeishuCommandDefaultWorkspace = settings.FeishuCommandDefaultWorkspace ?? "";
        FeishuCommandReplyEnabled = settings.FeishuCommandReplyEnabled;
        FeishuCommandCommandTtlSeconds = Math.Clamp(settings.FeishuCommandCommandTtlSeconds, 10, 3600);
        FeishuCommandHelpText = string.IsNullOrWhiteSpace(settings.FeishuCommandHelpText)
            ? ClientSettingsDefaults.FeishuCommandHelpText
            : settings.FeishuCommandHelpText;
        XingeRemoteEnabled = settings.XingeRemoteEnabled;
        XingeServerUrl = FirstNonEmpty(settings.XingeServerUrl, settings.AuthServerUrl, state.ServerUrl);
        XingeAccount = FirstNonEmpty(settings.XingeAccount, settings.AuthAccount);
        XingePassword = !string.IsNullOrEmpty(settings.XingePassword)
            ? settings.XingePassword
            : settings.AuthPassword ?? "";
        XingeClientId = settings.XingeClientId ?? "";
        XingeClientToken = settings.XingeClientToken ?? "";
        XingeClientName = string.IsNullOrWhiteSpace(settings.XingeClientName) ? "TikTokPublisher" : settings.XingeClientName;
        XingePollIntervalSeconds = Math.Clamp(settings.XingePollIntervalSeconds <= 0 ? 3 : settings.XingePollIntervalSeconds, 1, 60);
        RemoteAutoArchiveAfterUpload = settings.FeishuTiktokUploadAutoArchiveAfterUpload;
        RemoteForceRerunCompletedSteps = settings.FeishuTiktokUploadForceRerunCompletedSteps;
        RemotePreferUploadWhenReady = settings.FeishuTiktokUploadPreferUploadWhenReady;
        VideoTranslateEngine = settings.VideoTranslateEngine;
        VideoTranslateSourceLanguage = settings.VideoTranslateSourceLanguage;
        VideoTranslateTargetLanguage = settings.VideoTranslateTargetLanguage;
        VideoTranslateVolcAccessKeyId = settings.VideoTranslateVolcAccessKeyId;
        VideoTranslateVolcSecretAccessKey = settings.VideoTranslateVolcSecretAccessKey;
        VideoTranslateLlmBaseUrl = settings.VideoTranslateLlmBaseUrl;
        VideoTranslateLlmApiKey = settings.VideoTranslateLlmApiKey;
        VideoTranslateLlmModel = settings.VideoTranslateLlmModel;
        VideoTranslateFont = settings.VideoTranslateFont;
        VideoTranslateFontSize = settings.VideoTranslateFontSize;
        VideoTranslateMarginV = settings.VideoTranslateMarginV;
        VideoTranslateBilingual = settings.VideoTranslateBilingual;
        VolcVideoTranslateVoice = settings.VolcVideoTranslateVoice;
        VolcVideoTranslateFacial = settings.VolcVideoTranslateFacial;
        VolcVideoTranslateSubtitleSourceIndex = settings.VolcVideoTranslateSubtitleSource.ToUpperInvariant() switch
        {
            "ASR" => 1,
            "SUBTITLEFILE" => 2,
            _ => 0,
        };
        VolcVideoTranslateSubtitleFileName = settings.VolcVideoTranslateSubtitleFileName;
        VolcVideoTranslateHardSubtitle = settings.VolcVideoTranslateHardSubtitle;
        VolcVideoTranslateEraseSourceSubtitle = settings.VolcVideoTranslateEraseSourceSubtitle;
        VolcVideoTranslateFontSize = Math.Clamp(
            settings.VolcVideoTranslateFontSize <= 0 ? 30 : settings.VolcVideoTranslateFontSize, 16, 36);
        VolcVideoTranslateUploadConcurrency = Math.Clamp(
            settings.VolcVideoTranslateUploadConcurrency <= 0 ? 4 : settings.VolcVideoTranslateUploadConcurrency,
            1,
            10);
        SubtitleRemovalProvider = settings.SubtitleRemovalProvider;
        SubtitleRemovalProviderIndex = settings.SubtitleRemovalProvider switch
        {
            "baidu" => 1,
            "aliyun" => 2,
            _ => 0,
        };
        VolcSubtitleEraseModeIndex = settings.VolcSubtitleEraseMode == "aideo" ? 1 : 0;
        VolcSubtitleAccessKeyId = settings.VolcSubtitleAccessKeyId;
        VolcSubtitleSecretAccessKey = settings.VolcSubtitleSecretAccessKey;
        VolcSubtitleRegion = settings.VolcSubtitleRegion;
        VolcSubtitleSpaceName = settings.VolcSubtitleSpaceName;
        VolcSubtitlePlaybackDomain = settings.VolcSubtitlePlaybackDomain;
        VolcSubtitleVidManifestPath = settings.VolcSubtitleVidManifestPath;
        VolcSubtitlePollSeconds = settings.VolcSubtitlePollSeconds;
        BaiduSubtitleAccessKeyId = settings.BaiduSubtitleAccessKeyId;
        BaiduSubtitleSecretAccessKey = settings.BaiduSubtitleSecretAccessKey;
        BaiduSubtitleEndpoint = settings.BaiduSubtitleEndpoint;
        BaiduSubtitleModelType = settings.BaiduSubtitleModelType;
        BaiduSubtitleType = settings.BaiduSubtitleType;
        BaiduSubtitleModelTypeIndex = settings.BaiduSubtitleModelType == "v3" ? 1 : 0;
        BaiduSubtitleTypeIndex = settings.BaiduSubtitleType == "global" ? 1 : 0;
        BaiduSubtitleMediaManifestPath = settings.BaiduSubtitleMediaManifestPath;
        BaiduSubtitlePollSeconds = settings.BaiduSubtitlePollSeconds;
        AliyunSubtitleAccessKeyId = settings.AliyunSubtitleAccessKeyId;
        AliyunSubtitleAccessKeySecret = settings.AliyunSubtitleAccessKeySecret;
        AliyunSubtitleRegion = settings.AliyunSubtitleRegion;
        AliyunSubtitleOssBucket = settings.AliyunSubtitleOssBucket;
        AliyunSubtitleOssPrefix = settings.AliyunSubtitleOssPrefix;
        AliyunSubtitleAdvancedModel = settings.AliyunSubtitleAdvancedModel;
        AliyunSubtitleLimitRegion = settings.AliyunSubtitleLimitRegion;
        AliyunSubtitleObjectManifestPath = settings.AliyunSubtitleObjectManifestPath;
        AliyunSubtitlePollSeconds = settings.AliyunSubtitlePollSeconds;
        VideoDubbingProviderIndex = settings.VideoDubbingProvider == "volc" ? 1 : 0;
        ElevenLabsApiKey = settings.ElevenLabsApiKey;
        ElevenLabsSourceLanguage = settings.ElevenLabsSourceLanguage;
        ElevenLabsTargetLanguage = settings.ElevenLabsTargetLanguage;
        ElevenLabsPollSeconds = settings.ElevenLabsPollSeconds;
        ElevenLabsDropBackgroundAudio = settings.ElevenLabsDropBackgroundAudio;
        ElevenLabsDisableVoiceCloning = settings.ElevenLabsDisableVoiceCloning;
        ElevenLabsSpeechRateRepairEnabled = settings.ElevenLabsSpeechRateMode == "repair";
        ElevenLabsSpeechRateTarget = settings.ElevenLabsSpeechRateTarget;
        ElevenLabsSpeechRateThreshold = settings.ElevenLabsSpeechRateThreshold;
        ElevenLabsSpeechRateMaxStretch = settings.ElevenLabsSpeechRateMaxStretch;
        ApplyRemoteSteps(TikTokRemoteRunOptions.LoadFeishuTikTokUploadEnabledSteps(settings));
        FeishuCommandStatus = FeishuCommandEnabled ? "已启用" : "未启用";
        XingeRemoteStatus = XingeRemoteEnabled ? "等待连接" : "未启用";
        RefreshLicenseSummary();
    }

    [RelayCommand]
    private void RefreshLicenseSummary()
    {
        var state = LicenseStore.Load();
        if (!state.IsActivated())
        {
            LicenseSummary = "未登录";
            return;
        }

        var account = string.IsNullOrWhiteSpace(state.AccountUsername) ? state.LicenseKey : state.AccountUsername;
        var masked = string.IsNullOrWhiteSpace(state.LicenseKeyMasked)
            ? LicenseStore.MaskLicenseKey(account)
            : state.LicenseKeyMasked;
        LicenseSummary = $"已登录：{masked} | 设备：{state.MachineId[..Math.Min(8, state.MachineId.Length)]}…";
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LoginStatus = "登录中...";
        try
        {
            var settings = ClientSettingsStore.Load();
            ApplyUiToSettings(settings);
            ClientSettingsStore.Save(settings);

            await LicenseAuthService.LoginAsync(settings.AuthServerUrl, LoginAccount, LoginPassword);
            LoginStatus = "登录成功";
            RefreshLicenseSummary();
            StatusRequested?.Invoke("授权登录成功");
        }
        catch (Exception ex)
        {
            LoginStatus = ex.Message;
            StatusRequested?.Invoke(LoginStatus);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task LogoutAsync() => ClearLicenseLoginAsync();

    [RelayCommand]
    private void SaveAuthServerUrl()
    {
        SaveSystemServices();
    }

    [RelayCommand]
    private void SaveSystemServices()
    {
        var settings = ClientSettingsStore.Load();
        ApplyUiToSettings(settings);
        ClientSettingsStore.Save(settings);
        FeishuCommandStatus = settings.FeishuCommandEnabled ? "已启用" : "未启用";
        XingeRemoteStatus = settings.XingeRemoteEnabled ? "等待连接" : "未启用";
        StatusRequested?.Invoke("系统服务配置已保存");
        SettingsSaved?.Invoke();
    }

    [RelayCommand]
    private async Task TestXingeLoginAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        XingeRemoteStatus = "正在使用用户名和密码登录 XINGE...";
        try
        {
            var settings = ClientSettingsStore.Load();
            ApplyUiToSettings(settings);
            var credentials = await XingeRemoteAccountService.ProvisionAsync(settings);
            XingeClientId = credentials.ClientId;
            XingeClientToken = credentials.ClientToken;
            settings.XingeClientId = credentials.ClientId;
            settings.XingeClientToken = credentials.ClientToken;
            settings.XingeCredentialFingerprint = credentials.CredentialFingerprint;
            ClientSettingsStore.Save(settings);
            XingeRemoteStatus = $"账号登录成功：{credentials.Username}，远程客户端凭证已生成";
            StatusRequested?.Invoke(XingeRemoteStatus);
            SettingsSaved?.Invoke();
        }
        catch (Exception ex)
        {
            XingeRemoteStatus = $"XINGE 登录失败：{ex.Message}";
            StatusRequested?.Invoke(XingeRemoteStatus);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAllRemoteSteps() => ApplyRemoteSteps(QueueStepRegistry.UserSelectable.Select(step => step.Key));

    [RelayCommand]
    private void ResetRemoteSteps() => ApplyRemoteSteps(TikTokRemoteCommandStepDefaults.FullUploadDefaultEnabledSteps);

    [RelayCommand]
    private void ClearRemoteSteps() => ApplyRemoteSteps([]);

    [RelayCommand]
    private async Task RunRemoteCommandPreviewAsync()
    {
        var command = TikTokRemoteCommandParser.Parse(RemoteCommandPreviewText);
        if (command is null)
        {
            RemoteCommandPreviewResult = "命令格式未识别";
            StatusRequested?.Invoke(RemoteCommandPreviewResult);
            return;
        }

        if (RemoteCommandRequested is null)
        {
            RemoteCommandPreviewResult = $"已识别命令：{command.Command}";
            StatusRequested?.Invoke(RemoteCommandPreviewResult);
            return;
        }

        var result = await RemoteCommandRequested.Invoke(command);
        RemoteCommandPreviewResult = result.SummaryText;
        StatusRequested?.Invoke(result.SummaryText);
    }

    public QueueRunOptions BuildRemoteUploadRunOptions(TikTokRemoteCommand? command = null)
    {
        var settings = ClientSettingsStore.Load();
        ApplyUiToSettings(settings);
        return TikTokRemoteRunOptions.BuildFeishuTikTokUploadRunOptions(settings, command);
    }

    private void ApplyUiToSettings(ClientSettings settings)
    {
        settings.AuthServerUrl = AuthServerUrl.Trim();
        settings.AuthAccount = LoginAccount.Trim();
        settings.AuthPassword = LoginPassword;
        settings.FeishuCommandEnabled = FeishuCommandEnabled;
        settings.FeishuCommandAppId = FeishuCommandAppId.Trim();
        settings.FeishuCommandAppSecret = FeishuCommandAppSecret;
        settings.FeishuCommandBotName = FeishuCommandBotName.Trim();
        settings.FeishuCommandBotAliases = FeishuCommandBotAliases.Trim();
        settings.FeishuCommandRequireBotMention = FeishuCommandRequireBotMention;
        settings.FeishuCommandAllowedChatIds = FeishuCommandAllowedChatIds.Trim();
        settings.FeishuCommandDirectMessageEnabled = FeishuCommandDirectMessageEnabled;
        settings.FeishuCommandAllowedUserIds = FeishuCommandAllowedUserIds.Trim();
        settings.FeishuCommandDefaultWorkspace = FeishuCommandDefaultWorkspace.Trim();
        settings.FeishuCommandReplyEnabled = FeishuCommandReplyEnabled;
        settings.FeishuCommandCommandTtlSeconds = Math.Clamp(FeishuCommandCommandTtlSeconds, 10, 3600);
        settings.FeishuCommandHelpText = string.IsNullOrWhiteSpace(FeishuCommandHelpText)
            ? ClientSettingsDefaults.FeishuCommandHelpText
            : FeishuCommandHelpText.Trim();
        settings.FeishuTiktokUploadEnabledStepsJson =
            TikTokRemoteRunOptions.DumpFeishuTikTokUploadEnabledSteps(ReadRemoteSteps());
        settings.FeishuTiktokUploadAutoArchiveAfterUpload = RemoteAutoArchiveAfterUpload;
        settings.FeishuTiktokUploadForceRerunCompletedSteps = RemoteForceRerunCompletedSteps;
        settings.FeishuTiktokUploadPreferUploadWhenReady = RemotePreferUploadWhenReady;
        settings.VideoTranslateEngine = VideoTranslateEngine == "llm" ? "llm" : "volc";
        settings.VideoTranslateSourceLanguage = VideoTranslateSourceLanguage.Trim();
        settings.VideoTranslateTargetLanguage = VideoTranslateTargetLanguage.Trim();
        settings.VideoTranslateVolcAccessKeyId = VideoTranslateVolcAccessKeyId.Trim();
        settings.VideoTranslateVolcSecretAccessKey = VideoTranslateVolcSecretAccessKey;
        settings.VideoTranslateLlmBaseUrl = VideoTranslateLlmBaseUrl.Trim();
        settings.VideoTranslateLlmApiKey = VideoTranslateLlmApiKey;
        settings.VideoTranslateLlmModel = VideoTranslateLlmModel.Trim();
        settings.VideoTranslateFont = VideoTranslateFont.Trim();
        settings.VideoTranslateFontSize = Math.Clamp(VideoTranslateFontSize, 12, 120);
        settings.VideoTranslateMarginV = Math.Clamp(VideoTranslateMarginV, 0, 600);
        settings.VideoTranslateBilingual = VideoTranslateBilingual;
        settings.VolcVideoTranslateVoice = VolcVideoTranslateVoice;
        settings.VolcVideoTranslateFacial = VolcVideoTranslateFacial;
        settings.VolcVideoTranslateSubtitleSource = VolcVideoTranslateSubtitleSourceIndex switch
        {
            1 => "ASR",
            2 => "SubtitleFile",
            _ => "OCR",
        };
        settings.VolcVideoTranslateSubtitleFileName = VolcVideoTranslateSubtitleFileName.Trim();
        settings.VolcVideoTranslateHardSubtitle = VolcVideoTranslateHardSubtitle;
        settings.VolcVideoTranslateEraseSourceSubtitle = VolcVideoTranslateEraseSourceSubtitle;
        settings.VolcVideoTranslateFontSize = Math.Clamp(VolcVideoTranslateFontSize, 16, 36);
        settings.VolcVideoTranslateUploadConcurrency = Math.Clamp(VolcVideoTranslateUploadConcurrency, 1, 10);
        settings.SubtitleRemovalProvider = SubtitleRemovalProviderIndex switch
        {
            1 => "baidu",
            2 => "aliyun",
            _ => "volc",
        };
        settings.VolcSubtitleEraseMode = VolcSubtitleEraseModeIndex == 1 ? "aideo" : "start_execution";
        settings.VolcSubtitleAccessKeyId = VolcSubtitleAccessKeyId.Trim();
        settings.VolcSubtitleSecretAccessKey = VolcSubtitleSecretAccessKey;
        settings.VolcSubtitleRegion = VolcSubtitleRegion.Trim();
        settings.VolcSubtitleSpaceName = VolcSubtitleSpaceName.Trim();
        settings.VolcSubtitlePlaybackDomain = VolcSubtitlePlaybackDomain.Trim();
        settings.VolcSubtitleVidManifestPath = VolcSubtitleVidManifestPath.Trim();
        settings.VolcSubtitlePollSeconds = Math.Clamp(VolcSubtitlePollSeconds, 3, 60);
        settings.BaiduSubtitleAccessKeyId = BaiduSubtitleAccessKeyId.Trim();
        settings.BaiduSubtitleSecretAccessKey = BaiduSubtitleSecretAccessKey;
        settings.BaiduSubtitleEndpoint = BaiduSubtitleEndpoint.Trim();
        settings.BaiduSubtitleModelType = BaiduSubtitleModelTypeIndex == 1 ? "v3" : "v4";
        settings.BaiduSubtitleType = BaiduSubtitleTypeIndex == 1 ? "global" : "dialog";
        settings.BaiduSubtitleMediaManifestPath = BaiduSubtitleMediaManifestPath.Trim();
        settings.BaiduSubtitlePollSeconds = Math.Clamp(BaiduSubtitlePollSeconds, 3, 60);
        settings.AliyunSubtitleAccessKeyId = AliyunSubtitleAccessKeyId.Trim();
        settings.AliyunSubtitleAccessKeySecret = AliyunSubtitleAccessKeySecret;
        settings.AliyunSubtitleRegion = AliyunSubtitleRegion.Trim();
        settings.AliyunSubtitleOssBucket = AliyunSubtitleOssBucket.Trim();
        settings.AliyunSubtitleOssPrefix = AliyunSubtitleOssPrefix.Trim().Trim('/');
        settings.AliyunSubtitleAdvancedModel = AliyunSubtitleAdvancedModel;
        settings.AliyunSubtitleLimitRegion = AliyunSubtitleLimitRegion.Trim();
        settings.AliyunSubtitleObjectManifestPath = AliyunSubtitleObjectManifestPath.Trim();
        settings.AliyunSubtitlePollSeconds = Math.Clamp(AliyunSubtitlePollSeconds, 3, 60);
        settings.VideoDubbingProvider = VideoDubbingProviderIndex == 1 ? "volc" : "elevenlabs";
        settings.ElevenLabsApiKey = ElevenLabsApiKey;
        settings.ElevenLabsSourceLanguage = ElevenLabsSourceLanguage.Trim();
        settings.ElevenLabsTargetLanguage = ElevenLabsTargetLanguage.Trim();
        settings.ElevenLabsPollSeconds = Math.Clamp(ElevenLabsPollSeconds, 3, 60);
        settings.ElevenLabsDropBackgroundAudio = ElevenLabsDropBackgroundAudio;
        settings.ElevenLabsDisableVoiceCloning = ElevenLabsDisableVoiceCloning;
        settings.ElevenLabsSpeechRateMode = ElevenLabsSpeechRateRepairEnabled ? "repair" : "off";
        settings.ElevenLabsSpeechRateTarget = Math.Clamp(ElevenLabsSpeechRateTarget, 2.0, 4.0);
        settings.ElevenLabsSpeechRateThreshold = Math.Clamp(ElevenLabsSpeechRateThreshold, settings.ElevenLabsSpeechRateTarget, 5.0);
        settings.ElevenLabsSpeechRateMaxStretch = Math.Clamp(ElevenLabsSpeechRateMaxStretch, 1.0, 1.3);
        settings.XingeRemoteEnabled = XingeRemoteEnabled;
        settings.XingeServerUrl = XingeServerUrl.Trim();
        settings.XingeAccount = XingeAccount.Trim();
        settings.XingePassword = XingePassword;
        settings.XingeClientId = XingeClientId.Trim();
        settings.XingeClientToken = XingeClientToken.Trim();
        settings.XingeClientName = string.IsNullOrWhiteSpace(XingeClientName) ? "TikTokPublisher" : XingeClientName.Trim();
        settings.XingePollIntervalSeconds = Math.Clamp(XingePollIntervalSeconds <= 0 ? 3 : XingePollIntervalSeconds, 1, 60);
    }

    private IReadOnlyList<string> ReadRemoteSteps()
    {
        var steps = new List<string>();
        if (RemoteDownloadEnabled) steps.Add(QueueStepRegistry.Download);
        if (RemoteRewriteEnabled) steps.Add(QueueStepRegistry.RewriteInfo);
        if (RemoteGeneratePosterEnabled) steps.Add(QueueStepRegistry.GeneratePoster);
        if (RemoteGenerateProofMaterialEnabled) steps.Add(QueueStepRegistry.GenerateProofMaterial);
        if (RemoteSmallVideoRepairEnabled) steps.Add(QueueStepRegistry.SmallVideoRepair);
        if (RemoteDubVideoEnabled) steps.Add(QueueStepRegistry.DubVideo);
        if (RemoteSilenceDetectEnabled) steps.Add(QueueStepRegistry.SilenceDetect);
        if (RemoteSilenceRepairEnabled) steps.Add(QueueStepRegistry.SilenceRepair);
        if (RemoteMaterialValidateEnabled) steps.Add(QueueStepRegistry.MaterialValidate);
        if (RemoteDeleteSourceVideosEnabled) steps.Add(QueueStepRegistry.DeleteSourceVideos);
        if (RemoteUploadEnabled) steps.Add(QueueStepRegistry.UploadSeries);
        return QueueStepRegistry.OrderEnabledSteps(steps).ToList();
    }

    private void ApplyRemoteSteps(IEnumerable<string> steps)
    {
        var normalized = TikTokRemoteCommandParser.NormalizeEnabledSteps(steps?.Cast<object?>() ?? []);
        var selected = normalized.ToHashSet(StringComparer.Ordinal);
        RemoteDownloadEnabled = selected.Contains(QueueStepRegistry.Download);
        RemoteRewriteEnabled = selected.Contains(QueueStepRegistry.RewriteInfo);
        RemoteGeneratePosterEnabled = selected.Contains(QueueStepRegistry.GeneratePoster);
        RemoteGenerateProofMaterialEnabled = selected.Contains(QueueStepRegistry.GenerateProofMaterial);
        RemoteSmallVideoRepairEnabled = selected.Contains(QueueStepRegistry.SmallVideoRepair);
        RemoteRemoveSubtitlesEnabled = false;
        RemoteDubVideoEnabled = selected.Contains(QueueStepRegistry.DubVideo);
        RemoteAddSubtitlesEnabled = false;
        RemoteSilenceDetectEnabled = selected.Contains(QueueStepRegistry.SilenceDetect);
        RemoteSilenceRepairEnabled = selected.Contains(QueueStepRegistry.SilenceRepair);
        RemoteMaterialValidateEnabled = selected.Contains(QueueStepRegistry.MaterialValidate);
        RemoteDeleteSourceVideosEnabled = selected.Contains(QueueStepRegistry.DeleteSourceVideos);
        RemoteUploadEnabled = selected.Contains(QueueStepRegistry.UploadSeries);
    }
}

public sealed partial class ArchivedProjectRowViewModel : ViewModelBase
{
    public ArchivedProjectItem Item { get; }

    public ArchivedProjectRowViewModel(ArchivedProjectItem item) => Item = item;

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private int _rowIndex;

    public string DisplayName => Item.DisplayName;
    public string OriginalTitle => Item.OriginalTitle;
    public string NewTitle => Item.NewTitle;
    public string ArchiveSource => Item.ArchiveSource.ToLowerInvariant() switch
    {
        "" or "tiktok" => "TikTok",
        "kuaishou" => "快手",
        "video_channel" => "视频号",
        "miniprogram" => "小程序",
        _ => Item.ArchiveSource,
    };
    public string ArchivedAt => Item.ArchivedAt;
    public string QueuedAt => QueueProjectRowViewModel.FormatQueuedAt(Item.QueuedAt, compact: true);
    public string QueuedAtTooltip => QueueProjectRowViewModel.FormatQueuedAt(Item.QueuedAt, compact: false);
    public string MetadataPath => Item.MetadataPath;
    public string ArchiveDisplayPath => string.IsNullOrWhiteSpace(Item.MetadataPath)
        ? Item.ArchiveProjectDir
        : Item.MetadataPath;
    public string SourceDir => Item.ArchivedSourceDir;
    public string WorkflowDir => Item.ArchivedWorkflowDir;
}

public sealed partial class ArchivedProjectsViewModel : ViewModelBase
{
    public ObservableCollection<ArchivedProjectRowViewModel> Rows { get; } = new();
    public ObservableCollection<ArchivedProjectRowViewModel> FilteredRows { get; } = new();

    [ObservableProperty] private string _workspacePath = "";
    [ObservableProperty] private string _archiveRootDir = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _rootSummary = "归档根目录: 未选择工作目录";
    [ObservableProperty] private ArchivedProjectRowViewModel? _selectedRow;

    public event Action<string>? StatusRequested;
    public event Action? Restored;
    public Func<TikTokAccountProfile?>? AccountProvider { get; set; }
    public Func<QueueProjectItem, TikTokAccountProfile?>? AccountResolver { get; set; }

    public void SetWorkspace(string? workspacePath, bool refresh = true)
    {
        var previousRoot = ArchiveRootDir;
        WorkspacePath = workspacePath?.Trim() ?? "";
        SyncArchiveRootFromSettings();

        // 切账号导致归档根目录变化时必须重载列表，否则界面停留在上一账号的归档记录。
        var rootChanged = !string.IsNullOrWhiteSpace(previousRoot) &&
                          !string.Equals(previousRoot, ArchiveRootDir, StringComparison.OrdinalIgnoreCase);
        if (refresh || rootChanged)
            Refresh();
    }

    public void SetArchiveRootDir(string? archiveRootDir)
    {
        ArchiveRootDir = Path.GetFullPath((archiveRootDir ?? "").Trim());
        var settings = ClientSettingsStore.Load();
        settings.ArchiveRootDir = ArchiveRootDir;
        ClientSettingsStore.Save(settings);
        Refresh();
    }

    partial void OnSearchTextChanged(string value) => ApplySearchFilter();

    private int _refreshGeneration;

    [RelayCommand]
    private void Refresh() => _ = RefreshAsync();

    /// <summary>归档清单读取涉及目录扫描与元数据解析，放后台线程执行，避免切账号/进入页面时卡 UI。</summary>
    private async Task RefreshAsync()
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        RootSummary = string.IsNullOrWhiteSpace(ArchiveRootDir)
            ? "归档根目录: 未选择工作目录"
            : $"归档根目录: {ArchiveRootDir}";
        var workspace = WorkspaceForAction();
        if (workspace is null || !Directory.Exists(workspace))
        {
            Rows.Clear();
            FilteredRows.Clear();
            StatusMessage = "请先绑定工作目录";
            return;
        }

        var archiveRoot = ArchiveRootDir;
        List<ArchivedProjectRowViewModel> loaded;
        try
        {
            loaded = await Task.Run(() =>
                TikTokArchivedProjectService.List(workspace, archiveRoot)
                    .Select(item => new ArchivedProjectRowViewModel(item))
                    .ToList()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (generation == _refreshGeneration)
                StatusMessage = $"加载归档列表失败：{ex.Message}";
            return;
        }

        // 期间又发生了刷新（快速切账号）则丢弃过期结果。
        if (generation != _refreshGeneration)
            return;

        Rows.Clear();
        FilteredRows.Clear();
        foreach (var row in loaded)
            Rows.Add(row);
        ApplySearchFilter();
    }

    public void SelectAll()
    {
        foreach (var row in FilteredRows)
            row.Selected = true;
    }

    public void ClearSelection()
    {
        foreach (var row in FilteredRows)
            row.Selected = false;
    }

    public void SelectToCurrentProject()
    {
        if (SelectedRow is null)
        {
            StatusMessage = "请先点击选中一个目标项目";
            return;
        }

        var current = FilteredRows.IndexOf(SelectedRow);
        if (current < 0)
        {
            StatusMessage = "当前项目不在筛选结果中";
            return;
        }

        var anchor = FilteredRows
            .Select((row, index) => (row, index))
            .FirstOrDefault(pair => pair.row.Selected)
            .index;
        var start = Math.Min(anchor, current);
        var end = Math.Max(anchor, current);
        for (var i = start; i <= end; i++)
            FilteredRows[i].Selected = true;
        StatusMessage = $"已勾选 {end - start + 1} 个归档项目";
    }

    public void OpenArchiveRoot() => OpenPath(ArchiveRootDir);
    public void OpenSelectedArchiveDir() => OpenPath(SelectedRow?.ArchiveDisplayPath);
    public void OpenSelectedSourceDir() => OpenPath(SelectedRow?.SourceDir);
    public void OpenSelectedWorkflowDir() => OpenPath(SelectedRow?.WorkflowDir);
    public void OpenRowSource(ArchivedProjectRowViewModel? row) => OpenPath(row?.SourceDir);
    public void OpenRowWorkflow(ArchivedProjectRowViewModel? row) => OpenPath(row?.WorkflowDir);

    public async Task RestoreSelectedAsync()
    {
        var targets = TargetRowsForAction();
        if (targets.Length == 0)
        {
            StatusMessage = "请先勾选要回退的归档项目（或点击选中一行）";
            return;
        }

        var workspace = WorkspaceForAction();
        if (workspace is null)
        {
            StatusMessage = "未找到可恢复的工作目录";
            return;
        }

        var (success, failures) = await Task.Run(() =>
        {
            var ok = 0;
            var errors = new List<string>();
            foreach (var row in targets)
            {
                try
                {
                    TikTokArchivedProjectService.Restore(workspace, row.Item.ArchiveProjectDir, ArchiveRootDir);
                    ok++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{row.DisplayName}: {ex.Message}");
                }
            }

            return (ok, errors);
        });

        Refresh();
        if (success > 0)
            Restored?.Invoke();
        StatusMessage = failures.Count == 0
            ? $"已回退 {success} 个归档项目"
            : $"回退完成：成功 {success} 个，失败 {failures.Count} 个";
        StatusRequested?.Invoke(StatusMessage);
        foreach (var failure in failures.Take(5))
            StatusRequested?.Invoke($"回退失败：{failure}");
    }

    public async Task DeleteSelectedAsync()
    {
        var targets = TargetRowsForAction();
        if (targets.Length == 0)
        {
            StatusMessage = "请先勾选要删除的归档项目（或点击选中一行）";
            return;
        }

        var workspace = WorkspaceForAction();
        if (workspace is null)
        {
            StatusMessage = "未找到可删除的工作目录";
            return;
        }

        var deleted = 0;
        foreach (var row in targets)
        {
            await TikTokArchivedProjectService.DeleteAsync(workspace, row.Item.ArchiveProjectDir, ArchiveRootDir);
            deleted++;
        }

        Refresh();
        StatusMessage = deleted == 1
            ? $"已删除归档项目：{targets[0].DisplayName}"
            : $"已删除归档项目 {deleted} 个";
        StatusRequested?.Invoke(StatusMessage);
    }

    public int GetActionTargetCount() => TargetRowsForAction().Length;

    public async Task SyncCheckedToManagementAsync()
    {
        var targets = Rows.Where(row => row.Selected).ToArray();
        if (targets.Length == 0)
        {
            StatusMessage = "请先勾选要同步到管理系统的已归档项目";
            return;
        }

        var ok = 0;
        var failed = 0;
        foreach (var row in targets)
        {
            var item = TikTokArchivedProjectService.ToQueueItemForSync(row.Item);
            var account = AccountResolver?.Invoke(item) ?? AccountProvider?.Invoke();
            var result = await TikTokManagementUploadRecordSyncService
                .SyncUploadRecordAsync(item, account, CancellationToken.None);
            if (result.Ok) ok++; else failed++;
            StatusRequested?.Invoke($"同步归档项目：{row.DisplayName} - {result.Message}");
        }

        StatusMessage = $"同步完成：成功 {ok}，失败 {failed}";
        StatusRequested?.Invoke(StatusMessage);
    }

    private void SyncArchiveRootFromSettings()
    {
        var settings = ClientSettingsStore.Load();
        ArchiveRootDir = string.IsNullOrWhiteSpace(settings.ArchiveRootDir)
            ? string.IsNullOrWhiteSpace(WorkspacePath) ? "" : Path.Combine(Path.GetFullPath(WorkspacePath), "archive")
            : Path.GetFullPath(settings.ArchiveRootDir);
        RootSummary = string.IsNullOrWhiteSpace(ArchiveRootDir)
            ? "归档根目录: 未选择工作目录"
            : $"归档根目录: {ArchiveRootDir}";
    }

    private void ApplySearchFilter()
    {
        FilteredRows.Clear();
        var keyword = (SearchText ?? "").Trim().ToLowerInvariant();
        var index = 1;
        foreach (var row in Rows)
        {
            if (keyword.Length > 0 &&
                !Contains(row.DisplayName, keyword) &&
                !Contains(row.OriginalTitle, keyword) &&
                !Contains(row.NewTitle, keyword) &&
                !Contains(row.ArchiveSource, keyword))
            {
                continue;
            }

            row.RowIndex = index++;
            FilteredRows.Add(row);
        }

        StatusMessage = FilteredRows.Count == Rows.Count
            ? $"已归档: {Rows.Count}"
            : $"已归档: {FilteredRows.Count} / {Rows.Count}";
    }

    private ArchivedProjectRowViewModel[] TargetRowsForAction()
    {
        var checkedRows = Rows.Where(r => r.Selected).ToArray();
        if (checkedRows.Length > 0)
            return checkedRows;
        return SelectedRow is null ? Array.Empty<ArchivedProjectRowViewModel>() : new[] { SelectedRow };
    }

    private string? WorkspaceForAction()
    {
        if (!string.IsNullOrWhiteSpace(WorkspacePath) && Directory.Exists(WorkspacePath))
            return Path.GetFullPath(WorkspacePath);
        if (!string.IsNullOrWhiteSpace(ArchiveRootDir))
        {
            var parent = Directory.GetParent(Path.GetFullPath(ArchiveRootDir));
            if (parent is not null)
                return parent.FullName;
        }

        return null;
    }

    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "未找到目录";
            return;
        }

        var target = Path.GetFullPath(path);
        if (File.Exists(target))
            target = Path.GetDirectoryName(target) ?? target;
        if (!Directory.Exists(target))
        {
            StatusMessage = $"目录不存在：{target}";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        });
        StatusMessage = $"已打开：{target}";
    }

    private static bool Contains(string? value, string keyword) =>
        (value ?? "").ToLowerInvariant().Contains(keyword);
}
