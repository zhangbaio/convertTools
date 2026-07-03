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

        public static Options FromAccount(TikTokAccountProfile? account) => new()
        {
            SilenceValidationEnabled = account?.TiktokSilenceValidationEnabled ?? true,
            MaxContinuousSilenceSeconds = Math.Max(1, account?.TiktokMaxContinuousSilenceSeconds ?? (int)TikTokVideoConstraints.DefaultMaxContinuousSilenceSeconds),
            SilenceThresholdDb = account?.TiktokSilenceThresholdDb ?? TikTokVideoConstraints.DefaultSilenceThresholdDb,
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
        var payload = TikTokUploadStagingService.BuildPayload(
            sourceProjectDir, title, originalTitle,
            rebuildStaging: false, repairSmallVideos: false, log);
        if (payload.UploadPaths.Count == 0)
        {
            payload = TikTokUploadStagingService.BuildPayload(
                sourceProjectDir, title, originalTitle,
                rebuildStaging: true, repairSmallVideos: false, log);
        }

        if (payload.UploadPaths.Count == 0)
            throw new InvalidOperationException("TikTok 素材校验失败：未找到可校验视频");

        if (payload.UploadPaths.Count != payload.SourcePaths.Count)
            throw new InvalidOperationException("TikTok 素材校验失败：上传副本数量异常");

        TikTokUploadManifestService.Save(sourceProjectDir, account, payload, log);

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

            if (options.SilenceValidationEnabled &&
                string.IsNullOrWhiteSpace(probe.AudioCodec) &&
                probe.AudioBitrateBps <= 0)
            {
                issues.Add($"第{episode}集 | {name} | 无音轨（按静音失败，阈值 {(int)options.MaxContinuousSilenceSeconds} 秒）");
                continue;
            }

            foreach (var message in ValidateVideoLimits(fileSize, probe.DurationSeconds))
                issues.Add($"第{episode}集 | {name} | {message}");

            if (issues.Any(i => i.StartsWith($"第{episode}集", StringComparison.Ordinal)))
                continue;

            if (options.SilenceValidationEnabled)
            {
                var segments = await TikTokAudioSilenceService.DetectExcessiveSilenceAsync(
                    uploadPath,
                    probe.DurationSeconds,
                    options.MaxContinuousSilenceSeconds,
                    options.SilenceThresholdDb,
                    ct);
                if (segments.Count > 0)
                {
                    var first = segments[0];
                    var extra = segments.Count > 1 ? $"；另有 {segments.Count - 1} 处" : "";
                    issues.Add(
                        $"第{episode}集 | {name} | 连续静音>{(int)options.MaxContinuousSilenceSeconds}秒（{TikTokAudioSilenceService.FormatSegment(first)}{extra}）");
                    continue;
                }
            }

            log?.Invoke(
                $"通过：第{episode}集 | {name} | {TikTokSmallVideoPaddingService.FormatSize(fileSize)} | {FormatDuration(probe.DurationSeconds)} | 静音{(options.SilenceValidationEnabled ? "正常" : "已跳过")}");
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
        $"v1|{(options.SilenceValidationEnabled ? 1 : 0)}|{options.MaxContinuousSilenceSeconds:g}|{options.SilenceThresholdDb:g}";

    private static string ComputeMaterialFingerprint(IReadOnlyList<string> uploadVideoPaths)
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
}
