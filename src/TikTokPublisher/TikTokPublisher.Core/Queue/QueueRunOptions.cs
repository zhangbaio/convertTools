namespace TikTokPublisher.Core.Queue;

public static class QueueStepRegistry
{
    public const string UploadSeries = QueueStepKeys.UploadSeries;
    public const string MaterialValidate = QueueStepKeys.MaterialValidate;
    public const string Download = QueueStepKeys.Download;
    public const string RewriteInfo = QueueStepKeys.RewriteInfo;
    public const string GeneratePoster = QueueStepKeys.GeneratePoster;
    public const string GenerateProjectImages = QueueStepKeys.GenerateProjectImages;
    public const string GenerateProofMaterial = QueueStepKeys.GenerateProofMaterial;
    public const string DeleteSourceVideos = QueueStepKeys.DeleteSourceVideos;

    public const string SmallVideoRepair = QueueStepKeys.SmallVideoRepair;
    public const string SilenceDetect = QueueStepKeys.SilenceDetect;
    public const string SilenceRepair = QueueStepKeys.SilenceRepair;

    /// <summary>与 Python <c>STEP_ORDER</c> 一致。</summary>
    public static IReadOnlyList<QueueStepDefinition> All { get; } = new[]
    {
        new QueueStepDefinition(QueueStepKeys.Download, "下载剧集", true),
        new QueueStepDefinition(QueueStepKeys.RewriteInfo, "改写信息", true),
        new QueueStepDefinition(QueueStepKeys.GeneratePoster, "生成海报", true),
        new QueueStepDefinition(QueueStepKeys.GenerateProjectImages, "生成工程图", true),
        new QueueStepDefinition(QueueStepKeys.GenerateProofMaterial, "生成证明材料", true),
        new QueueStepDefinition(SmallVideoRepair, "小文件修复", true),
        new QueueStepDefinition(SilenceDetect, "静音检测", true),
        new QueueStepDefinition(SilenceRepair, "静音修复", true),
        new QueueStepDefinition(MaterialValidate, "素材校验", true),
        new QueueStepDefinition(QueueStepKeys.DeleteSourceVideos, "删除源视频", true),
        new QueueStepDefinition(UploadSeries, "上传剧集", true),
    };

    public static IReadOnlyList<QueueStepDefinition> UserSelectable { get; } =
        All.Where(step => step.Key != GenerateProjectImages).ToArray();

    public static IReadOnlyList<string> DefaultEnabledSteps { get; } = new[] { UploadSeries };

    public static string LabelOf(string stepKey) =>
        All.FirstOrDefault(s => s.Key == stepKey).Label ?? stepKey;

    public static bool IsImplemented(string stepKey) =>
        All.FirstOrDefault(s => s.Key == stepKey).Implemented;

    public static IEnumerable<string> OrderEnabledSteps(IEnumerable<string> enabledSteps) =>
        All.Select(s => s.Key).Where(enabledSteps.Contains);

    public static IEnumerable<string> OrderUserSelectableSteps(IEnumerable<string> enabledSteps) =>
        UserSelectable.Select(s => s.Key).Where(enabledSteps.Contains);
}

public readonly record struct QueueStepDefinition(string Key, string Label, bool Implemented);

public sealed class QueueRunOptions
{
    public List<string> EnabledSteps { get; set; } = QueueStepRegistry.DefaultEnabledSteps.ToList();
    public bool AutoArchiveAfterUpload { get; set; }
    public bool ForceRerunCompletedSteps { get; set; }
    public bool PreferUploadWhenReady { get; set; }
    public bool SyncManagementAfterUpload { get; set; }
    public int ProjectConcurrency { get; set; } = 4;
    public string UploadEntryMode { get; set; } = "";

    public bool IsStepEnabled(string stepKey) =>
        EnabledSteps.Contains(stepKey, StringComparer.Ordinal);

    public IReadOnlyList<string> OrderedEnabledSteps() =>
        QueueStepRegistry.OrderEnabledSteps(EnabledSteps).ToList();

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
        ["enabled_steps"] = EnabledSteps.ToList(),
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
                if (!string.IsNullOrEmpty(key) && QueueStepRegistry.All.Any(s => s.Key == key))
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
        return normalized is "edit" or "edit_existing" or "existing" ? "edit" : "";
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
