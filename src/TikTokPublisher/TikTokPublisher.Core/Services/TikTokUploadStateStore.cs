using System.Text.Json;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

/// <summary>读写项目 <c>tiktok-upload-state.json</c>（对齐 Python <c>upload_state_service.py</c> 子集）。</summary>
public static class TikTokUploadStateStore
{
    public const string StateFileName = "tiktok-upload-state.json";
    private const string CopyrightProofCompletedAtKey = "copyright_proof_completed_at";
    private const string CopyrightProofCompletedAccountIdKey = "copyright_proof_completed_account_id";
    private const string CopyrightProofStartedAtKey = "copyright_proof_started_at";
    private static readonly Regex SeriesIdPattern = new(@"/series/draft/(\d{16,20})", RegexOptions.Compiled);

    public static string StateFilePath(string workflowProjectDir) =>
        Path.Combine(Path.GetFullPath(workflowProjectDir), StateFileName);

    public static Dictionary<string, JsonElement> LoadState(string workflowProjectDir)
    {
        try
        {
            var context = ProjectWorkspaceService.LoadContext(workflowProjectDir);
            var databaseState = ProjectStateDocumentStore.LoadUploadState(
                context.WorkspaceRoot,
                context.SourceProjectDir);
            if (databaseState.Count > 0)
                return databaseState;
        }
        catch
        {
            // Fall back to the legacy json file below.
        }

        var path = StateFilePath(workflowProjectDir);
        if (!File.Exists(path)) return new Dictionary<string, JsonElement>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, JsonElement>();
            return doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    public static void SaveState(string workflowProjectDir, Dictionary<string, object?> state)
    {
        var path = StateFilePath(workflowProjectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

        try
        {
            var context = ProjectWorkspaceService.LoadContext(workflowProjectDir);
            ProjectStateDocumentStore.SaveUploadState(
                context.WorkspaceRoot,
                context.SourceProjectDir,
                state,
                context.WorkflowProjectDir);
            return;
        }
        catch
        {
            // Fall back to locating an existing workspace database.
        }

        var projectDir = Path.GetFullPath(workflowProjectDir);
        var workspaceRoot = FindWorkspaceRoot(projectDir);
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
            ProjectStateDocumentStore.SaveUploadState(workspaceRoot, projectDir, state, workflowProjectDir);
    }

    private static string FindWorkspaceRoot(string projectDir)
    {
        var current = new DirectoryInfo(Path.GetFullPath(projectDir));
        while (current is not null)
        {
            var dbPath = Path.Combine(current.FullName, WorkspaceQueuePaths.QueueDatabaseFileName);
            if (File.Exists(dbPath))
                return current.FullName;
            current = current.Parent;
        }

        return "";
    }

    public static bool HasUploadStepAttempted(string workflowProjectDir)
    {
        var state = LoadState(workflowProjectDir);
        return state.TryGetValue("upload_step_attempted", out var value) &&
               value.ValueKind is JsonValueKind.True;
    }

    /// <summary>
    /// 是否应在发布前搜索平台已有草稿。仅在上传曾真正进入表单、或已有平台草稿缓存时搜索；
    /// 避免「浏览器未打开就失败」的脏状态反复触发无效搜索。
    /// </summary>
    public static bool ShouldSearchPlatformForExistingDraft(string workflowProjectDir)
    {
        if (!string.IsNullOrWhiteSpace(LoadCachedEditDetailUrl(workflowProjectDir)))
            return true;

        var state = LoadState(workflowProjectDir);
        if (state.TryGetValue("platform_series_lookup", out var lookup) &&
            lookup.ValueKind == JsonValueKind.Object &&
            lookup.TryGetProperty("status", out var statusEl))
        {
            var status = statusEl.GetString() ?? "";
            if (string.Equals(status, "found", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(status, "not_found", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return state.TryGetValue("last_upload_step_started_at", out var started) &&
               started.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(started.GetString());
    }

    public static string LoadCachedEditDetailUrl(string workflowProjectDir)
    {
        var state = LoadState(workflowProjectDir);
        if (!state.TryGetValue("platform_series_lookup", out var lookup) ||
            lookup.ValueKind != JsonValueKind.Object)
            return "";

        var status = lookup.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : "";
        if (!string.Equals(status, "found", StringComparison.OrdinalIgnoreCase))
            return "";

        return lookup.TryGetProperty("detail_url", out var urlEl) ? (urlEl.GetString() ?? "").Trim() : "";
    }

    public static bool HasCopyrightProofCompleted(
        string projectDir,
        string? accountProfileId = null)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
            return false;

        try
        {
            var workflowProjectDir = ResolveWorkflowProjectDir(projectDir);
            var state = LoadState(workflowProjectDir);
            if (!state.TryGetValue(CopyrightProofCompletedAtKey, out var value) ||
                string.IsNullOrWhiteSpace(value.ToString()))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(accountProfileId))
                return true;

            return state.TryGetValue(CopyrightProofCompletedAccountIdKey, out var accountValue) &&
                   string.Equals(
                       accountValue.ToString(),
                       accountProfileId.Trim(),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void MarkCopyrightProofStepStarted(string projectDir)
    {
        var workflowProjectDir = ResolveWorkflowProjectDir(projectDir);
        var state = LoadStateObject(workflowProjectDir);
        state[CopyrightProofStartedAtKey] = NowText();
        state.Remove(CopyrightProofCompletedAtKey);
        state.Remove(CopyrightProofCompletedAccountIdKey);
        SaveState(workflowProjectDir, state);
    }

    public static void MarkCopyrightProofStepCompleted(
        string projectDir,
        string? accountProfileId = null)
    {
        var workflowProjectDir = ResolveWorkflowProjectDir(projectDir);
        var state = LoadStateObject(workflowProjectDir);
        state[CopyrightProofCompletedAtKey] = NowText();
        if (!string.IsNullOrWhiteSpace(accountProfileId))
            state[CopyrightProofCompletedAccountIdKey] = accountProfileId.Trim();
        else
            state.Remove(CopyrightProofCompletedAccountIdKey);
        state.Remove(CopyrightProofStartedAtKey);
        SaveState(workflowProjectDir, state);
    }

    /// <summary>
    /// Recovers a confirmed draft detail URL from failure snapshots. A create flow can acquire
    /// its series id before failing, so the snapshot may be the only durable copy of that id.
    /// </summary>
    public static string RecoverEditDetailUrlFromFailureSnapshots(string workflowProjectDir)
    {
        var snapshotRoot = Path.Combine(Path.GetFullPath(workflowProjectDir), "upload-failure-snapshots");
        if (!Directory.Exists(snapshotRoot)) return "";

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(snapshotRoot)
                         .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
            {
                var metadataPath = Path.Combine(directory, "metadata.json");
                if (!File.Exists(metadataPath)) continue;

                try
                {
                    using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
                    if (metadata.RootElement.ValueKind != JsonValueKind.Object ||
                        !metadata.RootElement.TryGetProperty("url", out var urlElement))
                        continue;

                    var normalized = NormalizeSeriesDraftDetailUrl(urlElement.GetString());
                    if (!string.IsNullOrWhiteSpace(normalized))
                        return normalized;
                }
                catch
                {
                    // A partially written snapshot must not prevent checking older snapshots.
                }
            }
        }
        catch
        {
            return "";
        }

        return "";
    }

    public static bool TryRecordPlatformSeriesFromUrl(
        string workflowProjectDir,
        string? currentUrl,
        string? matchedTitle,
        string source)
    {
        var detailUrl = NormalizeSeriesDraftDetailUrl(currentUrl);
        if (string.IsNullOrWhiteSpace(detailUrl)) return false;

        RecordPlatformSeriesFound(
            workflowProjectDir,
            detailUrl,
            matchedTitle ?? "",
            source);
        return true;
    }

    public static void RecordPlatformSeriesFound(
        string workflowProjectDir,
        string detailUrl,
        string matchedTitle,
        string source,
        IReadOnlyList<string>? searchedTitles = null)
    {
        var state = LoadStateObject(workflowProjectDir);
        state["platform_series_lookup"] = new Dictionary<string, object?>
        {
            ["status"] = "found",
            ["detail_url"] = detailUrl.Trim(),
            ["series_id"] = ExtractSeriesId(detailUrl),
            ["matched_title"] = matchedTitle.Trim(),
            ["source"] = string.IsNullOrWhiteSpace(source) ? "search" : source.Trim(),
            ["searched_titles"] = searchedTitles?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList() ?? new List<string>(),
            ["searched_at"] = DateTimeOffset.Now.ToString("o"),
        };
        SaveState(workflowProjectDir, state);
    }

    public static void RecordPlatformSeriesNotFound(
        string workflowProjectDir,
        string source,
        IReadOnlyList<string>? searchedTitles = null)
    {
        var state = LoadStateObject(workflowProjectDir);
        if (state.TryGetValue("platform_series_lookup", out var existing) &&
            existing is Dictionary<string, object?> dict &&
            string.Equals(dict.GetValueOrDefault("status")?.ToString(), "found", StringComparison.OrdinalIgnoreCase))
            return;

        state["platform_series_lookup"] = new Dictionary<string, object?>
        {
            ["status"] = "not_found",
            ["detail_url"] = "",
            ["series_id"] = "",
            ["matched_title"] = "",
            ["source"] = string.IsNullOrWhiteSpace(source) ? "search" : source.Trim(),
            ["searched_titles"] = searchedTitles?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList() ?? new List<string>(),
            ["searched_at"] = DateTimeOffset.Now.ToString("o"),
        };
        SaveState(workflowProjectDir, state);
    }

    public static void MarkUploadStepAttempted(string workflowProjectDir, string? title = null) =>
        MarkUploadStepStarted(workflowProjectDir, title);

    public static void MarkUploadStepStarted(string workflowProjectDir, string? title = null)
    {
        var state = LoadStateObject(workflowProjectDir);
        var attemptCount = state.TryGetValue("upload_step_attempt_count", out var countRaw)
            ? Math.Max(0, Convert.ToInt32(countRaw ?? 0))
            : 0;
        state["upload_step_attempted"] = true;
        state["upload_step_attempt_count"] = attemptCount + 1;
        state["last_upload_step_started_at"] = NowText();
        state.Remove("last_upload_completed_at");
        state.Remove("last_upload_step_failed_at");
        state.Remove("last_upload_step_error");
        state.Remove("last_upload_failure_snapshot_dir");
        if (!string.IsNullOrWhiteSpace(title))
            state["last_upload_title"] = title.Trim();
        SaveState(workflowProjectDir, state);
    }

    public static void MarkUploadStepCompleted(string workflowProjectDir, string? title = null)
    {
        var state = LoadStateObject(workflowProjectDir);
        state["upload_step_attempted"] = true;
        state["last_upload_completed_at"] = NowText();
        state.Remove("last_upload_step_failed_at");
        state.Remove("last_upload_step_error");
        state.Remove("last_upload_failure_snapshot_dir");
        if (!string.IsNullOrWhiteSpace(title))
            state["last_upload_title"] = title.Trim();
        SaveState(workflowProjectDir, state);
    }

    public static void MarkUploadStepFailed(
        string workflowProjectDir,
        string error,
        string? title = null,
        string? failureSnapshotDir = null)
    {
        var state = LoadStateObject(workflowProjectDir);
        state["upload_step_attempted"] = true;
        state.Remove("last_upload_completed_at");
        state["last_upload_step_failed_at"] = NowText();
        state["last_upload_step_error"] = (error ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(failureSnapshotDir))
            state["last_upload_failure_snapshot_dir"] = Path.GetFullPath(failureSnapshotDir);
        else
            state.Remove("last_upload_failure_snapshot_dir");
        if (!string.IsNullOrWhiteSpace(title))
            state["last_upload_title"] = title.Trim();
        SaveState(workflowProjectDir, state);
    }

    public static string ResolveWorkflowProjectDir(string? projectDir) =>
        ProjectWorkspaceService.ResolveWorkflowProjectDir(projectDir);

    private static Dictionary<string, object?> LoadStateObject(string workflowProjectDir)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in LoadState(workflowProjectDir))
            result[key] = JsonElementToObject(value);
        return result;
    }

    private static string ExtractSeriesId(string detailUrl)
    {
        var match = SeriesIdPattern.Match(detailUrl ?? "");
        return match.Success ? match.Groups[1].Value : "";
    }

    internal static string NormalizeSeriesDraftDetailUrl(string? candidate)
    {
        var text = (candidate ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "www.tiktokdramacenter.com", StringComparison.OrdinalIgnoreCase))
            return "";

        var match = SeriesIdPattern.Match(uri.AbsolutePath);
        if (!match.Success) return "";
        return $"https://www.tiktokdramacenter.com/series/draft/{match.Groups[1].Value}";
    }

    private static string NowText() => DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss");

    private static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
        _ => null,
    };
}
