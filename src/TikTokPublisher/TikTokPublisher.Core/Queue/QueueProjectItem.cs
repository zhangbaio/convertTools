namespace TikTokPublisher.Core.Queue;

public static class QueueStepKeys
{
    public const string Download = "download";
    public const string RewriteInfo = "rewrite_info";
    public const string GeneratePoster = "generate_poster";
    public const string GenerateEpisodeScript = "generate_episode_script";
    public const string GenerateAiScriptOutline = "generate_ai_script_outline";
    public const string GenerateAiDramaMaterials = "generate_ai_drama_materials";
    public const string GenerateRoleVector = "generate_role_vector";
    public const string GenerateProjectImages = "generate_project_images";
    public const string GenerateProofMaterial = "generate_proof_material";
    public const string GenerateTimestampCertificate = "generate_timestamp_certificate";
    public const string SmallVideoRepair = "small_video_repair";
    public const string VideoTranslate = "video_translate";
    public const string MaterialValidate = "material_validate";
    public const string DeleteSourceVideos = "delete_source_videos";
    public const string UploadSeries = "upload_series";
}

public static class QueueStepStatus
{
    public const string Pending = "待执行";
    public const string Running = "执行中";
    public const string Completed = "已完成";
    public const string Failed = "失败";
    public const string Stopped = "已停止";
    public const string WaitingUploadSlot = "待上传槽位";
    public const string ManualIntervention = "等待人工介入";
    public const string Skipped = "已跳过";
}

public sealed class QueueProjectItem
{
    private const string LegacyLiveActionStep = "detect_live_action";
    private const string LegacyLiveActionBlockedStatus = "真人剧已拦截";
    private static readonly string[] RemovedStepKeys = ["silence_detect", "silence_repair"];

    public string ProjectDir { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public string NewTitle { get; set; } = "";
    public int EpisodeCount { get; set; }
    /// <summary>视频方向：1=竖屏，0=横屏，-1=未知。</summary>
    public int VideoVertical { get; set; } = -1;
    public string GenreCategory { get; set; } = "";
    public string Description { get; set; } = "";
    public string QueueEntryDramaType { get; set; } = "";
    public string AccountProfileId { get; set; } = "";
    public string AccountProfileName { get; set; } = "";
    public string QueuedAt { get; set; } = "";
    public string UploadCompletedAt { get; set; } = "";
    public string ProofMaterialStatementDate { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string CurrentStep { get; set; } = "";
    public string StatusText { get; set; } = QueueStepStatus.Pending;
    public string LastError { get; set; } = "";
    public string Remark { get; set; } = "";
    public string ManualUploadStatus { get; set; } = "";
    public Dictionary<string, string> StepStates { get; set; } = new();
    public bool Archived { get; set; }

    public string? PrimaryVideoPath { get; set; }
    public string? CoverPath { get; set; }

    public string Title => !string.IsNullOrWhiteSpace(NewTitle) ? NewTitle : OriginalTitle;

    public string UploadSeriesStatus =>
        StepStates.TryGetValue(QueueStepKeys.UploadSeries, out var status) ? status : QueueStepStatus.Pending;

    public bool IsUploadCompleted => UploadSeriesStatus == QueueStepStatus.Completed;

    public bool IsPendingUpload => Enabled && !Archived && !IsUploadCompleted;

    public void NormalizeStepStates()
    {
        RecoverLegacyLiveActionBlock();
        RemoveRetiredSteps();

        // An explicit pending proof-material state must survive normalization (for example,
        // after a title rename). Only legacy uploaded records that predate this step are
        // backfilled as completed.
        var hadEpisodeScriptState = StepStates.ContainsKey(QueueStepKeys.GenerateEpisodeScript);
        var hadAiScriptOutlineState = StepStates.ContainsKey(QueueStepKeys.GenerateAiScriptOutline);
        var hadAiDramaMaterialsState = StepStates.ContainsKey(QueueStepKeys.GenerateAiDramaMaterials);
        var hadRoleVectorState = StepStates.ContainsKey(QueueStepKeys.GenerateRoleVector);
        var hadTimestampState = StepStates.ContainsKey(QueueStepKeys.GenerateTimestampCertificate);

        foreach (var key in new[]
                 {
                     QueueStepKeys.Download,
                     QueueStepKeys.RewriteInfo,
                     QueueStepKeys.GeneratePoster,
                     QueueStepKeys.GenerateEpisodeScript,
                     QueueStepKeys.GenerateAiScriptOutline,
                     QueueStepKeys.GenerateAiDramaMaterials,
                     QueueStepKeys.GenerateRoleVector,
                     QueueStepKeys.GenerateProjectImages,
                     QueueStepKeys.GenerateProofMaterial,
                     QueueStepKeys.GenerateTimestampCertificate,
                     QueueStepKeys.DeleteSourceVideos,
                     QueueStepKeys.UploadSeries,
                     QueueStepKeys.MaterialValidate,
                     QueueStepKeys.SmallVideoRepair,
                     QueueStepKeys.VideoTranslate,
                 })
            StepStates.TryAdd(key, QueueStepStatus.Pending);

        if (!hadRoleVectorState && HasCurrentRoleVector())
            StepStates[QueueStepKeys.GenerateRoleVector] = QueueStepStatus.Completed;

        if (StepStates.GetValueOrDefault(QueueStepKeys.UploadSeries) == QueueStepStatus.Completed)
        {
            // Legacy uploaded rows predate several generation steps. Never infer that
            // a local artifact exists merely because the platform upload completed;
            // cross-computer copies and cleanup can retain the database row without
            // retaining its generated files.
            if (!hadEpisodeScriptState && HasCurrentEpisodeScript())
                StepStates[QueueStepKeys.GenerateEpisodeScript] = QueueStepStatus.Completed;
            if (!hadAiScriptOutlineState && HasCurrentAiScriptOutline())
                StepStates[QueueStepKeys.GenerateAiScriptOutline] = QueueStepStatus.Completed;
            if (!hadAiDramaMaterialsState && HasCurrentAiDramaMaterials())
                StepStates[QueueStepKeys.GenerateAiDramaMaterials] = QueueStepStatus.Completed;
            if (!hadRoleVectorState && HasCurrentRoleVector())
                StepStates[QueueStepKeys.GenerateRoleVector] = QueueStepStatus.Completed;
            if (!hadTimestampState && HasCurrentTimestampCertificate())
                StepStates[QueueStepKeys.GenerateTimestampCertificate] = QueueStepStatus.Completed;
            if (StepStates.GetValueOrDefault(QueueStepKeys.MaterialValidate) == QueueStepStatus.Pending &&
                HasCurrentMaterialValidation())
                StepStates[QueueStepKeys.MaterialValidate] = QueueStepStatus.Completed;
            if (StepStates.GetValueOrDefault(QueueStepKeys.GenerateProjectImages) == QueueStepStatus.Pending &&
                HasCurrentProjectImages())
                StepStates[QueueStepKeys.GenerateProjectImages] = QueueStepStatus.Completed;
            if (StepStates.GetValueOrDefault(QueueStepKeys.SmallVideoRepair) == QueueStepStatus.Pending &&
                HasCurrentSmallVideoRepair())
                StepStates[QueueStepKeys.SmallVideoRepair] = QueueStepStatus.Completed;
        }
    }

    private bool HasCurrentEpisodeScript() =>
        TikTokPublisher.Core.Services.TikTokEpisodeScriptService.HasCurrentOutput(this, account: null);

    private bool HasCurrentAiScriptOutline() =>
        TikTokPublisher.Core.Services.TikTokAiScriptOutlineService.HasCurrentOutput(this);

    private bool HasCurrentAiDramaMaterials()
    {
        try
        {
            var workflow = ProjectWorkspaceService.ResolveWorkflowProjectDir(ProjectDir);
            return TikTokPublisher.Core.Services.TikTokAiDramaProductionMaterialService.HasCurrentOutput(workflow);
        }
        catch
        {
            return false;
        }
    }

    private bool HasCurrentTimestampCertificate() =>
        TikTokPublisher.Core.Services.TikTokTimestampCertificateService.HasCurrentOutput(this);

    private bool HasCurrentMaterialValidation() =>
        TikTokPublisher.Core.Services.TikTokMaterialValidationService.HasCurrentValidationState(ProjectDir);

    private bool HasCurrentProjectImages() =>
        TikTokPublisher.Core.Services.TikTokProjectImageService.HasCurrentProjectImages(ProjectDir);

    private bool HasCurrentSmallVideoRepair() =>
        !TikTokPublisher.Core.Services.TikTokSmallVideoRepairService.NeedsRepair(ProjectDir);

    private bool HasCurrentRoleVector()
    {
        try
        {
            var workflow = ProjectWorkspaceService.ResolveWorkflowProjectDir(ProjectDir);
            return TikTokPublisher.Core.Services.TikTokRoleVectorService.HasCurrentOutput(workflow);
        }
        catch
        {
            return false;
        }
    }

    private void RecoverLegacyLiveActionBlock()
    {
        var wasBlocked = string.Equals(StatusText, LegacyLiveActionBlockedStatus, StringComparison.Ordinal) ||
                         string.Equals(
                             StepStates.GetValueOrDefault(LegacyLiveActionStep),
                             LegacyLiveActionBlockedStatus,
                             StringComparison.Ordinal);

        // 真人检测步骤已经移除。旧版本在判定为真人剧时会把所有后续生产步骤
        // 批量标记为“已跳过”；只迁移带有明确拦截标记的记录，避免覆盖用户主动跳过。
        StepStates.Remove(LegacyLiveActionStep);
        if (!wasBlocked)
            return;

        foreach (var stepKey in StepStates.Keys.ToArray())
        {
            if (string.Equals(StepStates[stepKey], QueueStepStatus.Skipped, StringComparison.Ordinal))
                StepStates[stepKey] = QueueStepStatus.Pending;
        }

        StatusText = QueueStepStatus.Pending;
        CurrentStep = "";
        LastError = "";
    }

    private void RemoveRetiredSteps()
    {
        foreach (var stepKey in RemovedStepKeys)
            StepStates.Remove(stepKey);

        if (RemovedStepKeys.Contains(CurrentStep, StringComparer.Ordinal))
            CurrentStep = "";
    }

    public Dictionary<string, object?> ToPayload()
    {
        NormalizeStepStates();
        return new Dictionary<string, object?>
        {
            ["project_dir"] = ProjectDir,
            ["display_name"] = DisplayName,
            ["original_title"] = OriginalTitle,
            ["new_title"] = NewTitle,
            ["episode_count"] = EpisodeCount,
            ["video_vertical"] = VideoVertical,
            ["genre_category"] = GenreCategory,
            ["description"] = Description,
            ["queue_entry_drama_type"] = QueueEntryDramaType,
            ["account_profile_id"] = AccountProfileId,
            ["account_profile_name"] = AccountProfileName,
            ["queued_at"] = QueuedAt,
            ["upload_completed_at"] = UploadCompletedAt,
            ["proof_material_statement_date"] = ProofMaterialStatementDate,
            ["enabled"] = Enabled,
            ["current_step"] = CurrentStep,
            ["status_text"] = StatusText,
            ["last_error"] = LastError,
            ["remark"] = Remark,
            ["manual_upload_status"] = ManualUploadStatus,
            ["step_states"] = new Dictionary<string, string>(StepStates),
            ["archived"] = Archived,
            ["primary_video_path"] = PrimaryVideoPath,
            ["cover_path"] = CoverPath,
        };
    }

    public static QueueProjectItem FromPayload(Dictionary<string, object?> payload)
    {
        var item = new QueueProjectItem
        {
            ProjectDir = GetString(payload, "project_dir"),
            DisplayName = GetString(payload, "display_name"),
            OriginalTitle = GetString(payload, "original_title"),
            NewTitle = GetString(payload, "new_title"),
            EpisodeCount = GetInt(payload, "episode_count"),
            VideoVertical = NormalizeVideoVertical(GetInt(payload, "video_vertical", -1)),
            GenreCategory = GetString(payload, "genre_category"),
            Description = GetString(payload, "description"),
            QueueEntryDramaType = GetString(payload, "queue_entry_drama_type"),
            AccountProfileId = GetString(payload, "account_profile_id"),
            AccountProfileName = GetString(payload, "account_profile_name"),
            QueuedAt = GetString(payload, "queued_at"),
            UploadCompletedAt = GetString(payload, "upload_completed_at"),
            ProofMaterialStatementDate = GetString(payload, "proof_material_statement_date"),
            Enabled = GetBool(payload, "enabled", true),
            CurrentStep = GetString(payload, "current_step"),
            StatusText = GetString(payload, "status_text", QueueStepStatus.Pending),
            LastError = GetString(payload, "last_error"),
            Remark = GetString(payload, "remark"),
            ManualUploadStatus = GetString(payload, "manual_upload_status"),
            Archived = GetBool(payload, "archived"),
            StepStates = GetStepStates(payload),
            PrimaryVideoPath = GetString(payload, "primary_video_path"),
            CoverPath = GetString(payload, "cover_path"),
        };
        item.NormalizeStepStates();
        return item;
    }

    private static Dictionary<string, string> GetStepStates(Dictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("step_states", out var raw) || raw is null)
            return new Dictionary<string, string>();
        if (raw is Dictionary<string, string> direct)
            return new Dictionary<string, string>(direct);
        if (raw is Dictionary<string, object?> objDict)
        {
            return objDict.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString() ?? QueueStepStatus.Pending,
                StringComparer.Ordinal);
        }
        return new Dictionary<string, string>();
    }

    private static string GetString(Dictionary<string, object?> payload, string key, string fallback = "")
        => payload.TryGetValue(key, out var value) ? (value?.ToString() ?? "").Trim() : fallback;

    private static int GetInt(Dictionary<string, object?> payload, string key, int fallback = 0)
    {
        if (!payload.TryGetValue(key, out var value) || value is null) return fallback;
        return int.TryParse(value.ToString(), out var n) ? n : fallback;
    }

    private static bool GetBool(Dictionary<string, object?> payload, string key, bool fallback = false)
    {
        if (!payload.TryGetValue(key, out var value) || value is null) return fallback;
        return value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            _ => fallback,
        };
    }

    private static int NormalizeVideoVertical(int value) => value is 0 or 1 ? value : -1;

    private static double GetDouble(Dictionary<string, object?> payload, string key, double fallback = 0)
    {
        if (!payload.TryGetValue(key, out var value) || value is null) return fallback;
        return double.TryParse(
            value.ToString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var number)
            ? number
            : fallback;
    }
}

public sealed class WorkspaceQueueState
{
    public List<QueueProjectItem> Items { get; set; } = new();
    public Dictionary<string, object?> Options { get; set; } = new();
}
