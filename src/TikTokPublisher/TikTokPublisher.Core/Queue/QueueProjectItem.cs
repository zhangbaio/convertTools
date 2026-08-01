namespace TikTokPublisher.Core.Queue;

public static class QueueStepKeys
{
    public const string Download = "download";
    public const string DetectLiveAction = "detect_live_action";
    public const string RewriteInfo = "rewrite_info";
    public const string GeneratePoster = "generate_poster";
    public const string GenerateProjectImages = "generate_project_images";
    public const string GenerateProofMaterial = "generate_proof_material";
    public const string SmallVideoRepair = "small_video_repair";
    public const string VideoTranslate = "video_translate";
    public const string SilenceDetect = "silence_detect";
    public const string SilenceRepair = "silence_repair";
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
    public const string Excluded = "真人剧已拦截";
    public const string Skipped = "已跳过";
}

public sealed class QueueProjectItem
{
    public string ProjectDir { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public string NewTitle { get; set; } = "";
    public int EpisodeCount { get; set; }
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
    public string LiveActionClassification { get; set; } = "";
    public double LiveActionConfidence { get; set; }
    public string LiveActionDetectionReason { get; set; } = "";
    public string LiveActionDetectedAt { get; set; } = "";
    public string LiveActionVideoFingerprint { get; set; } = "";
    public bool PipelineExcluded { get; set; }
    public Dictionary<string, string> StepStates { get; set; } = new();
    public bool Archived { get; set; }

    public string? PrimaryVideoPath { get; set; }
    public string? CoverPath { get; set; }

    public string Title => !string.IsNullOrWhiteSpace(NewTitle) ? NewTitle : OriginalTitle;

    public string UploadSeriesStatus =>
        StepStates.TryGetValue(QueueStepKeys.UploadSeries, out var status) ? status : QueueStepStatus.Pending;

    public bool IsUploadCompleted => UploadSeriesStatus == QueueStepStatus.Completed;

    public bool IsPendingUpload => Enabled && !Archived && !PipelineExcluded && !IsUploadCompleted;

    public void NormalizeStepStates()
    {
        // An explicit pending proof-material state must survive normalization (for example,
        // after a title rename). Only legacy uploaded records that predate this step are
        // backfilled as completed.
        var hadProofMaterialState = StepStates.ContainsKey(QueueStepKeys.GenerateProofMaterial);

        foreach (var key in new[]
                 {
                     QueueStepKeys.Download,
                     QueueStepKeys.DetectLiveAction,
                     QueueStepKeys.RewriteInfo,
                     QueueStepKeys.GeneratePoster,
                     QueueStepKeys.GenerateProjectImages,
                     QueueStepKeys.GenerateProofMaterial,
                     QueueStepKeys.DeleteSourceVideos,
                     QueueStepKeys.UploadSeries,
                     QueueStepKeys.MaterialValidate,
                     QueueStepKeys.SmallVideoRepair,
                     QueueStepKeys.VideoTranslate,
                     QueueStepKeys.SilenceDetect,
                     QueueStepKeys.SilenceRepair,
                 })
            StepStates.TryAdd(key, QueueStepStatus.Pending);

        if (StepStates.GetValueOrDefault(QueueStepKeys.UploadSeries) == QueueStepStatus.Completed)
        {
            if (!hadProofMaterialState)
                StepStates[QueueStepKeys.GenerateProofMaterial] = QueueStepStatus.Completed;
            if (StepStates.GetValueOrDefault(QueueStepKeys.MaterialValidate) == QueueStepStatus.Pending)
                StepStates[QueueStepKeys.MaterialValidate] = QueueStepStatus.Completed;
            if (StepStates.GetValueOrDefault(QueueStepKeys.GenerateProjectImages) == QueueStepStatus.Pending)
                StepStates[QueueStepKeys.GenerateProjectImages] = QueueStepStatus.Completed;
            if (StepStates.GetValueOrDefault(QueueStepKeys.SmallVideoRepair) == QueueStepStatus.Pending)
                StepStates[QueueStepKeys.SmallVideoRepair] = QueueStepStatus.Completed;
            if (StepStates.GetValueOrDefault(QueueStepKeys.VideoTranslate) == QueueStepStatus.Pending)
                StepStates[QueueStepKeys.VideoTranslate] = QueueStepStatus.Completed;
            if (StepStates.GetValueOrDefault(QueueStepKeys.SilenceDetect) == QueueStepStatus.Pending)
                StepStates[QueueStepKeys.SilenceDetect] = QueueStepStatus.Completed;
            if (StepStates.GetValueOrDefault(QueueStepKeys.SilenceRepair) == QueueStepStatus.Pending)
                StepStates[QueueStepKeys.SilenceRepair] = QueueStepStatus.Completed;
        }
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
            ["live_action_classification"] = LiveActionClassification,
            ["live_action_confidence"] = LiveActionConfidence,
            ["live_action_detection_reason"] = LiveActionDetectionReason,
            ["live_action_detected_at"] = LiveActionDetectedAt,
            ["live_action_video_fingerprint"] = LiveActionVideoFingerprint,
            ["pipeline_excluded"] = PipelineExcluded,
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
            LiveActionClassification = GetString(payload, "live_action_classification"),
            LiveActionConfidence = GetDouble(payload, "live_action_confidence"),
            LiveActionDetectionReason = GetString(payload, "live_action_detection_reason"),
            LiveActionDetectedAt = GetString(payload, "live_action_detected_at"),
            LiveActionVideoFingerprint = GetString(payload, "live_action_video_fingerprint"),
            PipelineExcluded = GetBool(payload, "pipeline_excluded"),
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
