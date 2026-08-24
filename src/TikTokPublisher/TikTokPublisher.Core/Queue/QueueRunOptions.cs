namespace TikTokPublisher.Core.Queue;

public static class QueueStepRegistry
{
    public const string UploadSeries = QueueStepKeys.UploadSeries;
    public const string MaterialValidate = QueueStepKeys.MaterialValidate;
    public const string Download = QueueStepKeys.Download;
    public const string RewriteInfo = QueueStepKeys.RewriteInfo;
    public const string GeneratePoster = QueueStepKeys.GeneratePoster;
    public const string GenerateEpisodeScript = QueueStepKeys.GenerateEpisodeScript;
    public const string GenerateAiScriptOutline = QueueStepKeys.GenerateAiScriptOutline;
    public const string GenerateAiDramaMaterials = QueueStepKeys.GenerateAiDramaMaterials;
    public const string GenerateRoleVector = QueueStepKeys.GenerateRoleVector;
    public const string GenerateProjectImages = QueueStepKeys.GenerateProjectImages;
    public const string GenerateProofMaterial = QueueStepKeys.GenerateProofMaterial;
    public const string GenerateTimestampCertificate = QueueStepKeys.GenerateTimestampCertificate;
    public const string DeleteSourceVideos = QueueStepKeys.DeleteSourceVideos;

    public const string SmallVideoRepair = QueueStepKeys.SmallVideoRepair;
    public const string VideoTranslate = QueueStepKeys.VideoTranslate;

    /// <summary>与 Python <c>STEP_ORDER</c> 一致。</summary>
    public static IReadOnlyList<QueueStepDefinition> All { get; } = new[]
    {
        new QueueStepDefinition(QueueStepKeys.Download, "下载剧集", true),
        new QueueStepDefinition(QueueStepKeys.RewriteInfo, "改写信息", true),
        new QueueStepDefinition(QueueStepKeys.GeneratePoster, "生成海报", true),
        new QueueStepDefinition(QueueStepKeys.GenerateEpisodeScript, "生成剧本", true),
        new QueueStepDefinition(QueueStepKeys.GenerateAiDramaMaterials, "生成AI漫剧素材", true),
        new QueueStepDefinition(QueueStepKeys.GenerateAiScriptOutline, "生成AI大纲", true),
        new QueueStepDefinition(QueueStepKeys.GenerateRoleVector, "生成角色矢量图", true),
        new QueueStepDefinition(QueueStepKeys.GenerateProjectImages, "生成工程图", true),
        new QueueStepDefinition(QueueStepKeys.GenerateProofMaterial, "生成证明材料", true),
        new QueueStepDefinition(QueueStepKeys.GenerateTimestampCertificate, "生成时间戳", true),
        new QueueStepDefinition(SmallVideoRepair, "小文件修复", true),
        new QueueStepDefinition(VideoTranslate, "视频翻译", true),
        new QueueStepDefinition(MaterialValidate, "素材校验", true),
        new QueueStepDefinition(QueueStepKeys.DeleteSourceVideos, "删除源视频", true),
        new QueueStepDefinition(UploadSeries, "上传剧集", true),
    };

    public static IReadOnlyList<QueueStepDefinition> UserSelectable { get; } =
        All.Where(step =>
                step.Key != GenerateProjectImages &&
                IsAvailable(step.Key))
            .ToArray();

    public static IReadOnlyList<string> DefaultEnabledSteps { get; } = Array.Empty<string>();

    public static string LabelOf(string stepKey) =>
        All.FirstOrDefault(s => s.Key == stepKey).Label ?? stepKey;

    public static bool IsImplemented(string stepKey) =>
        All.FirstOrDefault(s => s.Key == stepKey).Implemented;

    // Video translation is developed on the dedicated feature branch. Keep the
    // persisted step key readable on main, but never expose or execute it here.
    public static bool IsAvailable(string stepKey) =>
        !string.Equals(stepKey, VideoTranslate, StringComparison.Ordinal);

    public static IEnumerable<string> OrderEnabledSteps(IEnumerable<string> enabledSteps) =>
        All.Select(s => s.Key).Where(key => IsAvailable(key) && enabledSteps.Contains(key));

    public static IEnumerable<string> OrderUserSelectableSteps(IEnumerable<string> enabledSteps) =>
        UserSelectable.Select(s => s.Key).Where(enabledSteps.Contains);
}

public readonly record struct QueueStepDefinition(string Key, string Label, bool Implemented);

public enum CopyrightProofExecutionMode
{
    GenerateMaterialOnly,
    GenerateAndEdit,
}

public sealed class QueueRunOptions
{
    public const string EditUploadEntryMode = "edit";
    public const string CopyrightProofOnlyEntryMode = "copyright_proof_only";
    public const string CopyrightProofMaterialOnlyEntryMode = "copyright_proof_material_only";
    public const string AiOutlineSupplementEntryMode = "ai_outline_supplement";

    public List<string> EnabledSteps { get; set; } = QueueStepRegistry.DefaultEnabledSteps.ToList();
    public bool AutoArchiveAfterUpload { get; set; }
    public bool ForceRerunCompletedSteps { get; set; }
    public bool PreferUploadWhenReady { get; set; }
    public bool SyncManagementAfterUpload { get; set; }
    public int ProjectConcurrency { get; set; } = 4;
    public string UploadEntryMode { get; set; } = "";

    public bool IsStepEnabled(string stepKey)
    {
        return QueueStepRegistry.IsAvailable(stepKey) &&
               EnabledSteps.Contains(stepKey, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> OrderedEnabledSteps()
    {
        return QueueStepRegistry.OrderEnabledSteps(EnabledSteps).ToList();
    }

    public QueueRunOptions Clone() => new()
    {
        EnabledSteps = EnabledSteps.ToList(),
        AutoArchiveAfterUpload = AutoArchiveAfterUpload,
        ForceRerunCompletedSteps = ForceRerunCompletedSteps,
        PreferUploadWhenReady = PreferUploadWhenReady,
        SyncManagementAfterUpload = SyncManagementAfterUpload,
        ProjectConcurrency = ProjectConcurrency,
        UploadEntryMode = UploadEntryMode,
    };

    public QueueRunOptions ClonePersistent()
    {
        var clone = Clone();
        clone.ClearTransientRunState();
        return clone;
    }

    public void ClearTransientRunState()
    {
        ForceRerunCompletedSteps = false;
        UploadEntryMode = "";
    }

    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["enabled_steps"] = QueueStepRegistry.OrderEnabledSteps(EnabledSteps).ToList(),
        ["auto_archive_after_upload"] = AutoArchiveAfterUpload,
        ["force_rerun_completed_steps"] = ForceRerunCompletedSteps,
        ["prefer_upload_when_ready"] = PreferUploadWhenReady,
        ["sync_management_after_upload"] = SyncManagementAfterUpload,
        ["project_concurrency"] = Math.Clamp(ProjectConcurrency, 1, 20),
        ["upload_entry_mode"] = NormalizeUploadEntryMode(UploadEntryMode),
    };

    public Dictionary<string, object?> ToPersistentDictionary() =>
        ClonePersistent().ToDictionary();

    public static QueueRunOptions FromDictionary(Dictionary<string, object?>? payload)
    {
        payload ??= new Dictionary<string, object?>();
        var enabled = new List<string>();
        var hasEnabledSteps = payload.TryGetValue("enabled_steps", out var raw);
        if (hasEnabledSteps && raw is IEnumerable<object?> list)
        {
            foreach (var item in list)
            {
                var key = (item?.ToString() ?? "").Trim();
                if (!string.IsNullOrEmpty(key) &&
                    QueueStepRegistry.IsAvailable(key) &&
                    QueueStepRegistry.All.Any(s => s.Key == key))
                    enabled.Add(key);
            }
        }
        if (!hasEnabledSteps)
            enabled = QueueStepRegistry.DefaultEnabledSteps.ToList();

        return new QueueRunOptions
        {
            EnabledSteps = enabled,
            AutoArchiveAfterUpload = GetBool(payload, "auto_archive_after_upload"),
            ForceRerunCompletedSteps = GetBool(payload, "force_rerun_completed_steps"),
            PreferUploadWhenReady = GetBool(payload, "prefer_upload_when_ready"),
            SyncManagementAfterUpload =
                GetBool(payload, "sync_management_after_upload") ||
                GetBool(payload, "sync_management_on_upload_success"),
            ProjectConcurrency = Math.Clamp(GetInt(payload, "project_concurrency", 4), 1, 20),
            UploadEntryMode = NormalizeUploadEntryMode(GetString(payload, "upload_entry_mode")),
        };
    }

    private static string NormalizeUploadEntryMode(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        if (normalized is "edit" or "edit_existing" or "existing")
            return EditUploadEntryMode;
        if (normalized is "copyright_proof_only" or "proof_only")
            return CopyrightProofOnlyEntryMode;
        if (normalized is "ai_outline_supplement" or "outline_supplement")
            return AiOutlineSupplementEntryMode;
        return normalized is "copyright_proof_material_only" or "proof_material_only"
            ? CopyrightProofMaterialOnlyEntryMode
            : string.Empty;
    }

    public bool IsCopyrightProofOnlyRun() =>
        string.Equals(UploadEntryMode, CopyrightProofOnlyEntryMode, StringComparison.OrdinalIgnoreCase) ||
        IsAiOutlineSupplementRun();

    public bool IsAiOutlineSupplementRun() =>
        string.Equals(UploadEntryMode, AiOutlineSupplementEntryMode, StringComparison.OrdinalIgnoreCase);

    public bool IsCopyrightProofMaterialOnlyRun() =>
        string.Equals(
            UploadEntryMode,
            CopyrightProofMaterialOnlyEntryMode,
            StringComparison.OrdinalIgnoreCase);

    public bool IsCopyrightProofWorkflowRun() =>
        IsCopyrightProofOnlyRun() || IsCopyrightProofMaterialOnlyRun();

    public void ConfigureForCopyrightProofCompletion()
    {
        ConfigureForCopyrightProof(CopyrightProofExecutionMode.GenerateAndEdit);
    }

    public void ConfigureForCopyrightProof(CopyrightProofExecutionMode executionMode)
    {
        EnabledSteps = executionMode == CopyrightProofExecutionMode.GenerateAndEdit
            ?
            [
                QueueStepRegistry.GenerateProofMaterial,
                QueueStepRegistry.UploadSeries,
            ]
            :
            [
                QueueStepRegistry.GenerateProofMaterial,
            ];
        ForceRerunCompletedSteps = false;
        AutoArchiveAfterUpload = false;
        SyncManagementAfterUpload = false;
        UploadEntryMode = executionMode == CopyrightProofExecutionMode.GenerateAndEdit
            ? CopyrightProofOnlyEntryMode
            : CopyrightProofMaterialOnlyEntryMode;
    }

    private static bool GetBool(Dictionary<string, object?> payload, string key) =>
        payload.TryGetValue(key, out var v) && v switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            _ => false,
        };

    private static int GetInt(Dictionary<string, object?> payload, string key, int fallback)
    {
        if (!payload.TryGetValue(key, out var v) || v is null) return fallback;
        return int.TryParse(v.ToString(), out var n) ? n : fallback;
    }

    private static string GetString(Dictionary<string, object?> payload, string key, string fallback = "")
        => payload.TryGetValue(key, out var value) ? (value?.ToString() ?? "").Trim() : fallback;
}
