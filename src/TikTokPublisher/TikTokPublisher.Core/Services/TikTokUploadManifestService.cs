using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokUploadManifestService
{
    public const string DocumentType = "tiktok_upload_manifest";

    public static void Save(
        string sourceProjectDir,
        TikTokAccountProfile? account,
        TikTokUploadStagingService.StagingResult payload,
        Action<string>? log = null)
    {
        var context = ProjectWorkspaceService.LoadContext(sourceProjectDir);
        var source = Path.GetFullPath(context.SourceProjectDir);
        var workflow = Path.GetFullPath(context.WorkflowProjectDir);
        var projectInfo = ProjectInfoTextHelper.MergeProjectInfo(
            Path.Combine(source, "短剧信息.txt"),
            Path.Combine(workflow, "短剧信息.txt"));
        var title = FirstNonEmpty(
            projectInfo.GetValueOrDefault("新剧名"),
            projectInfo.GetValueOrDefault("剧名"),
            Path.GetFileName(workflow).TrimStart('_'),
            Path.GetFileName(source));
        var originalTitle = FirstNonEmpty(
            projectInfo.GetValueOrDefault("原剧名"),
            projectInfo.GetValueOrDefault("剧名"),
            Path.GetFileName(source));
        var description = FirstNonEmpty(
            projectInfo.GetValueOrDefault("简介"),
            projectInfo.GetValueOrDefault("描述"),
            projectInfo.GetValueOrDefault("剧情简介"));
        var posterPath = ProjectWorkspaceService.FindPosterInputFile(source, workflow) ?? "";

        var manifest = new Dictionary<string, object?>
        {
            ["generated_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["project_dir"] = source,
            ["workflow_project_dir"] = workflow,
            ["series_url"] = FirstNonEmpty(account?.TiktokSeriesUrl, TikTokUrls.DefaultSeriesDraftUrl),
            ["display_title"] = title,
            ["original_title"] = originalTitle,
            ["description"] = description,
            ["episode_count"] = payload.SourcePaths.Count,
            ["video_paths"] = payload.SourcePaths.Select(Path.GetFullPath).ToList(),
            ["upload_video_paths"] = payload.UploadPaths.Select(Path.GetFullPath).ToList(),
            ["poster_path"] = string.IsNullOrWhiteSpace(posterPath) ? "" : Path.GetFullPath(posterPath),
            ["publish_config"] = BuildPublishConfigSnapshot(account, workflow),
            ["web_upload_pending"] = false,
            ["notes"] = "TikTok Web 上传会复用本地登录态，自动填写新建剧集表单并执行对应提交动作。",
        };

        ProjectStateDocumentStore.SaveDocument(
            context.WorkspaceRoot,
            source,
            DocumentType,
            manifest,
            workflow);
        log?.Invoke("TikTok 上传清单已生成。");
    }

    public static Dictionary<string, object?> BuildPublishConfigSnapshot(
        TikTokAccountProfile? account,
        string? workflowProjectDir = null)
    {
        var options = TikTokPublishOptionsBuilder.FromAccount(account, workflowProjectDir);
        var settings = ClientSettingsStore.Load();
        var submitAction = NormalizeSubmitAction(account?.TiktokSubmitAction, account?.TiktokSubmitEnabled);
        var snapshot = new Dictionary<string, object?>
        {
            ["series_url"] = FirstNonEmpty(account?.TiktokSeriesUrl, TikTokUrls.DefaultSeriesDraftUrl),
            ["storage_state_path"] = account?.TiktokStorageStatePath ?? "",
            ["upload_profile_path"] = account?.TiktokUploadProfilePath ?? "",
            ["submit_action"] = submitAction,
            ["submit_enabled"] = string.Equals(submitAction, "submit", StringComparison.Ordinal),
            ["contract_id_mode"] = options.ContractIdMode,
            ["contract_id"] = options.ContractId,
            ["anchor_promotion_enabled"] = options.AnchorPromotionEnabled,
            ["target_audience_mode"] = options.TargetAudienceMode,
            ["genre_count"] = options.GenreCount,
            ["source_language"] = options.SourceLanguage,
            ["is_ai_drama"] = options.IsAiDrama,
            ["content_creation_type"] = options.ContentCreationType,
            ["publish_mode"] = options.PublishMode,
            ["consignment_enabled"] = options.ConsignmentEnabled,
            ["silence_validation_enabled"] = account?.TiktokSilenceValidationEnabled ?? true,
            ["max_continuous_silence_seconds"] = account?.TiktokMaxContinuousSilenceSeconds ?? 20,
            ["silence_threshold_db"] = account?.TiktokSilenceThresholdDb ?? -45.0,
            ["management_dedup_enabled"] = settings.ManagementDedupEnabled,
            ["management_dedup_scope"] = settings.ManagementDedupScope,
            ["paid_enabled"] = options.PaidEnabled,
            ["profile_preview_episodes"] = options.ProfilePreviewEpisodes,
        };

        if (options.PaidEnabled)
        {
            snapshot["free_preview_episodes"] = options.FreePreviewEpisodes;
            snapshot["expected_full_price_mode"] = options.ExpectedFullPriceMode;
            if (string.Equals(options.ExpectedFullPriceMode, "option_index", StringComparison.Ordinal))
            {
                snapshot["expected_full_price_option_index"] = options.ExpectedFullPriceOptionIndex;
            }
            else
            {
                snapshot["expected_full_price_value"] = options.ExpectedFullPriceValue;
                snapshot["expected_full_price_label"] = options.ExpectedFullPriceLabel;
            }
        }

        return snapshot;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
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
}
