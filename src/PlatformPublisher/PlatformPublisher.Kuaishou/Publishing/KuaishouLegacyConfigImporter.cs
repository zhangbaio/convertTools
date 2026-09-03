using System.Reflection;
using System.Text.Json;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouLegacyImportResult(int ImportedFields, int ImportedSensitiveFields, string SourcePath);

public static class KuaishouLegacyConfigImporter
{
    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".weixin_channel_tool",
        "settings.json");

    private static readonly IReadOnlyDictionary<string, string> CommonFields = new Dictionary<string, string>
    {
        [nameof(KuaishouPersonalConfig.ApiBaseUrl)] = "kuaishou_api_base_url",
        [nameof(KuaishouPersonalConfig.AppName)] = "kuaishou_app_name",
        [nameof(KuaishouPersonalConfig.AppId)] = "kuaishou_app_id",
        [nameof(KuaishouPersonalConfig.AppSecret)] = "kuaishou_app_secret",
        [nameof(KuaishouPersonalConfig.AuthorizeBaseUrl)] = "kuaishou_authorize_base_url",
        [nameof(KuaishouPersonalConfig.AuthorizeRedirectUri)] = "kuaishou_authorize_redirect_uri",
        [nameof(KuaishouPersonalConfig.AuthorizeScope)] = "kuaishou_authorize_scope",
        [nameof(KuaishouPersonalConfig.AuthorizeState)] = "kuaishou_authorize_state",
        [nameof(KuaishouPersonalConfig.AuthorizeOauthType)] = "kuaishou_authorize_oauth_type",
        [nameof(KuaishouPersonalConfig.AuthCode)] = "kuaishou_auth_code",
        [nameof(KuaishouPersonalConfig.AccessToken)] = "kuaishou_access_token",
        [nameof(KuaishouPersonalConfig.AccessTokenExpiresAt)] = "kuaishou_access_token_expires_at",
        [nameof(KuaishouPersonalConfig.RefreshToken)] = "kuaishou_refresh_token",
        [nameof(KuaishouPersonalConfig.RefreshTokenExpiresAt)] = "kuaishou_refresh_token_expires_at",
        [nameof(KuaishouPersonalConfig.TokenHeader)] = "kuaishou_token_header",
        [nameof(KuaishouPersonalConfig.RemoteTokenEnabled)] = "kuaishou_remote_token_enabled",
        [nameof(KuaishouPersonalConfig.AdvertiserId)] = "kuaishou_advertiser_id",
        [nameof(KuaishouPersonalConfig.CommitmentTemplateDocxPath)] = "kuaishou_commitment_template_docx_path",
        [nameof(KuaishouPersonalConfig.CommitmentSealPath)] = "kuaishou_commitment_seal_path",
        [nameof(KuaishouPersonalConfig.CommitmentRecipientCompanyName)] = "kuaishou_commitment_recipient_company_name",
        [nameof(KuaishouPersonalConfig.CategoryLevel1)] = "kuaishou_category_level1",
        [nameof(KuaishouPersonalConfig.CategoryLevel2)] = "kuaishou_category_level2",
        [nameof(KuaishouPersonalConfig.CategoryLevel3)] = "kuaishou_category_level3",
        [nameof(KuaishouPersonalConfig.ContentType)] = "kuaishou_content_type",
        [nameof(KuaishouPersonalConfig.ProductionMethod)] = "kuaishou_comic_production_method",
        [nameof(KuaishouPersonalConfig.ProductionForm)] = "kuaishou_production_form",
        [nameof(KuaishouPersonalConfig.ProductionYear)] = "kuaishou_production_year",
        [nameof(KuaishouPersonalConfig.ProductionCost)] = "kuaishou_production_cost",
        [nameof(KuaishouPersonalConfig.AverageEpisodeMinutes)] = "kuaishou_episode_average_duration_minutes",
        [nameof(KuaishouPersonalConfig.BroadcastPlatform)] = "kuaishou_broadcast_platform",
        [nameof(KuaishouPersonalConfig.BroadcastChannel)] = "kuaishou_broadcast_channel",
        [nameof(KuaishouPersonalConfig.Finished)] = "kuaishou_is_finished",
        [nameof(KuaishouPersonalConfig.HasRecordNumber)] = "kuaishou_has_record_number",
        [nameof(KuaishouPersonalConfig.ActorInfoJson)] = "kuaishou_actor_info_json",
        [nameof(KuaishouPersonalConfig.ActorLibraryText)] = "kuaishou_actor_library_text",
        [nameof(KuaishouPersonalConfig.DirectorInfoJson)] = "kuaishou_director_info_json",
        [nameof(KuaishouPersonalConfig.ScreenwriterInfoJson)] = "kuaishou_screenwriter_info_json",
        [nameof(KuaishouPersonalConfig.ProductionOrganization)] = "kuaishou_production_org",
        [nameof(KuaishouPersonalConfig.FullSceneDisplay)] = "kuaishou_full_scene_display",
        [nameof(KuaishouPersonalConfig.PlotLabels)] = "kuaishou_plot_list",
        [nameof(KuaishouPersonalConfig.TagLabels)] = "kuaishou_tag_list",
        [nameof(KuaishouPersonalConfig.AuthorDeclaration)] = "kuaishou_author_declaration",
        [nameof(KuaishouPersonalConfig.HasCopyrightProof)] = "kuaishou_has_copyright_proof",
        [nameof(KuaishouPersonalConfig.CopyrightProofType)] = "kuaishou_copyright_proof_type",
        [nameof(KuaishouPersonalConfig.CopyrightValidStartTime)] = "kuaishou_copyright_valid_start_time",
        [nameof(KuaishouPersonalConfig.CopyrightValidEndTime)] = "kuaishou_copyright_valid_end_time",
        [nameof(KuaishouPersonalConfig.HasSubAuthorizationRight)] = "kuaishou_has_sub_authorization_right",
        [nameof(KuaishouPersonalConfig.SaleType)] = "kuaishou_sale_type",
        [nameof(KuaishouPersonalConfig.EpisodePrice)] = "kuaishou_episode_price_yuan",
        [nameof(KuaishouPersonalConfig.FreeEpisodeCount)] = "kuaishou_free_episode_count",
        [nameof(KuaishouPersonalConfig.UnlockEpisodeCount)] = "kuaishou_unlock_count",
        [nameof(KuaishouPersonalConfig.FirstPageAction)] = "kuaishou_first_page_submit_action",
        [nameof(KuaishouPersonalConfig.FinalAction)] = "kuaishou_final_submit_action",
        [nameof(KuaishouPersonalConfig.LoginTimeoutSeconds)] = "kuaishou_login_timeout_seconds",
        [nameof(KuaishouPersonalConfig.QueueMaxParallelProjects)] = "kuaishou_queue_max_parallel_projects",
        [nameof(KuaishouPersonalConfig.SubmitPreCheckWaitSeconds)] = "kuaishou_submit_pre_check_wait_seconds",
        [nameof(KuaishouPersonalConfig.SubmitReadyCheckIntervalSeconds)] = "kuaishou_submit_ready_check_interval_seconds",
        [nameof(KuaishouPersonalConfig.SubmitReadyCheckMax)] = "kuaishou_submit_ready_check_max",
        [nameof(KuaishouPersonalConfig.SubmitRetryIntervalSeconds)] = "kuaishou_submit_retry_interval_seconds",
        [nameof(KuaishouPersonalConfig.SubmitRetryMax)] = "kuaishou_submit_retry_max",
        [nameof(KuaishouPersonalConfig.VideoStorageProvider)] = "kuaishou_video_storage_provider",
        [nameof(KuaishouPersonalConfig.PublicStorageUploadConcurrency)] = "kuaishou_public_storage_upload_concurrency",
        [nameof(KuaishouPersonalConfig.PublicStorageUploadRetryCount)] = "kuaishou_public_storage_upload_retry_count",
        [nameof(KuaishouPersonalConfig.ProjectImageTemplateRoot)] = "kuaishou_project_image_template_root",
        [nameof(KuaishouPersonalConfig.ProjectImageTemplateId)] = "kuaishou_project_image_template_id",
        [nameof(KuaishouPersonalConfig.PrepareDownload)] = "kuaishou_prepare_download",
        [nameof(KuaishouPersonalConfig.PrepareRewriteInfo)] = "kuaishou_prepare_rewrite_info",
        [nameof(KuaishouPersonalConfig.PrepareGeneratePoster)] = "kuaishou_prepare_generate_poster",
        [nameof(KuaishouPersonalConfig.PrepareGenerateGuaranteeLetter)] = "kuaishou_prepare_generate_guarantee_letter",
        [nameof(KuaishouPersonalConfig.PrepareGenerateProjectImages)] = "kuaishou_prepare_generate_project_images",
        [nameof(KuaishouPersonalConfig.PrepareAutoFillInfo)] = "kuaishou_prepare_auto_fill_info",
        [nameof(KuaishouPersonalConfig.PrepareForceRerunCompletedSteps)] = "kuaishou_prepare_force_rerun_completed_steps",
        [nameof(KuaishouPersonalConfig.SeriesCreatePath)] = "kuaishou_series_create_path",
        [nameof(KuaishouPersonalConfig.SeriesBaseInfoPath)] = "kuaishou_series_base_info_path",
        [nameof(KuaishouPersonalConfig.EpisodeUploadPath)] = "kuaishou_episode_upload_path",
        [nameof(KuaishouPersonalConfig.EpisodeCoverUpdatePath)] = "kuaishou_episode_cover_update_path",
        [nameof(KuaishouPersonalConfig.SeriesSubmitPath)] = "kuaishou_series_submit_path",
        [nameof(KuaishouPersonalConfig.MaterialUploadPath)] = "kuaishou_material_upload_path",
        [nameof(KuaishouPersonalConfig.EpisodeFileFieldName)] = "kuaishou_episode_file_field_name",
        [nameof(KuaishouPersonalConfig.StepCreateSeries)] = "kuaishou_step_create_series",
        [nameof(KuaishouPersonalConfig.StepUploadImages)] = "kuaishou_step_upload_images",
        [nameof(KuaishouPersonalConfig.StepUploadVideos)] = "kuaishou_step_upload_videos",
        [nameof(KuaishouPersonalConfig.StepSubmitSeries)] = "kuaishou_step_submit_series",
        [nameof(KuaishouPersonalConfig.StepOnlineSeries)] = "kuaishou_step_online_series",
        [nameof(KuaishouPersonalConfig.StepDistributionSeries)] = "kuaishou_step_distribution_series",
        [nameof(KuaishouPersonalConfig.StepDeleteOssVideos)] = "kuaishou_step_delete_oss_videos",
        [nameof(KuaishouPersonalConfig.DistributionEnabled)] = "kuaishou_distribution_enabled",
        [nameof(KuaishouPersonalConfig.DistributionApiPath)] = "kuaishou_distribution_api_path",
        [nameof(KuaishouPersonalConfig.DistributionMode)] = "kuaishou_distribution_mode",
        [nameof(KuaishouPersonalConfig.DistributionDefaultRatePercent)] = "kuaishou_distribution_default_rate_percent",
        [nameof(KuaishouPersonalConfig.DistributionDistributorAccountsJson)] = "kuaishou_distribution_distributor_accounts_json",
        [nameof(KuaishouPersonalConfig.DistributionSubmitEnabled)] = "kuaishou_distribution_submit_enabled",
        [nameof(KuaishouPersonalConfig.DistributionAllowJuxing)] = "kuaishou_distribution_allow_juxing",
        [nameof(KuaishouPersonalConfig.DistributionAllowOnlineTime)] = "kuaishou_distribution_allow_online_time",
        [nameof(KuaishouPersonalConfig.DistributionHeadlessEnabled)] = "kuaishou_distribution_headless_enabled",
        [nameof(KuaishouPersonalConfig.DistributionLoginTimeoutSeconds)] = "kuaishou_distribution_login_timeout_seconds",
        [nameof(KuaishouPersonalConfig.DistributionManualInterventionEnabled)] = "kuaishou_distribution_manual_intervention_enabled",
        [nameof(KuaishouPersonalConfig.DistributionLoginNotifyEnabled)] = "kuaishou_distribution_login_notify_enabled",
        [nameof(KuaishouPersonalConfig.DistributionFeishuNotifyEnabled)] = "kuaishou_distribution_feishu_notify_enabled",
        [nameof(KuaishouPersonalConfig.AutoOnlineEnabled)] = "kuaishou_auto_online_enabled",
        [nameof(KuaishouPersonalConfig.AutoOnlineIntervalMinutes)] = "kuaishou_auto_online_interval_minutes",
        [nameof(KuaishouPersonalConfig.AutoOnlineMaxItemsPerRound)] = "kuaishou_auto_online_max_items_per_round",
        [nameof(KuaishouPersonalConfig.AutoOnlineMaxWaitDays)] = "kuaishou_auto_online_max_wait_days",
        [nameof(KuaishouPersonalConfig.AutoOnlineOnlyWhenIdle)] = "kuaishou_auto_online_only_when_idle",
        [nameof(KuaishouPersonalConfig.OnlineAutoDistributionEnabled)] = "kuaishou_online_auto_distribution_enabled",
        [nameof(KuaishouPersonalConfig.OnlineCleanupEnabled)] = "kuaishou_online_cleanup_enabled",
        [nameof(KuaishouPersonalConfig.OnlineKeepOnlineDays)] = "kuaishou_online_keep_online_days",
        [nameof(KuaishouPersonalConfig.OnlineKeepRejectedDays)] = "kuaishou_online_keep_rejected_days",
        [nameof(KuaishouPersonalConfig.OnlineKeepInvalidDays)] = "kuaishou_online_keep_invalid_days",
        [nameof(KuaishouPersonalConfig.OnlineKeepManualOnlineDays)] = "kuaishou_online_keep_manual_online_days",
        [nameof(KuaishouPersonalConfig.OnlineNotifyEnabled)] = "kuaishou_online_notify_enabled",
        [nameof(KuaishouPersonalConfig.OnlineNotifyRejectedEnabled)] = "kuaishou_online_notify_rejected_enabled",
        [nameof(KuaishouPersonalConfig.OnlineNotifyRejectedOnce)] = "kuaishou_online_notify_rejected_once",
        [nameof(KuaishouPersonalConfig.OnlineShowResultDialog)] = "kuaishou_online_show_result_dialog",
        [nameof(KuaishouPersonalConfig.OnlineNoticeConditions)] = "kuaishou_online_notice_conditions",
        [nameof(KuaishouPersonalConfig.OnlineOfflinePath)] = "kuaishou_online_offline_path",
        [nameof(KuaishouPersonalConfig.OssCleanupEnabled)] = "kuaishou_oss_cleanup_enabled",
        [nameof(KuaishouPersonalConfig.OssCleanupDeleteOnProjectDelete)] = "kuaishou_oss_cleanup_delete_on_project_delete",
        [nameof(KuaishouPersonalConfig.OssCleanupIntervalMinutes)] = "kuaishou_oss_cleanup_interval_minutes",
        [nameof(KuaishouPersonalConfig.OssCleanupMaxAttempts)] = "kuaishou_oss_cleanup_max_attempts",
        [nameof(KuaishouPersonalConfig.OssCleanupRetentionHours)] = "kuaishou_oss_cleanup_retention_hours",
        [nameof(KuaishouPersonalConfig.AiComplianceReviewEnabled)] = "kuaishou_ai_compliance_review_enabled",
        [nameof(KuaishouPersonalConfig.SynopsisAiRewriteEnabled)] = "kuaishou_synopsis_ai_rewrite_enabled",
        [nameof(KuaishouPersonalConfig.SynopsisPolicyJson)] = "kuaishou_synopsis_policy_json",
    };

    private static readonly HashSet<string> SensitiveProperties =
    [
        nameof(KuaishouPersonalConfig.AppSecret),
        nameof(KuaishouPersonalConfig.AuthCode),
        nameof(KuaishouPersonalConfig.AccessToken),
        nameof(KuaishouPersonalConfig.RefreshToken),
    ];

    public static KuaishouLegacyImportResult Import(
        string path,
        KuaishouPersonalConfig target,
        PublishPlatform platform)
    {
        var fullPath = Path.GetFullPath(path);
        using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("旧版 settings.json 根节点必须是对象。");

        var values = root.EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var imported = 0;
        var sensitive = 0;
        foreach (var mapping in CommonFields)
        {
            if (!values.TryGetValue(mapping.Value, out var value)) continue;
            if (!TryAssign(target, mapping.Key, value)) continue;
            imported++;
            if (SensitiveProperties.Contains(mapping.Key)) sensitive++;
        }
        imported += AssignAliases(values, target);

        var prefix = platform == PublishPlatform.KuaishouEnterpriseRevenue
            ? "kuaishou_enterprise_"
            : "kuaishou_personal_";
        imported += AssignEdition(values, target, prefix, platform);
        target.LegacyImportCompleted = true;
        return new(imported, sensitive, fullPath);
    }

    private static int AssignEdition(
        IReadOnlyDictionary<string, JsonElement> values,
        KuaishouPersonalConfig target,
        string prefix,
        PublishPlatform platform)
    {
        var mappings = new Dictionary<string, string>
        {
            [nameof(KuaishouPersonalConfig.EntryUrl)] = prefix + "upload_entry_url",
            [nameof(KuaishouPersonalConfig.RealName)] = prefix + "real_name",
            [nameof(KuaishouPersonalConfig.Gender)] = prefix + "gender",
            [nameof(KuaishouPersonalConfig.KuaishouNickname)] = prefix + "nickname",
            [nameof(KuaishouPersonalConfig.KuaishouId)] = prefix + "kuaishou_id",
            [nameof(KuaishouPersonalConfig.Username)] = prefix + "username",
            [nameof(KuaishouPersonalConfig.LastWorkspace)] = prefix + "last_workspace",
            [nameof(KuaishouPersonalConfig.ArchiveRootDirectory)] = prefix + "archive_root_dir",
            [nameof(KuaishouPersonalConfig.Headless)] = prefix + "headless_enabled",
            [nameof(KuaishouPersonalConfig.KeepBrowserOpenOnFailure)] = prefix + "keep_browser_open_on_failure",
            [nameof(KuaishouPersonalConfig.CommitmentPdfPath)] = prefix + "commitment_pdf_path",
            [nameof(KuaishouPersonalConfig.ProjectImageTemplateId)] = prefix + "project_image_template_id",
            [nameof(KuaishouPersonalConfig.PrepareDownload)] = prefix + "prepare_download",
            [nameof(KuaishouPersonalConfig.PrepareRewriteInfo)] = prefix + "prepare_rewrite_info",
            [nameof(KuaishouPersonalConfig.PrepareGeneratePoster)] = prefix + "prepare_generate_poster",
            [nameof(KuaishouPersonalConfig.PrepareGenerateGuaranteeLetter)] = prefix + "prepare_generate_guarantee_letter",
            [nameof(KuaishouPersonalConfig.PrepareGenerateProjectImages)] = prefix + "prepare_generate_project_images",
            [nameof(KuaishouPersonalConfig.PrepareAutoFillInfo)] = prefix + "prepare_auto_fill_info",
            [nameof(KuaishouPersonalConfig.PrepareForceRerunCompletedSteps)] = prefix + "prepare_force_rerun_completed_steps",
            [nameof(KuaishouPersonalConfig.StepUploadVideos)] = prefix + "step_upload_series",
            [nameof(KuaishouPersonalConfig.FirstPageAction)] = prefix + "first_page_submit_action",
            [nameof(KuaishouPersonalConfig.FinalAction)] = prefix + "final_submit_action",
            [nameof(KuaishouPersonalConfig.LoginTimeoutSeconds)] = prefix + "login_timeout_seconds",
            [nameof(KuaishouPersonalConfig.AuditRecordHeadless)] = prefix + "audit_record_headless",
            [nameof(KuaishouPersonalConfig.AuditRecordPages)] = prefix + "audit_record_pages",
        };
        var count = 0;
        foreach (var mapping in mappings)
        {
            if (!values.TryGetValue(mapping.Value, out var value)) continue;
            if (TryAssign(target, mapping.Key, value)) count++;
        }
        return count;
    }

    private static int AssignAliases(
        IReadOnlyDictionary<string, JsonElement> values,
        KuaishouPersonalConfig target)
    {
        var mappings = new Dictionary<string, string>
        {
            ["kuaishou_keep_browser_open_on_failure"] = nameof(KuaishouPersonalConfig.KeepBrowserOpenOnFailure),
            ["kuaishou_product_method"] = nameof(KuaishouPersonalConfig.ProductMethod),
            ["kuaishou_series_content_type"] = nameof(KuaishouPersonalConfig.SeriesContentType),
            ["kuaishou_series_price_yuan"] = nameof(KuaishouPersonalConfig.SeriesPrice),
            ["kuaishou_revenue_share_full_scene_display"] = nameof(KuaishouPersonalConfig.RevenueShareFullSceneDisplay),
            ["kuaishou_revenue_share_headless_enabled"] = nameof(KuaishouPersonalConfig.RevenueShareHeadless),
            ["kuaishou_step_upload_videos_oss"] = nameof(KuaishouPersonalConfig.StepUploadVideosOss),
            ["kuaishou_archive_root_dir"] = nameof(KuaishouPersonalConfig.ArchiveRootDirectory),
            ["kuaishou_last_workspace"] = nameof(KuaishouPersonalConfig.LastWorkspace),
            ["kuaishou_broadcast_time_mode"] = nameof(KuaishouPersonalConfig.BroadcastTimeMode),
            ["kuaishou_special_theme"] = nameof(KuaishouPersonalConfig.SpecialTheme),
            ["kuaishou_small_amount_unlock"] = nameof(KuaishouPersonalConfig.SmallAmountUnlock),
            ["kuaishou_new_title_alias_enabled"] = nameof(KuaishouPersonalConfig.NewTitleAliasEnabled),
            ["kuaishou_new_title_alias_position"] = nameof(KuaishouPersonalConfig.NewTitleAliasPosition),
            ["kuaishou_new_title_alias_value"] = nameof(KuaishouPersonalConfig.NewTitleAliasValue),
            ["kuaishou_author_backfill_concurrent"] = nameof(KuaishouPersonalConfig.AuthorBackfillConcurrent),
            ["kuaishou_form_options_json"] = nameof(KuaishouPersonalConfig.FormOptionsJson),
            ["kuaishou_default_fields_json"] = nameof(KuaishouPersonalConfig.DefaultFieldsJson),
            ["kuaishou_audit_record_include_date"] = nameof(KuaishouPersonalConfig.AuditRecordIncludeDate),
            ["kuaishou_audit_record_only_reviewing"] = nameof(KuaishouPersonalConfig.AuditRecordOnlyReviewing),
            ["kuaishou_settlement_full_info_system_prompt"] = nameof(KuaishouPersonalConfig.SettlementFullInfoSystemPrompt),
            ["kuaishou_settlement_full_info_batch_prompt"] = nameof(KuaishouPersonalConfig.SettlementFullInfoBatchPrompt),
            ["kuaishou_settlement_full_info_retry_prompt"] = nameof(KuaishouPersonalConfig.SettlementFullInfoRetryPrompt),
        };
        var count = 0;
        foreach (var mapping in mappings)
        {
            if (!values.TryGetValue(mapping.Key, out var value)) continue;
            if (TryAssign(target, mapping.Value, value)) count++;
        }
        return count;
    }

    private static bool TryAssign(KuaishouPersonalConfig target, string propertyName, JsonElement value)
    {
        var property = typeof(KuaishouPersonalConfig).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.CanWrite != true) return false;
        try
        {
            object? converted = property.PropertyType == typeof(string)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString()
                : property.PropertyType == typeof(bool)
                    ? ReadBoolean(value)
                    : property.PropertyType == typeof(int)
                        ? ReadInt32(value)
                        : null;
            if (converted is null) return false;
            property.SetValue(target, converted);
            return true;
        }
        catch { return false; }
    }

    private static bool ReadBoolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
        JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
        _ => false,
    };

    private static int ReadInt32(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out var number) => number,
        JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
        _ => 0,
    };
}
