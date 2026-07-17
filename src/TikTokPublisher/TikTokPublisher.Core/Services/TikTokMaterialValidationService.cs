using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokMaterialValidationService
{
    public sealed class Options
    {
        public bool SilenceValidationEnabled { get; init; } = true;
        public double MaxContinuousSilenceSeconds { get; init; } = TikTokVideoConstraints.DefaultMaxContinuousSilenceSeconds;
        public double SilenceThresholdDb { get; init; } = TikTokVideoConstraints.DefaultSilenceThresholdDb;
        public int Concurrency { get; init; } = 4;

        public static Options FromAccount(TikTokAccountProfile? account, ClientSettings? settings = null) => new()
        {
            SilenceValidationEnabled = account?.TiktokSilenceValidationEnabled ?? true,
            MaxContinuousSilenceSeconds = Math.Max(1, account?.TiktokMaxContinuousSilenceSeconds ?? (int)TikTokVideoConstraints.DefaultMaxContinuousSilenceSeconds),
            SilenceThresholdDb = account?.TiktokSilenceThresholdDb ?? TikTokVideoConstraints.DefaultSilenceThresholdDb,
            Concurrency = Math.Clamp(settings?.TiktokMaterialValidateConcurrency ?? 4, 1, 16),
        };
    }

    public static async Task ValidateAsync(
        string sourceProjectDir,
        string title,
        string originalTitle,
        Options options,
        Action<string>? log,
        CancellationToken ct,
        TikTokAccountProfile? account = null)
    {
        ct.ThrowIfCancellationRequested();
        var payload = TikTokUploadStagingService.BuildPayload(
            sourceProjectDir, title, originalTitle,
            rebuildStaging: false, repairSmallVideos: false, log, ct);
        if (payload.UploadPaths.Count == 0)
        {
            payload = TikTokUploadStagingService.BuildPayload(
                sourceProjectDir, title, originalTitle,
                rebuildStaging: true, repairSmallVideos: false, log, ct);
        }

        if (payload.UploadPaths.Count == 0)
            throw new InvalidOperationException("TikTok 素材校验失败：未找到可校验视频");

        if (payload.UploadPaths.Count != payload.SourcePaths.Count)
            throw new InvalidOperationException("TikTok 素材校验失败：上传副本数量异常");

        var ffprobe = MediaBinaryResolver.ResolveFfprobe();
        var issues = new List<string>();
        for (var index = 0; index < payload.SourcePaths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var sourcePath = payload.SourcePaths[index];
            var uploadPath = payload.UploadPaths[index];
            var episode = index + 1;
            var name = Path.GetFileName(sourcePath);

            long fileSize;
            try { fileSize = new FileInfo(uploadPath).Length; }
            catch (Exception ex)
            {
                issues.Add($"第{episode}集 | {name} | 读大小失败（{ex.Message}）");
                continue;
            }

            if (fileSize < TikTokVideoConstraints.MinSizeBytes &&
                TikTokSmallVideoPaddingService.SupportsPadding(uploadPath))
            {
                try
                {
                    if (await TikTokSmallVideoPaddingService.PadWithoutReencodeAsync(uploadPath, log, ct))
                        fileSize = new FileInfo(uploadPath).Length;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"第{episode}集 | {name} | 小文件自动补齐失败（{ex.Message}）");
                }
            }

            MediaProbe probe;
            try { probe = await MediaProbe.ProbeAsync(ffprobe, uploadPath, ct); }
            catch (Exception ex)
            {
                issues.Add($"第{episode}集 | {name} | 读时长失败（{ex.Message}）");
                continue;
            }

            foreach (var message in ValidateVideoLimits(fileSize, probe.DurationSeconds))
                issues.Add($"第{episode}集 | {name} | {message}");

            if (issues.Any(i => i.StartsWith($"第{episode}集", StringComparison.Ordinal)))
                continue;

            log?.Invoke(
                $"通过：第{episode}集 | {name} | {TikTokSmallVideoPaddingService.FormatSize(fileSize)} | {FormatDuration(probe.DurationSeconds)}");
        }

        if (issues.Count > 0)
        {
            foreach (var message in issues)
                log?.Invoke($"失败：{message}");
            var preview = string.Join("；", issues.Take(5));
            var suffix = issues.Count > 5 ? $"；等 {issues.Count} 个问题" : "";
            throw new InvalidOperationException($"TikTok 素材校验失败：{preview}{suffix}");
        }

        log?.Invoke($"通过：共 {payload.SourcePaths.Count} 个视频。");
        TikTokUploadManifestService.Save(sourceProjectDir, account, payload, log);
        SaveValidationState(sourceProjectDir, payload, options);
    }

    public static IEnumerable<string> ValidateVideoLimits(long fileSizeBytes, double durationSeconds)
    {
        if (fileSizeBytes < TikTokVideoConstraints.MinSizeBytes)
            yield return $"文件过小（{TikTokSmallVideoPaddingService.FormatSize(fileSizeBytes)} < {TikTokSmallVideoPaddingService.FormatSize(TikTokVideoConstraints.MinSizeBytes)}）";
        if (fileSizeBytes > TikTokVideoConstraints.MaxSizeBytes)
            yield return $"文件过大（{TikTokSmallVideoPaddingService.FormatSize(fileSizeBytes)} > {TikTokSmallVideoPaddingService.FormatSize(TikTokVideoConstraints.MaxSizeBytes)}）";
        if (durationSeconds < TikTokVideoConstraints.MinDurationSeconds)
            yield return $"时长过短（{FormatDuration(durationSeconds)} < {FormatDuration(TikTokVideoConstraints.MinDurationSeconds)}）";
        if (durationSeconds > TikTokVideoConstraints.MaxDurationSeconds)
            yield return $"时长过长（{FormatDuration(durationSeconds)} > {FormatDuration(TikTokVideoConstraints.MaxDurationSeconds)}）";
    }

    private static string FormatDuration(double seconds)
    {
        var total = Math.Max(0, (int)Math.Round(seconds));
        var minutes = total / 60;
        var rest = total % 60;
        return minutes > 0 ? $"{minutes}分{rest}秒" : $"{rest}秒";
    }

    private static void SaveValidationState(
        string sourceProjectDir,
        TikTokUploadStagingService.StagingResult payload,
        Options options)
    {
        var context = ProjectWorkspaceService.LoadContext(sourceProjectDir);
        var episodes = payload.UploadPaths
            .Select(TikTokSilenceAsrService.CacheKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(key => key, _ => (object?)true, StringComparer.Ordinal);

        var state = new Dictionary<string, object?>
        {
            ["fingerprint"] = ComputeMaterialFingerprint(payload.UploadPaths),
            ["params"] = ValidationParamsSignature(options),
            ["episodes"] = episodes,
        };
        ProjectStateDocumentStore.SaveDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            "material_validation_state",
            state,
            context.WorkflowProjectDir);
    }

    private static string ValidationParamsSignature(Options options) =>
        $"v2|material-only|{options.Concurrency}";

    internal static string ComputeMaterialFingerprint(IReadOnlyList<string> uploadVideoPaths)
    {
        var entries = new List<object?[]>();
        foreach (var path in uploadVideoPaths)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return "";
                var mtimeNs = (long)(info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100L;
                entries.Add([info.Name, info.Length, mtimeNs]);
            }
            catch
            {
                return "";
            }
        }

        if (entries.Count == 0) return "";
        entries.Sort((a, b) => string.CompareOrdinal(a[0]?.ToString(), b[0]?.ToString()));
        var text = JsonSerializer.Serialize(entries);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public static bool HasCurrentValidationState(string sourceProjectDir)
    {
        try
        {
            var uploadPaths = ProjectVideoResolver.ResolveUploadVideos(
                sourceProjectDir,
                allowStagedFallback: true);
            if (uploadPaths.Count == 0)
                return false;

            var context = ProjectWorkspaceService.LoadContext(sourceProjectDir);
            var state = ProjectStateDocumentStore.LoadDocument(
                context.WorkspaceRoot,
                context.SourceProjectDir,
                "material_validation_state");
            if (!state.TryGetValue("fingerprint", out var fingerprintElement))
                return false;

            var savedFingerprint = fingerprintElement.ValueKind == JsonValueKind.String
                ? fingerprintElement.GetString()
                : "";
            if (string.IsNullOrWhiteSpace(savedFingerprint))
                return false;

            return string.Equals(
                savedFingerprint.Trim(),
                ComputeMaterialFingerprint(uploadPaths),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
