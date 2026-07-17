using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Config;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Automation.Weixin;

public sealed class WeixinAutomationConfigLoader : IWeixinAutomationConfigLoader
{
    private const string DefaultBaseUrl = "https://channels.weixin.qq.com";
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    public async Task<WeixinAutomationConfig> LoadAsync(
        string? configPath,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedProjectDirectory = Path.GetFullPath(projectDirectory);
        var normalizedConfigPath = !string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath)
            ? Path.GetFullPath(configPath)
            : null;
        var configDirectory = normalizedConfigPath is null
            ? normalizedProjectDirectory
            : Path.GetDirectoryName(normalizedConfigPath) ?? normalizedProjectDirectory;
        var globalConfig = LoadGlobalConfig(normalizedProjectDirectory, configDirectory);

        JsonElement root = default;
        if (normalizedConfigPath is not null)
        {
            await using var stream = File.OpenRead(normalizedConfigPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            root = document.RootElement.Clone();
        }

        var baseUrl = ResolveString(root, "base_url") ?? DefaultBaseUrl;
        var authFilePath = ResolvePath(
            configDirectory,
            ResolveString(root, "auth_file"),
            WeixinRuntimePaths.DefaultAuthFilePath);
        var outputDirectory = ResolvePath(
            configDirectory,
            ResolveConfigString(globalConfig, "WeixinSubmissionReportDir") ?? ResolveString(root, "output_dir"),
            WeixinRuntimePaths.DefaultOutputDirectory);

        var browserElement = TryGetObject(root, "browser");
        var debugElement = TryGetObject(root, "debug");
        var loginElement = TryGetObject(root, "login");
        var navigationElement = TryGetObject(root, "navigation");
        var firstPageElement = TryGetObject(root, "first_page");
        var secondPageElement = TryGetObject(root, "second_page");
        var submitElement = TryGetObject(root, "submit");
        var videoPublishElement = TryGetObject(root, "video_publish");
        var viewportElement = TryGetObject(browserElement, "viewport");
        var secondPageUploadElement = TryGetObject(secondPageElement, "upload");
        var secondPageUploadQueueElement = TryGetObject(secondPageElement, "upload_queue");
        var secondPageEnterSubmitElement = TryGetObject(secondPageElement, "enter_submit_page");
        var videoPublishNavigationElement = TryGetObject(videoPublishElement, "navigation");
        var videoUploadActionElement = TryGetObject(videoPublishElement, "video_upload_action");

        var config = new WeixinAutomationConfig(
            ConfigPath: normalizedConfigPath,
            ConfigDirectory: configDirectory,
            BaseUrl: baseUrl,
            AuthFilePath: authFilePath,
            OutputDirectory: outputDirectory,
            TaskType: ResolveString(root, "task_type") ?? "series_submission",
            Browser: new WeixinBrowserOptions(
                Headless: ResolveConfigBool(globalConfig, "WeixinHeadless") ?? ResolveBool(browserElement, "headless") ?? false,
                SlowMoMs: ResolveConfigInt(globalConfig, "WeixinSlowMoMs") ?? ResolveInt(browserElement, "slow_mo_ms") ?? 0,
                KeepOpenSeconds: ResolveConfigInt(globalConfig, "WeixinKeepOpenSeconds") ?? ResolveInt(browserElement, "keep_open_seconds") ?? 0,
                UserDataDirectory: ResolvePath(
                    configDirectory,
                    ResolveString(browserElement, "user_data_dir"),
                    WeixinRuntimePaths.DefaultBrowserProfileDirectory),
                UserAgent: ResolveString(browserElement, "user_agent") ?? DefaultUserAgent,
                Viewport: new WeixinViewportOptions(
                    Width: ResolveInt(viewportElement, "width") ?? 1440,
                    Height: ResolveInt(viewportElement, "height") ?? 1200)),
            Debug: new WeixinDebugOptions(
                LogFilePath: ResolvePath(
                    configDirectory,
                    ResolveString(debugElement, "log_file"),
                    Path.Combine(outputDirectory, "run.log")),
                SaveHtml: ResolveConfigBool(globalConfig, "WeixinSaveHtml") ?? ResolveBool(debugElement, "save_html") ?? false,
                SaveText: ResolveConfigBool(globalConfig, "WeixinSaveText") ?? ResolveBool(debugElement, "save_text") ?? false),
            Login: new WeixinLoginOptions(
                TimeoutSeconds: ResolveConfigInt(globalConfig, "WeixinLoginTimeoutSeconds") ?? ResolveInt(loginElement, "timeout_seconds") ?? 300),
            PauseOnError: ResolveConfigBool(globalConfig, "WeixinPauseOnError") ?? ResolveBool(root, "pause_on_error") ?? true,
            Navigation: new WeixinNavigationOptions(
                Section: ResolveString(navigationElement, "section") ?? "收入与服务",
                Item: ResolveString(navigationElement, "item") ?? "剧集管理",
                EntryButton: ResolveString(navigationElement, "entry_button") ?? "上架剧集"),
            FirstPage: new WeixinFirstPageOptions(
                ReadyText: ResolveString(firstPageElement, "ready_text") ?? string.Empty,
                NextButtonText: ResolveString(firstPageElement, "next_button_text") ?? "下一步",
                ReadyLabels: ResolveFirstPageReadyLabels(firstPageElement),
                Actions: ResolveFirstPageActions(firstPageElement, secondPageElement, globalConfig)),
            SecondPage: new WeixinSecondPageOptions(
                ReadyText: ResolveString(secondPageElement, "ready_text") ?? "请选择要上传的视频文件",
                ActionsBeforeUpload: ResolveFormActions(secondPageElement, "actions_before_upload"),
                Upload: new WeixinUploadAction(
                    InputSelector: ResolveString(secondPageUploadElement, "input_selector") ?? "input[type='file']",
                    Paths: ResolveStringArray(secondPageUploadElement, "paths"),
                    TimeoutSeconds: ResolveInt(secondPageUploadElement, "timeout_seconds") ?? 3600,
                    SuccessTexts: ResolveStringArray(secondPageUploadElement, "success_texts"),
                    ErrorTexts: ResolveStringArray(secondPageUploadElement, "error_texts")),
                UploadQueue: ResolveUploadQueue(secondPageUploadQueueElement, secondPageUploadElement),
                EnterSubmitPage: new WeixinSubmitPageEntryOptions(
                    Enabled: ResolveBool(secondPageEnterSubmitElement, "enabled") ?? true,
                    Text: ResolveString(secondPageEnterSubmitElement, "text") ?? "确认提审",
                    WaitText: ResolveString(secondPageEnterSubmitElement, "wait_text") ?? string.Empty),
                ActionsAfterUpload: ResolveFormActions(secondPageElement, "actions_after_upload")),
            Submit: new WeixinSubmitOptions(
                Enabled: ResolveConfigBool(globalConfig, "WeixinSubmitEnabled") ?? ResolveBool(submitElement, "enabled") ?? false,
                Text: ResolveString(submitElement, "text") ?? "确认提审",
                ReadyText: ResolveString(submitElement, "ready_text") ?? string.Empty),
            VideoPublish: new WeixinVideoPublishOptions(
                Enabled: ResolveBool(videoPublishElement, "enabled") ?? true,
                Navigation: new WeixinNavigationOptions(
                    Section: ResolveString(videoPublishNavigationElement, "section") ?? "内容管理",
                    Item: ResolveString(videoPublishNavigationElement, "item") ?? "视频",
                    EntryButton: ResolveString(videoPublishNavigationElement, "entry_button") ?? "发表视频"),
                ReadyText: ResolveString(videoPublishElement, "ready_text") ?? "发表视频",
                RunStrategy: NormalizeVideoPublishRunStrategy(ResolveString(videoPublishElement, "run_strategy")),
                StateFile: ResolveString(videoPublishElement, "state_file") ?? ".weixin-channel-publish-state.json",
                AllowDuplicatePublish: ResolveBool(videoPublishElement, "_runtime_allow_duplicate_material_publish")
                    ?? ResolveBool(videoPublishElement, "allow_duplicate_publish")
                    ?? ResolveBool(videoPublishElement, "runtime_allow_duplicate_material_publish")
                    ?? false,
                PauseOnError: ResolveBool(videoPublishElement, "pause_on_error") ?? ResolveBool(root, "pause_on_error") ?? true,
                VideoSourceMode: NormalizeVideoPublishSourceMode(
                    ResolveString(videoPublishElement, "publish_video_source_mode")
                    ?? ResolveString(videoPublishElement, "video_source_mode")),
                FillDescription: ResolveBool(videoPublishElement, "fill_description") ?? true,
                FillShortTitle: ResolveBool(videoPublishElement, "fill_short_title") ?? false,
                DescriptionTemplate: ResolveString(videoPublishElement, "description_template") ?? "{剧集名称}，热播爆火剧，点击链接，免费观看全集。#热门#爆火 {标签}",
                PrependHashToDescription: ResolveBool(videoPublishElement, "prepend_hash_to_description") ?? true,
                LocationOptionText: ResolveString(videoPublishElement, "location_option_text") ?? "不显示",
                LinkOptionText: ResolveString(videoPublishElement, "link_option_text") ?? "视频号剧集",
                LinkPickerButtonText: ResolveString(videoPublishElement, "link_picker_button_text") ?? "选择需要添加的视频号剧集",
                LinkPickerSelector: ResolveString(videoPublishElement, "link_picker_selector") ?? string.Empty,
                LinkDialogTitle: ResolveString(videoPublishElement, "link_dialog_title") ?? "选择需要关联的视频号剧集",
                LinkSearchPlaceholder: ResolveString(videoPublishElement, "link_search_placeholder") ?? "搜索内容",
                ActivityOptionText: ResolveString(videoPublishElement, "activity_option_text") ?? "不参与活动",
                TimingOptionText: ResolveString(videoPublishElement, "timing_option_text") ?? "不定时",
                ShortTitleMaxLength: ResolveInt(videoPublishElement, "short_title_max_length") ?? 15,
                FinalAction: ResolveString(videoPublishElement, "final_action") ?? "publish",
                FinalActionText: ResolvePublishFinalActionText(ResolveString(videoPublishElement, "final_action")),
                WaitAfterUploadSeconds: ResolveDouble(videoPublishElement, "wait_after_upload_seconds") ?? 0.5,
                WaitAfterFinalActionSeconds: ResolveDouble(videoPublishElement, "wait_after_final_action_seconds") ?? 0,
                EpisodeSelectionMode: ResolveString(videoPublishElement, "episode_selection_mode") ?? "range",
                StartEpisodeIndex: ResolveInt(videoPublishElement, "start_episode_index") ?? 2,
                PublishCount: ResolveInt(videoPublishElement, "publish_count") ?? 4,
                EpisodeIndexes: ResolveIntArray(videoPublishElement, "episode_indexes"),
                VideoUploadSelector: ResolveString(videoUploadActionElement, "input_selector") ?? "input[type='file'][accept*='video'], input[type='file']")
            {
                CustomVideoFiles = ResolvePathArray(
                    configDirectory,
                    videoPublishElement,
                    "publish_video_custom_files",
                    "custom_video_files"),
                VideoDescriptionMap = ResolveStringMap(videoPublishElement, "publish_video_description_map"),
                MergePublishEnabled = ResolveBool(videoPublishElement, "merge_publish_enabled") ?? false,
                MergePublishGroupSize = ResolveInt(videoPublishElement, "merge_publish_group_size") ?? 0,
                DeclareOriginal = ResolveBool(videoPublishElement, "declare_original") ?? false,
                ReplaceCoverWithLocalImage = ResolveBool(videoPublishElement, "replace_cover_with_local_image") ?? false,
                CoverImagePath = ResolveOptionalPath(configDirectory, ResolveString(videoPublishElement, "cover_image_path")),
                SystemHighlightDramaTitle = ResolveString(videoPublishElement, "system_highlight_drama_title")
                    ?? ResolveString(videoPublishElement, "system_highlight_title")
                    ?? string.Empty,
                SystemHighlightPublishTargetMode = NormalizeSystemHighlightTargetMode(
                    ResolveString(videoPublishElement, "system_highlight_publish_target_mode")),
                SystemHighlightPublishVideoTypes = NormalizeSystemHighlightVideoTypes(
                    ResolveStringArray(videoPublishElement, "system_highlight_publish_video_types")),
                SystemHighlightRegenerateAfterPublish = ResolveBool(videoPublishElement, "system_highlight_regenerate_after_publish") ?? false,
                SystemHighlightRegenerateVideoTypes = NormalizeSystemHighlightVideoTypes(
                    ResolveStringArray(videoPublishElement, "system_highlight_regenerate_video_types")),
                NewDramaMountTitle = ResolveString(videoPublishElement, "new_drama_mount_title")
                    ?? ResolveString(videoPublishElement, "new_drama_title")
                    ?? string.Empty,
                NewDramaMountProjectDir = ResolveOptionalPath(
                    configDirectory,
                    ResolveString(videoPublishElement, "new_drama_mount_project_dir")),
                NewDramaMountResolvedTitle = ResolveString(videoPublishElement, "new_drama_mount_resolved_title") ?? string.Empty,
                NewDramaMountResolvedBookId = ResolveString(videoPublishElement, "new_drama_mount_resolved_book_id") ?? string.Empty,
                AiDescriptionEnabled = ResolveBool(videoPublishElement, "ai_description_enabled") ?? false,
                AiDescriptionUseAsr = ResolveBool(videoPublishElement, "ai_description_use_asr") ?? true,
                AiDescriptionFallbackToOriginal = ResolveBool(videoPublishElement, "ai_description_fallback_to_original") ?? true,
                AiDescriptionCacheEnabled = ResolveBool(videoPublishElement, "ai_description_cache_enabled") ?? true,
                AiDescriptionRetryAttempts = Math.Max(1, ResolveInt(videoPublishElement, "ai_description_retry_attempts") ?? 3),
                AiDescriptionTimeoutSeconds = Math.Max(5, ResolveInt(videoPublishElement, "ai_description_timeout_seconds")
                    ?? ResolveConfigInt(globalConfig, "AiTextTimeoutSeconds")
                    ?? 60),
                AiTextEndpoint = ResolveString(videoPublishElement, "ai_text_endpoint")
                    ?? ResolveConfigString(globalConfig, "AiTextEndpoint")
                    ?? ResolveConfigString(globalConfig, "ChatModelEndpoint")
                    ?? string.Empty,
                AiTextApiKey = ResolveString(videoPublishElement, "ai_text_api_key")
                    ?? ResolveConfigString(globalConfig, "AiTextApiKey")
                    ?? ResolveConfigString(globalConfig, "ChatModelApiKey")
                    ?? string.Empty,
                AiTextModel = ResolveString(videoPublishElement, "ai_text_model")
                    ?? ResolveConfigString(globalConfig, "AiTextModel")
                    ?? ResolveConfigString(globalConfig, "ChatModelId")
                    ?? string.Empty,
                AiDescriptionAsrEngine = ResolveString(videoPublishElement, "ai_description_asr_engine")
                    ?? ResolveConfigString(globalConfig, "MaterialClipAsrEngine")
                    ?? "volcengine",
                AiDescriptionAsrLanguage = ResolveString(videoPublishElement, "ai_description_asr_language")
                    ?? ResolveConfigString(globalConfig, "MaterialClipAsrLanguage")
                    ?? "zh-CN",
                AiDescriptionVolcengineAppId = ResolveString(videoPublishElement, "ai_description_volcengine_app_id")
                    ?? ResolveString(videoPublishElement, "material_clip_volcengine_app_id")
                    ?? ResolveConfigString(globalConfig, "MaterialClipVolcengineAppId")
                    ?? string.Empty,
                AiDescriptionVolcengineAccessToken = ResolveString(videoPublishElement, "ai_description_volcengine_access_token")
                    ?? ResolveString(videoPublishElement, "material_clip_volcengine_access_token")
                    ?? ResolveConfigString(globalConfig, "MaterialClipVolcengineAccessToken")
                    ?? string.Empty,
                AiDescriptionLocalModelDir = ResolveOptionalPath(
                    configDirectory,
                    ResolveString(videoPublishElement, "ai_description_local_model_dir")
                    ?? ResolveString(videoPublishElement, "material_clip_asr_local_model_dir")
                    ?? ResolveConfigString(globalConfig, "MaterialClipAsrLocalModelDir")),
                AiDescriptionLocalVadPath = ResolveOptionalPath(
                    configDirectory,
                    ResolveString(videoPublishElement, "ai_description_local_vad_path")
                    ?? ResolveString(videoPublishElement, "material_clip_asr_local_vad_path")
                    ?? ResolveConfigString(globalConfig, "MaterialClipAsrLocalVadPath")),
                AiDescriptionLocalUseItn = ResolveBool(videoPublishElement, "ai_description_local_use_itn")
                    ?? ResolveBool(videoPublishElement, "material_clip_asr_local_use_itn")
                    ?? ResolveConfigBool(globalConfig, "MaterialClipAsrLocalUseItn")
                    ?? false,
                AiDescriptionHybridMinCharsPerSec = ResolveDouble(videoPublishElement, "ai_description_hybrid_min_chars_per_sec")
                    ?? ResolveDouble(videoPublishElement, "material_clip_asr_hybrid_min_chars_per_sec")
                    ?? ResolveConfigDouble(globalConfig, "MaterialClipAsrHybridMinCharsPerSec")
                    ?? 1.0,
                PublishOriginalityEnabled = ResolveBool(videoPublishElement, "publish_originality_enabled")
                    ?? ResolveBool(videoPublishElement, "material_clip_originality_enabled")
                    ?? ResolveConfigBool(globalConfig, "PublishOriginalityEnabled")
                    ?? ResolveConfigBool(globalConfig, "MaterialClipOriginalityEnabled")
                    ?? false,
                PublishOriginalityReuseAcrossRuns = ResolveBool(videoPublishElement, "publish_originality_reuse_across_runs") ?? true,
                PublishOriginalityZoom = ResolveBool(videoPublishElement, "publish_originality_zoom")
                    ?? ResolveBool(videoPublishElement, "material_clip_originality_zoom")
                    ?? true,
                PublishOriginalityColor = ResolveBool(videoPublishElement, "publish_originality_color")
                    ?? ResolveBool(videoPublishElement, "material_clip_originality_color")
                    ?? false,
                PublishOriginalitySpeed = ResolveBool(videoPublishElement, "publish_originality_speed")
                    ?? ResolveBool(videoPublishElement, "material_clip_originality_speed")
                    ?? false,
                PublishOriginalityFade = ResolveBool(videoPublishElement, "publish_originality_fade")
                    ?? ResolveBool(videoPublishElement, "material_clip_originality_fade")
                    ?? false,
                PublishOriginalityStickerDir = ResolveOptionalPath(
                    configDirectory,
                    ResolveString(videoPublishElement, "publish_originality_sticker_dir")
                    ?? ResolveString(videoPublishElement, "material_clip_originality_sticker_dir")),
                RuntimeAccountProfileId = ResolveString(videoPublishElement, "_runtime_account_profile_id") ?? string.Empty,
                RuntimeAccountProfileName = ResolveString(videoPublishElement, "_runtime_account_profile_name") ?? string.Empty,
                FfmpegPath = ResolveString(videoPublishElement, "ffmpeg_path")
                    ?? ResolveConfigString(globalConfig, "FfmpegPath")
                    ?? "ffmpeg",
                FfprobePath = ResolveString(videoPublishElement, "ffprobe_path")
                    ?? ResolveConfigString(globalConfig, "FfprobePath")
                    ?? "ffprobe"
            });

        return config;
    }

    private static IReadOnlyList<string> ResolveFirstPageReadyLabels(JsonElement firstPageElement)
    {
        var actions = ResolveActionArray(firstPageElement, "actions");
        if (actions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var labels = new List<string>();
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = ResolveString(action, "type");
            var label = ResolveString(action, "label");
            if (!string.Equals(type, "fill", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            labels.Add(label);
        }

        return labels;
    }

    private static IReadOnlyList<WeixinFormAction> ResolveFirstPageActions(
        JsonElement firstPageElement,
        JsonElement secondPageElement,
        IReadOnlyDictionary<string, string> globalConfig)
    {
        var actions = ResolveFormActions(firstPageElement);
        if (actions.Count == 0)
        {
            return actions;
        }

        var fillRecommendation = ResolveConfigBool(globalConfig, "WeixinFillRecommendation") ?? true;
        var monetizationType = ResolveConfigString(globalConfig, "WeixinMonetizationType");
        var dramaType = ResolveConfigString(globalConfig, "WeixinDramaType");
        var dramaQualification = ResolveConfigString(globalConfig, "WeixinDramaQualification");
        var submitterIdentity = ResolveConfigString(globalConfig, "WeixinSubmitterIdentity");
        var trialEpisodes = ResolveConfigString(globalConfig, "WeixinTrialEpisodes");
        var actualEpisodeCount = ResolveActualEpisodeCount(secondPageElement);

        var normalized = new List<WeixinFormAction>();
        foreach (var action in actions)
        {
            if (IsLegacyCostReportUploadAction(action))
            {
                normalized.Add(action with
                {
                    Label = "剧目资质",
                    Text = string.IsNullOrWhiteSpace(action.Text) ? "选择文件" : action.Text,
                    Selector = null
                });
                continue;
            }

            if (string.Equals(action.Type, "fill", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(action.Label, "推荐语", StringComparison.Ordinal) &&
                !fillRecommendation)
            {
                continue;
            }

            if (string.Equals(action.Type, "choose", StringComparison.OrdinalIgnoreCase))
            {
                var optionText = action.FieldLabel switch
                {
                    "变现类型" when !string.IsNullOrWhiteSpace(monetizationType) => monetizationType,
                    "剧目类型" when !string.IsNullOrWhiteSpace(dramaType) => dramaType,
                    "剧目资质" when !string.IsNullOrWhiteSpace(dramaQualification) => dramaQualification,
                    "提审身份" when !string.IsNullOrWhiteSpace(submitterIdentity) => submitterIdentity,
                    _ => action.OptionText
                };

                normalized.Add(action with { OptionText = optionText });
                continue;
            }

            if (string.Equals(action.Type, "fill", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(action.Label, "试看集数", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(trialEpisodes))
            {
                var value = NormalizeTrialEpisodes(trialEpisodes, actualEpisodeCount);
                normalized.Add(action with { Value = value });
                continue;
            }

            normalized.Add(action);
        }

        return normalized;
    }

    private static bool IsLegacyCostReportUploadAction(WeixinFormAction action)
    {
        return string.Equals(action.Type, "upload", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(action.Selector) &&
               action.Selector.Contains("成本配置比例情况报告", StringComparison.Ordinal);
    }

    private static IReadOnlyList<WeixinFormAction> ResolveFormActions(JsonElement element, string propertyName = "actions")
    {
        var actions = ResolveActionArray(element, propertyName);
        if (actions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<WeixinFormAction>();
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = ResolveString(action, "type") ?? string.Empty;
            var fieldLabel = ResolveString(action, "field_label");
            var optionText = ResolveString(action, "option_text");

            results.Add(new WeixinFormAction(
                Type: type,
                Label: ResolveString(action, "label"),
                Control: ResolveString(action, "control"),
                Value: ResolveString(action, "value"),
                Selector: ResolveString(action, "selector"),
                FieldLabel: fieldLabel,
                OptionText: NormalizeChooseOption(type, fieldLabel, optionText),
                Text: ResolveString(action, "text"),
                Exact: ResolveBool(action, "exact") ?? false,
                Enabled: ResolveBool(action, "enabled"),
                Name: ResolveString(action, "name"),
                Message: ResolveString(action, "message"),
                Paths: ResolveStringArray(action, "paths"),
                WaitForTexts: ResolveStringArray(action, "wait_for_texts")));
        }

        return results;
    }

    private static WeixinUploadQueueOptions ResolveUploadQueue(JsonElement queueElement, JsonElement uploadElement)
    {
        var items = ResolveUploadQueueItems(queueElement);
        if (items.Count == 0)
        {
            items = ResolveStringArray(uploadElement, "paths")
                .Select(path => new WeixinUploadQueueItem(path, true))
                .ToArray();
        }

        return new WeixinUploadQueueOptions(
            Text: ResolveString(queueElement, "text") ?? string.Empty,
            Selector: ResolveString(queueElement, "selector")
                      ?? ResolveString(uploadElement, "input_selector")
                      ?? "input[type='file']",
            Mode: ResolveString(queueElement, "mode") ?? "batch",
            Items: items,
            ItemTimeoutSeconds: ResolveInt(queueElement, "item_timeout_seconds")
                                ?? ResolveInt(uploadElement, "timeout_seconds")
                                ?? 3600,
            OnItemError: ResolveString(queueElement, "on_item_error") ?? "stop",
            OnItemTimeout: ResolveString(queueElement, "on_item_timeout") ?? "stop",
            RetryFailedUploads: ResolveBool(queueElement, "retry_failed_uploads") ?? false,
            RetryMaxRounds: ResolveInt(queueElement, "retry_max_rounds") ?? 3,
            RetryIntervalSeconds: ResolveInt(queueElement, "retry_interval_seconds") ?? 3,
            RetryActionText: ResolveString(queueElement, "retry_action_text") ?? "重试",
            RetryDeleteText: ResolveString(queueElement, "retry_delete_text") ?? "删除",
            RetryStableRounds: ResolveInt(queueElement, "retry_stable_rounds") ?? 2,
            SuccessTexts: ResolveStringArray(queueElement, "success_texts").Count > 0
                ? ResolveStringArray(queueElement, "success_texts")
                : ResolveStringArray(uploadElement, "success_texts"),
            ErrorTexts: ResolveStringArray(queueElement, "error_texts").Count > 0
                ? ResolveStringArray(queueElement, "error_texts")
                : ResolveStringArray(uploadElement, "error_texts"));
    }

    private static IReadOnlyList<WeixinUploadQueueItem> ResolveUploadQueueItems(JsonElement queueElement)
    {
        if (queueElement.ValueKind != JsonValueKind.Object ||
            !queueElement.TryGetProperty("items", out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<WeixinUploadQueueItem>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var path = ResolveString(item, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            results.Add(new WeixinUploadQueueItem(
                Path: path,
                Enabled: ResolveBool(item, "enabled") ?? true));
        }

        return results;
    }

    private static string? NormalizeChooseOption(string type, string? fieldLabel, string? optionText)
    {
        if (string.Equals(type, "choose", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fieldLabel?.Trim(), "剧目类型", StringComparison.Ordinal) &&
            string.Equals(optionText?.Trim(), "真人", StringComparison.Ordinal))
        {
            return "漫剧";
        }

        return optionText;
    }

    private static int? ResolveActualEpisodeCount(JsonElement secondPageElement)
    {
        var uploadQueueElement = TryGetObject(secondPageElement, "upload_queue");
        var queueItems = ResolveUploadQueueItems(uploadQueueElement);
        if (queueItems.Count > 0)
        {
            return queueItems.Count(item => item.Enabled);
        }

        var uploadElement = TryGetObject(secondPageElement, "upload");
        var paths = ResolveStringArray(uploadElement, "paths");
        return paths.Count > 0 ? paths.Count : null;
    }

    private static string NormalizeTrialEpisodes(string configuredValue, int? actualEpisodeCount)
    {
        if (!int.TryParse(configuredValue, out var trial) || trial <= 0)
        {
            trial = 3;
        }

        if (actualEpisodeCount is > 0 && trial > actualEpisodeCount.Value)
        {
            trial = actualEpisodeCount.Value;
        }

        return trial.ToString();
    }

    private static IReadOnlyDictionary<string, string> LoadGlobalConfig(string projectDirectory, string configDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in EnumerateGlobalConfigCandidates(projectDirectory, configDirectory).Reverse())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            foreach (var item in KeyValueConfigReader.Read(candidate))
            {
                if (string.IsNullOrWhiteSpace(item.Value) &&
                    result.TryGetValue(item.Key, out var existingValue) &&
                    !string.IsNullOrWhiteSpace(existingValue))
                {
                    continue;
                }

                result[item.Key] = item.Value;
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateGlobalConfigCandidates(string projectDirectory, string configDirectory)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[] { projectDirectory, configDirectory })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var current = Path.GetFullPath(root);
            while (!string.IsNullOrWhiteSpace(current))
            {
                foreach (var candidate in new[]
                         {
                             Path.Combine(current, "config", "config.json")
                         })
                {
                    if (visited.Add(candidate))
                    {
                        yield return candidate;
                    }
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                {
                    break;
                }

                current = parent;
            }
        }

        foreach (var candidate in EnumerateDesktopGlobalConfigCandidates())
        {
            if (visited.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateDesktopGlobalConfigCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "ShortDramaDesktop", "global-settings.json");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".weixin_channel_tool", "settings.json");
        }
    }

    private static string? ResolveConfigString(IReadOnlyDictionary<string, string> map, string key)
    {
        return map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static int? ResolveConfigInt(IReadOnlyDictionary<string, string> map, string key)
    {
        return map.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : null;
    }

    private static bool? ResolveConfigBool(IReadOnlyDictionary<string, string> map, string key)
    {
        return map.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : null;
    }

    private static double? ResolveConfigDouble(IReadOnlyDictionary<string, string> map, string key)
    {
        return map.TryGetValue(key, out var value) && double.TryParse(value, out var parsed)
            ? parsed
            : null;
    }

    private static JsonElement ResolveActionArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var actions) &&
            actions.ValueKind == JsonValueKind.Array)
        {
            return actions;
        }

        return default;
    }

    private static JsonElement TryGetObject(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return default;
    }

    private static string? ResolveString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? ResolveInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out numeric))
        {
            return numeric;
        }

        return null;
    }

    private static bool? ResolveBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static IReadOnlyList<string> ResolveStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return SplitStringList(value.GetString());
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<string>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var text = entry.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    results.Add(text);
                }
            }
        }

        return results;
    }

    private static IReadOnlyList<string> SplitStringList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Replace(';', '\n')
            .Replace(',', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static IReadOnlyList<string> ResolvePathArray(
        string baseDirectory,
        JsonElement element,
        params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var values = ResolveStringArray(element, propertyName);
            if (values.Count == 0)
            {
                continue;
            }

            return values
                .Select(path => ResolveOptionalPath(baseDirectory, path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        }

        return [];
    }

    private static IReadOnlyDictionary<string, string> ResolveStringMap(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = property.Value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(property.Name) && !string.IsNullOrWhiteSpace(text))
            {
                results[property.Name.Trim()] = text;
            }
        }

        return results;
    }

    private static IReadOnlyList<int> ResolveIntArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<int>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Number && entry.TryGetInt32(out var intValue))
            {
                results.Add(intValue);
            }
            else if (entry.ValueKind == JsonValueKind.String && int.TryParse(entry.GetString(), out intValue))
            {
                results.Add(intValue);
            }
        }

        return results;
    }

    private static double? ResolveDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var doubleValue))
        {
            return doubleValue;
        }

        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out doubleValue))
        {
            return doubleValue;
        }

        return null;
    }

    private static string ResolvePublishFinalActionText(string? finalAction)
    {
        var normalized = (finalAction ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "publish" or "发表" => "发表",
            "draft" or "save_draft" or "保存草稿" => "保存草稿",
            "none" or "noop" or "skip" or "只填不发" => "只填不发",
            _ => string.IsNullOrWhiteSpace(finalAction) ? "发表" : finalAction.Trim()
        };
    }

    private static string NormalizeVideoPublishRunStrategy(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "resume" or "resume_unfinished" or "断点续跑" => "resume",
            "retry_failed" or "retry_failed_only" or "failed" or "只重试失败集" => "retry_failed",
            _ => "all"
        };
    }

    private static string NormalizeVideoPublishSourceMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "material_clips" or "material_clip" or "material_highlights" or "highlight_clips" or "clip_highlights" => "material_clips",
            "custom_files" or "custom" or "files" => "custom_files",
            "downloaded_system_highlight" or "downloaded_system_highlights" or "downloaded_highlight" or "downloaded_highlights" => "downloaded_system_highlight",
            "material_video_download" or "material_download" or "downloaded_material_video" => "material_video_download",
            "system_highlight" or "system_highlights" or "highlight_system" or "generated_highlight" or "generated_highlights" => "system_highlight",
            "directory_publish" or "dir_publish" => "directory_publish",
            "source_videos" or "source" => "source_videos",
            "project_materials" or "project_material" => "project_materials",
            "new_drama_mount" or "newdramamount" or "new_drama" => "new_drama_mount",
            _ => "project"
        };
    }

    private static string NormalizeSystemHighlightTargetMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "type" or "types" or "video_type" or "video_types" ? "type" : "count";
    }

    private static IReadOnlyList<string> NormalizeSystemHighlightVideoTypes(IEnumerable<string> values)
    {
        var supported = new[] { "混剪", "解说", "切片" };
        var requested = values
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToHashSet(StringComparer.Ordinal);
        return supported
            .Where(item => requested.Count == 0 || requested.Contains(item))
            .DefaultIfEmpty(supported[0])
            .ToArray();
    }

    private static string ResolveOptionalPath(string baseDirectory, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }

        return Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(baseDirectory, configuredPath));
    }

    private static string ResolvePath(string baseDirectory, string? configuredPath, string fallbackPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(
                Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(baseDirectory, configuredPath));
        }

        return Path.GetFullPath(fallbackPath);
    }
}
