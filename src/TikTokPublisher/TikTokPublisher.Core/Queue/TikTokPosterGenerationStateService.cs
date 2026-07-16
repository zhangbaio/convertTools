using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

/// <summary>
/// Tracks whether the workflow poster was actually generated for the current title and settings.
/// A source poster named 海报图片.* is only an input and must not be treated as generated output.
/// </summary>
public static class TikTokPosterGenerationStateService
{
    public const string StateDocumentType = "tiktok_poster_generation_state";
    public const string OutputFileName = "海报图片.png";

    private const string FingerprintVersion = "v2-title-input-config-single-title-only";

    public static bool HasCurrentTitleState(QueueProjectItem item)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(item);
            var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
            var state = LoadState(context);
            if (state.Count == 0 || !IsUsableFile(GetOutputPath(context)))
                return false;

            return !string.IsNullOrWhiteSpace(GetStateString(state, "fingerprint")) &&
                   string.Equals(
                       GetStateString(state, "drama_title"),
                       item.Title.Trim(),
                       StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static bool NeedsGeneratePoster(QueueProjectItem item, ClientSettings settings)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(settings);
            var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
            var state = LoadState(context);

            if (state.Count == 0)
            {
                // Keep legacy non-local projects compatible, but local-manual imports must run once:
                // their source poster alias is raw input copied into workflow, not a renamed poster.
                return LocalManualDramaImportService.IsLocalManualImportProject(context.SourceProjectDir) ||
                       !HasLegacyPosterArtifact(context);
            }

            var outputPath = GetOutputPath(context);
            if (!IsUsableFile(outputPath))
                return true;

            var inputPath = GetStateString(state, "input_path");
            if (!IsUsableFile(inputPath))
                return true;

            var expectedFingerprint = ComputeFingerprint(item, settings, context, inputPath);
            return !string.Equals(
                GetStateString(state, "fingerprint"),
                expectedFingerprint,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Invalid or missing inputs should execute the selected step and surface its actionable error.
            return true;
        }
    }

    public static void SaveGeneratedState(
        QueueProjectItem item,
        ClientSettings settings,
        string inputPath,
        string outputPath,
        string? effectivePosterMode = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var normalizedInput = Path.GetFullPath(inputPath);
        var normalizedOutput = Path.GetFullPath(outputPath);
        var expectedOutput = GetOutputPath(context);

        if (!string.Equals(normalizedOutput, expectedOutput, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"海报生成到了非当前项目目录：{normalizedOutput}；预期路径：{expectedOutput}。");
        if (!IsUsableFile(normalizedInput))
            throw new InvalidDataException($"海报输入文件无效：{normalizedInput}");
        if (!IsUsableFile(normalizedOutput))
            throw new InvalidDataException($"海报输出文件无效：{normalizedOutput}");

        var fingerprint = ComputeFingerprint(item, settings, context, normalizedInput);
        var payload = new Dictionary<string, object?>
        {
            ["fingerprint"] = fingerprint,
            ["drama_title"] = item.Title.Trim(),
            ["poster_mode"] = NormalizePosterMode(settings.PosterMode),
            ["effective_poster_mode"] = NormalizePosterMode(effectivePosterMode ?? settings.PosterMode),
            ["input_path"] = normalizedInput,
            ["input_sha256"] = ComputeFileSha256(normalizedInput),
            ["output_path"] = normalizedOutput,
            ["generated_at"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
        ProjectStateDocumentStore.SaveDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            StateDocumentType,
            payload,
            context.WorkflowProjectDir);
    }

    private static string ComputeFingerprint(
        QueueProjectItem item,
        ClientSettings settings,
        ProjectWorkspaceContext context,
        string inputPath)
    {
        var posterMode = NormalizePosterMode(settings.PosterMode);
        var payload = new
        {
            version = FingerprintVersion,
            drama_title = item.Title.Trim(),
            poster_mode = posterMode,
            input_sha256 = ComputeFileSha256(inputPath),
            source_media_stamp = BuildSourceMediaStamp(context, settings, posterMode),
            image_provider = (settings.ImageProvider ?? string.Empty).Trim(),
            image_model_id = (settings.ImageModelId ?? string.Empty).Trim(),
            image_model_endpoint = (settings.ImageModelEndpoint ?? string.Empty).Trim(),
            doubao_resolution = (settings.DoubaoImageResolution ?? string.Empty).Trim(),
            doubao_ratio = (settings.DoubaoImageRatio ?? string.Empty).Trim(),
            ofox_model_id = (settings.OfoxImage2ModelId ?? string.Empty).Trim(),
            ofox_endpoint = (settings.OfoxImage2Endpoint ?? string.Empty).Trim(),
            ofox_quality = (settings.OfoxImage2Quality ?? string.Empty).Trim(),
            ofox_size = (settings.OfoxImage2Size ?? string.Empty).Trim(),
            title_verify_enabled = settings.PosterTitleVerifyEnabled,
            title_verify_mode = (settings.PosterTitleVerifyMode ?? string.Empty).Trim(),
            title_verify_retries = settings.PosterTitleVerifyAiRetryCount,
            frame_episode = settings.FrameExtractEpisodeIndex,
            frame_time = settings.FrameExtractTime,
            frame_neighbor_offsets = (settings.FrameExtractNeighborOffsetsSeconds ?? string.Empty).Trim(),
            frame_fallback_percents = (settings.FrameExtractFallbackPercents ?? string.Empty).Trim(),
            frame_prompt = settings.FrameCoverPrompt ?? string.Empty,
            layout_prompt = settings.PosterLayoutDetectPrompt ?? string.Empty,
            inpaint_prompt = settings.PosterInpaintPrompt ?? string.Empty,
            inpaint_retry_prompt = settings.PosterInpaintSafeRetryPrompt ?? string.Empty,
            generation_prompt = settings.PosterGenerationPrompt ?? string.Empty,
            generation_retry_prompt = settings.PosterGenerationSafeRetryPrompt ?? string.Empty,
        };
        var json = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static string BuildSourceMediaStamp(
        ProjectWorkspaceContext context,
        ClientSettings settings,
        string posterMode)
    {
        if (!string.Equals(posterMode, "video_frame", StringComparison.Ordinal))
            return string.Empty;

        var videos = ProjectVideoResolver.ResolveSourceVideos(context.SourceProjectDir, allowStagedFallback: true);
        if (videos.Count == 0)
            return "missing";

        var selected = VideoFramePosterSourceService.SelectVideoForEpisode(
            videos,
            Math.Clamp(settings.FrameExtractEpisodeIndex, 1, 999));
        var info = new FileInfo(selected);
        return $"{Path.GetFullPath(selected)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private static Dictionary<string, JsonElement> LoadState(ProjectWorkspaceContext context) =>
        ProjectStateDocumentStore.LoadDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            StateDocumentType);

    private static string GetOutputPath(ProjectWorkspaceContext context) =>
        Path.GetFullPath(Path.Combine(context.WorkflowProjectDir, OutputFileName));

    private static bool HasLegacyPosterArtifact(ProjectWorkspaceContext context)
    {
        foreach (var root in new[] { context.WorkflowProjectDir, context.SourceProjectDir })
        {
            foreach (var name in new[] { "海报图片.png", "海报图片.jpg" })
            {
                if (File.Exists(Path.Combine(root, name)))
                    return true;
            }
        }

        return false;
    }

    private static bool IsUsableFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        try
        {
            return new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string GetStateString(IReadOnlyDictionary<string, JsonElement> state, string key)
    {
        if (!state.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String)
            return string.Empty;
        return value.GetString()?.Trim() ?? string.Empty;
    }

    private static string NormalizePosterMode(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ClientSettingsDefaults.PosterMode
            : value.Trim().ToLowerInvariant();
}
