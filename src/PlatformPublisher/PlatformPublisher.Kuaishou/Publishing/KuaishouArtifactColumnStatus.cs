using System.Text.Json;

namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 按短剧助手快手分账表格的口径，从工作目录产物判断每一列。
/// 个人版读 .kuaishou-personal-* 与 kuaishou-auto-fill.json，
/// 企业版读 .kuaishou-enterprise-* 与 kuaishou-enterprise-auto-fill.json。
/// </summary>
public sealed record KuaishouArtifactColumnStatus(
    string OriginalTitle,
    string NewTitle,
    int EpisodeCount,
    string DownloadStatus,
    string RewriteStatus,
    string PosterStatus,
    string ProjectImageStatus,
    string AutoFillStatus,
    string UploadStatus,
    string OverallStatus);

public static class KuaishouArtifactColumnStatusReader
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm",
    };

    private static readonly string[] ProjectImageSuffixes = [".png", ".jpg", ".jpeg", ".bmp"];

    public static (string SourceDirectory, string WorkflowDirectory) ResolveDirectories(string projectDirectory)
    {
        var projectDir = Path.GetFullPath(projectDirectory);
        var sourceDir = ResolveSourceDirectory(projectDir);
        return (sourceDir, ResolveWorkflowDirectory(projectDir, sourceDir));
    }

    public static KuaishouArtifactColumnStatus Read(string projectDirectory, bool enterprise)
    {
        var projectDir = Path.GetFullPath(projectDirectory);
        var sourceDir = ResolveSourceDirectory(projectDir);
        var workflowDir = ResolveWorkflowDirectory(projectDir, sourceDir);
        var sourceInfo = ReadInfo(Path.Combine(sourceDir, "短剧信息.txt"));
        var workflowInfo = ReadInfo(Path.Combine(workflowDir, "短剧信息.txt"));
        var sourceMetadata = JsonMap.Load(Path.Combine(sourceDir, "shortdrama-project.json"));
        var workflowMetadata = JsonMap.Load(Path.Combine(workflowDir, "shortdrama-project.json"));
        var prepareName = enterprise
            ? ".kuaishou-enterprise-prepare-state.json"
            : ".kuaishou-personal-prepare-state.json";
        var uploadName = enterprise
            ? ".kuaishou-enterprise-upload-state.json"
            : ".kuaishou-personal-upload-state.json";
        var autoFillName = enterprise ? "kuaishou-enterprise-auto-fill.json" : "kuaishou-auto-fill.json";
        var prepare = JsonMap.Load(Path.Combine(workflowDir, prepareName));
        var upload = NormalizeCompletedUpload(JsonMap.Load(Path.Combine(workflowDir, uploadName)));
        var download = JsonMap.Load(Path.Combine(sourceDir, ".weixin-channel-download-state.json"));

        var originalTitle = FirstText(
            sourceInfo.GetValueOrDefault("原剧名"),
            sourceInfo.GetValueOrDefault("原剧名称"),
            workflowInfo.GetValueOrDefault("原剧名"),
            workflowInfo.GetValueOrDefault("原剧名称"),
            sourceMetadata.Str("originalTitle"),
            sourceMetadata.Str("sourceName"),
            workflowMetadata.Str("originalTitle"),
            workflowMetadata.Str("sourceName"),
            Path.GetFileName(sourceDir)) ?? Path.GetFileName(sourceDir);
        var newTitle = ResolveNewTitle(workflowInfo, sourceInfo, workflowMetadata, sourceMetadata, workflowDir, sourceDir, originalTitle);
        var downloadedCount = ResolveDownloadedCount(projectDir, workflowDir, sourceDir, prepare);
        var declaredCount = MaxInt(
            ParseLeadingInt(workflowInfo.GetValueOrDefault("集数")),
            ParseLeadingInt(workflowInfo.GetValueOrDefault("总集数")),
            ParseLeadingInt(sourceInfo.GetValueOrDefault("集数")),
            ParseLeadingInt(sourceInfo.GetValueOrDefault("总集数")),
            sourceMetadata.Int("episodeCount"),
            sourceMetadata.Int("episode_count"),
            sourceMetadata.Int("totalEpisodes"),
            sourceMetadata.Int("total_episodes"),
            workflowMetadata.Int("episodeCount"),
            workflowMetadata.Int("episode_count"),
            workflowMetadata.Int("totalEpisodes"),
            workflowMetadata.Int("total_episodes"));
        var episodeCount = MaxInt(
            downloadedCount,
            upload.Int("episode_count"),
            prepare.Int("episode_count"),
            declaredCount);
        var imageCount = CountProjectImages(workflowDir);
        var completed = CompletedSteps(prepare);
        var downloadStatus = DownloadStatusText(prepare, download, downloadedCount, completed);
        var rewriteStatus = BoolStatus(IsRewriteCompleted(completed, originalTitle, newTitle, sourceDir));
        var posterStatus = BoolStatus(IsPosterCompleted(prepare, workflowDir, completed));
        var projectImageStatus = ProjectImageStatusText(imageCount);
        var autoFillStatus = BoolStatus(IsAutoFillCompleted(prepare, workflowDir, autoFillName, completed));
        var uploadStatus = UploadStatusText(upload);
        var overallStatus = OverallStatusText(OverallState(prepare, upload), uploadStatus);

        return new KuaishouArtifactColumnStatus(
            originalTitle,
            newTitle,
            episodeCount,
            downloadStatus,
            rewriteStatus,
            posterStatus,
            projectImageStatus,
            autoFillStatus,
            uploadStatus,
            overallStatus);
    }

    private static string ResolveSourceDirectory(string projectDir)
    {
        if (!IsWorkflowDirectory(projectDir)) return projectDir;
        var metadata = JsonMap.Load(Path.Combine(projectDir, "shortdrama-project.json"));
        var source = metadata.Str("sourceProjectDir");
        if (!string.IsNullOrWhiteSpace(source) && Directory.Exists(source))
            return Path.GetFullPath(source);
        var workspace = Directory.GetParent(projectDir)?.Parent?.FullName;
        var stripped = Path.GetFileName(projectDir).TrimStart('_');
        if (!string.IsNullOrWhiteSpace(workspace) && !string.IsNullOrWhiteSpace(stripped))
        {
            var sibling = Path.Combine(workspace, stripped);
            if (Directory.Exists(sibling)) return sibling;
        }
        return projectDir;
    }

    private static string ResolveWorkflowDirectory(string projectDir, string sourceDir)
    {
        if (IsWorkflowDirectory(projectDir)) return projectDir;
        var metadata = JsonMap.Load(Path.Combine(sourceDir, "shortdrama-project.json"));
        var configured = metadata.Str("workflowProjectDir");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return Path.GetFullPath(configured);
        var parent = Directory.GetParent(sourceDir)?.FullName;
        if (string.IsNullOrWhiteSpace(parent)) return sourceDir;
        var workflowName = metadata.Str("workflowDirName");
        if (!string.IsNullOrWhiteSpace(workflowName))
        {
            var named = Path.Combine(parent, "workflow", workflowName);
            if (Directory.Exists(named)) return named;
        }
        var workflowRoot = Path.Combine(parent, "workflow");
        if (!Directory.Exists(workflowRoot)) return sourceDir;
        foreach (var candidate in Directory.EnumerateDirectories(workflowRoot))
        {
            var workflowMetadata = JsonMap.Load(Path.Combine(candidate, "shortdrama-project.json"));
            if (PathsEqual(workflowMetadata.Str("sourceProjectDir"), sourceDir)) return candidate;
        }
        return sourceDir;
    }

    private static bool IsWorkflowDirectory(string projectDir) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(projectDir)), "workflow", StringComparison.OrdinalIgnoreCase);

    private static int ResolveDownloadedCount(string projectDir, string workflowDir, string sourceDir, JsonMap prepare)
    {
        var recorded = prepare.Str("source_video_dir");
        foreach (var candidate in new[]
                 {
                     string.IsNullOrWhiteSpace(recorded) ? null : recorded,
                     Path.Combine(workflowDir, "videos"),
                     sourceDir,
                     projectDir,
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var count = CountVideos(candidate);
            if (count > 0) return count;
        }
        return prepare.Int("episode_count");
    }

    private static string DownloadStatusText(JsonMap prepare, JsonMap download, int downloadedCount, HashSet<string> completed)
    {
        var prepareStatus = prepare.Str("status").ToLowerInvariant();
        var failedStep = prepare.Str("failed_step");
        if (prepareStatus is "failed" or "error" && failedStep == "download") return "失败";
        if (download.Exists)
        {
            if (download.Bool("stopped")) return "已停止";
            if (download.IsFalse("ok")) return "失败";
            if (download.IsTrue("ok"))
            {
                var expected = download.ArrayLength("episode_mappings");
                return expected <= 0 || downloadedCount >= expected ? "已完成" : "失败";
            }
        }
        if (completed.Contains("download")) return "已完成";
        if (prepareStatus == "stopped" && failedStep is "" or "download") return "已停止";
        return BoolStatus(downloadedCount > 0);
    }

    private static bool IsRewriteCompleted(HashSet<string> completed, string originalTitle, string newTitle, string sourceDir)
    {
        if (!IsValidNewTitle(newTitle)) return false;
        if (completed.Contains("rewrite_info")) return true;
        return IsDistinctTitle(newTitle, originalTitle, Path.GetFileName(sourceDir));
    }

    private static bool IsPosterCompleted(JsonMap prepare, string workflowDir, HashSet<string> completed)
    {
        if (completed.Contains("generate_poster")) return true;
        return ExistingFile(prepare.Str("horizontal_cover_path"), Path.Combine(workflowDir, "快手横屏封面.jpg"))
               && ExistingFile(prepare.Str("vertical_poster_path"), Path.Combine(workflowDir, "快手竖屏海报.jpg"));
    }

    private static bool IsAutoFillCompleted(JsonMap prepare, string workflowDir, string fileName, HashSet<string> completed)
    {
        if (completed.Contains("auto_fill_info")) return true;
        var path = Path.Combine(workflowDir, fileName);
        if (!File.Exists(path)) return false;
        var payload = JsonMap.Load(path);
        return payload.Exists
               && !string.IsNullOrWhiteSpace(payload.Str("description"))
               && payload.HasValue("audience_gender")
               && payload.ArrayLength("plot_list") > 0
               && payload.ArrayLength("tag_list") > 0;
    }

    private static string ProjectImageStatusText(int count) =>
        count > 0 ? $"已完成 {count}图" : "未执行";

    private static string UploadStatusText(JsonMap upload)
    {
        var status = upload.Str("status").ToLowerInvariant();
        var uploaded = upload.Int("uploaded_episode_count");
        var total = upload.Int("episode_count");
        if (status is "success" or "completed") return "已完成";
        if (status is "failed" or "error") return HasUploadEvidence(upload) ? "失败" : "未执行";
        if (status == "stopped") return "已停止";
        if (status == "draft_saved") return "已保存草稿";
        if (status is "manual_hold" or "final_hold") return "已保持";
        if (status is "running" or "uploading" or "publishing")
            return total > 0 ? $"进行中 {uploaded}/{total}" : "进行中";
        return "未执行";
    }

    private static JsonMap OverallState(JsonMap prepare, JsonMap upload)
    {
        var uploadStatus = upload.Str("status").ToLowerInvariant();
        if (uploadStatus is "failed" or "error" && !HasUploadEvidence(upload)) uploadStatus = "";
        var prepareStatus = prepare.Str("status").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(uploadStatus)) return prepare;
        if (uploadStatus is "success" or "completed" or "failed" or "error" or "stopped" or "draft_saved" or "manual_hold" or "final_hold")
            return upload;
        if (prepareStatus is "failed" or "error" or "stopped") return prepare;
        return upload;
    }

    private static string OverallStatusText(JsonMap state, string uploadStatus)
    {
        var status = state.Str("status").ToLowerInvariant();
        if (status is "failed" or "error") return "失败";
        if (status is "running" or "uploading" or "publishing" or "preparing") return "进行中";
        if (status == "stopped") return "已停止";
        if (status == "draft_saved") return "已保存草稿";
        if (status is "manual_hold" or "final_hold") return "已保持";
        if (uploadStatus == "已完成") return "已完成";
        return "待执行";
    }

    private static bool HasUploadEvidence(JsonMap upload) =>
        upload.Bool("upload_attempted")
        || !string.IsNullOrWhiteSpace(upload.Str("current_stage"))
        || !string.IsNullOrWhiteSpace(upload.Str("failed_stage"))
        || upload.Int("episode_count") > 0
        || upload.Int("uploaded_episode_count") > 0
        || upload.HasObject("response_summary");

    private static JsonMap NormalizeCompletedUpload(JsonMap upload)
    {
        if (!upload.Exists) return upload;
        var status = upload.Str("status").ToLowerInvariant();
        if (status is "success" or "completed") return upload;
        var response = upload.Object("response_summary");
        if (!response.Exists) return upload;
        var responseStatus = response.Str("status").ToLowerInvariant();
        var uploaded = response.Int("uploaded_episode_count");
        var total = response.Int("episode_count");
        if (responseStatus is "success" or "completed" && total > 0 && uploaded >= total)
            return upload.WithStatus("success");
        return upload;
    }

    private static HashSet<string> CompletedSteps(JsonMap state)
    {
        var steps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!state.Exists || !state.TryGet("completed_prepare_steps", out var value)) return steps;
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
                if (!string.IsNullOrWhiteSpace(text)) steps.Add(text);
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(property.Name) && IsTruthy(property.Value))
                    steps.Add(property.Name.Trim());
            }
        }
        return steps;
    }

    private static string ResolveNewTitle(
        Dictionary<string, string> workflowInfo,
        Dictionary<string, string> sourceInfo,
        JsonMap workflowMetadata,
        JsonMap sourceMetadata,
        string workflowDir,
        string sourceDir,
        string originalTitle)
    {
        var sourceName = Path.GetFileName(sourceDir);
        var preferred = new[]
        {
            workflowInfo.GetValueOrDefault("新剧名"),
            workflowInfo.GetValueOrDefault("新剧名称"),
            workflowInfo.GetValueOrDefault("剧名"),
            workflowMetadata.Str("newTitle"),
            workflowMetadata.Str("displayName"),
            workflowMetadata.Str("title"),
            Path.GetFileName(workflowDir).TrimStart('_'),
            sourceInfo.GetValueOrDefault("新剧名"),
            sourceInfo.GetValueOrDefault("新剧名称"),
            sourceInfo.GetValueOrDefault("剧名"),
            sourceMetadata.Str("newTitle"),
            sourceMetadata.Str("displayName"),
            sourceMetadata.Str("title"),
        };
        foreach (var candidate in preferred)
        {
            var text = Clean(candidate);
            if (IsDistinctTitle(text, originalTitle, sourceName)) return text;
        }
        return FirstText(preferred.Append(originalTitle).Append(sourceName).ToArray()) ?? sourceName;
    }

    private static bool IsValidNewTitle(string title)
    {
        var text = title.Trim();
        return text.Length is >= 2 and <= 10;
    }

    private static bool IsDistinctTitle(string? title, string originalTitle, string sourceName)
    {
        var normalized = NormalizeTitle(title);
        if (normalized.Length == 0) return false;
        return !string.Equals(normalized, NormalizeTitle(originalTitle), StringComparison.OrdinalIgnoreCase)
               && !string.Equals(normalized, NormalizeTitle(sourceName), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTitle(string? value) =>
        string.Concat((value ?? "").Trim().TrimStart('_').Where(ch => !char.IsWhiteSpace(ch))).ToLowerInvariant();

    private static int CountProjectImages(string workflowDir)
    {
        if (!Directory.Exists(workflowDir)) return 0;
        var count = 0;
        for (var index = 1; index <= 4; index++)
        {
            if (ProjectImageSuffixes.Any(suffix => File.Exists(Path.Combine(workflowDir, $"工程图_{index}{suffix}"))))
                count++;
        }
        return count;
    }

    private static int CountVideos(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        try
        {
            return Directory.EnumerateFiles(directory)
                .Count(path =>
                {
                    var name = Path.GetFileName(path);
                    if (name.StartsWith('.')) return false;
                    var stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                    if (stem.EndsWith(".transcode") || stem.EndsWith(".pad") || stem.EndsWith(".dynamic-speed")) return false;
                    if (!VideoExtensions.Contains(Path.GetExtension(path))) return false;
                    try { return new FileInfo(path).Length > 0; }
                    catch { return false; }
                });
        }
        catch
        {
            return 0;
        }
    }

    private static bool ExistingFile(string? recorded, string fallback)
    {
        foreach (var candidate in new[] { recorded, fallback })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                if (File.Exists(candidate)) return true;
            }
            catch
            {
                // 产物路径无效时继续看下一个候选。
            }
        }
        return false;
    }

    private static Dictionary<string, string> ReadInfo(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return values;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var index = line.IndexOfAny([':', '：']);
                if (index <= 0) continue;
                var key = line[..index].Trim();
                var value = line[(index + 1)..].Trim();
                if (key.Length > 0 && value.Length > 0) values[key] = value;
            }
        }
        catch
        {
            return values;
        }
        return values;
    }

    private static string BoolStatus(bool ready) => ready ? "已完成" : "未执行";

    private static string? FirstText(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = Clean(value);
            if (text.Length > 0) return text;
        }
        return null;
    }

    private static string Clean(string? value) => (value ?? "").Trim().TrimStart('_').Trim();

    private static int ParseLeadingInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            var matchStart = value.TakeWhile(ch => !char.IsDigit(ch)).Count();
            digits = new string(value.Skip(matchStart).TakeWhile(char.IsDigit).ToArray());
        }
        return int.TryParse(digits, out var parsed) ? parsed : 0;
    }

    private static int MaxInt(params int[] values) => values.Length == 0 ? 0 : values.Max();

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool IsTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        _ => false,
    };

    private readonly record struct JsonMap(JsonElement Element, bool Exists)
    {
        public static JsonMap Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return default;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? new JsonMap(document.RootElement.Clone(), true)
                    : default;
            }
            catch
            {
                return default;
            }
        }

        public string Str(string name)
        {
            if (!TryGet(name, out var value)) return "";
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()?.Trim() ?? "",
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => "",
            };
        }

        public int Int(string name)
        {
            if (!TryGet(name, out var value)) return 0;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)) return parsed;
            return 0;
        }

        public bool Bool(string name) => TryGet(name, out var value) && value.ValueKind == JsonValueKind.True;

        public bool IsTrue(string name) => Bool(name);

        public bool IsFalse(string name) => TryGet(name, out var value) && value.ValueKind == JsonValueKind.False;

        public bool HasValue(string name) =>
            TryGet(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            && (value.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(value.GetString()));

        public bool HasObject(string name) => TryGet(name, out var value) && value.ValueKind == JsonValueKind.Object;

        public int ArrayLength(string name) =>
            TryGet(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;

        public JsonMap Object(string name) =>
            TryGet(name, out var value) && value.ValueKind == JsonValueKind.Object
                ? new JsonMap(value, true)
                : default;

        public JsonMap WithStatus(string status)
        {
            if (!Exists) return this;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("status", status);
                foreach (var property in Element.EnumerateObject())
                {
                    if (property.NameEquals("status")) continue;
                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            using var document = JsonDocument.Parse(stream.ToArray());
            return new JsonMap(document.RootElement.Clone(), true);
        }

        public bool TryGet(string name, out JsonElement value)
        {
            value = default;
            return Exists && Element.TryGetProperty(name, out value);
        }
    }
}
