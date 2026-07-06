using System.Globalization;
using TikTokPublisher.Core.Media;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 静音修复：基于 ASR 找到的“最长无台词区间”，按位置自动裁剪 / 变速（对齐 Python
/// <c>repair_tiktok_silence</c>）。中间段可变速；片头/片尾用裁剪。
/// </summary>
public static class TikTokSilenceRepairService
{
    public static async Task RepairAsync(
        string sourceProjectDir,
        string title,
        string originalTitle,
        TikTokMaterialValidationService.Options options,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var payload = TikTokUploadStagingService.BuildPayload(
            sourceProjectDir, title, originalTitle,
            rebuildStaging: true, repairSmallVideos: true, log, ct);
        if (payload.UploadPaths.Count == 0)
        {
            log?.Invoke("跳过：未找到可修复的上传视频。");
            return;
        }

        var settings = ClientSettingsStore.Load();
        var (ok, reason) = TikTokSilenceAsrService.CheckAvailable(settings);
        if (!ok)
        {
            log?.Invoke($"⚠️ 跳过静音修复：{reason}");
            return;
        }

        var threshold = (double)Math.Max(5, settings.TiktokSilenceAsrThresholdSeconds);
        var target = Math.Max(3.0, settings.TiktokSilenceRepairTargetSeconds);
        var mode = NormalizeMode(settings.TiktokSilenceRepairMode);
        var maxSpeed = Math.Clamp(settings.TiktokSilenceRepairMaxSpeed, 1.1, 4.0);
        var blocking = settings.TiktokSilenceRepairBlocking;

        log?.Invoke(
            $"开始：检测并修复 ≥{threshold:F0} 秒无台词片段，目标压到 ≤{target:F0} 秒" +
            $"（引擎：{TikTokAsrEngine.Label(TikTokAsrEngine.Normalize(settings.TiktokSilenceAsrEngine))}，方式：{ModeLabel(mode)}）。");

        var repaired = 0;
        var failures = new List<string>();
        for (var i = 0; i < payload.UploadPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var uploadPath = payload.UploadPaths[i];
            SilenceGapReport gap;
            try
            {
                gap = await TikTokSilenceAsrService.AnalyzeAsync(uploadPath, i + 1, settings, log, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke($"⚠️ 第{i + 1}集 | {Path.GetFileName(uploadPath)} | 检测失败（{ex.Message}），已跳过修复。");
                continue;
            }

            if (gap.MaxGapSeconds < threshold) continue;

            try
            {
                var applied = await RepairOneAsync(
                    uploadPath, gap, target, mode, maxSpeed, ct).ConfigureAwait(false);
                repaired++;
                log?.Invoke($"✅ 第{gap.EpisodeIndex}集 | {gap.Name} | {applied}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"第{i + 1}集 | {Path.GetFileName(uploadPath)} | {ex.Message}");
                log?.Invoke($"❌ 第{i + 1}集 | {Path.GetFileName(uploadPath)} | 修复失败（{ex.Message}）");
            }
        }

        if (failures.Count > 0 && blocking)
        {
            var preview = string.Join("；", failures.Take(5));
            var suffix = failures.Count > 5 ? $"；等 {failures.Count} 个" : "";
            throw new InvalidOperationException($"TikTok 静音修复失败：{preview}{suffix}");
        }

        log?.Invoke(repaired > 0
            ? $"完成：共修复 {repaired} 集。" + (failures.Count > 0 ? $"{failures.Count} 集失败（不阻断）。" : "")
            : "完成：没有需要修复的集。");
    }

    private static async Task<string> RepairOneAsync(
        string uploadPath,
        SilenceGapReport gap,
        double target,
        string mode,
        double maxSpeed,
        CancellationToken ct)
    {
        var duration = gap.DurationSeconds;
        var t1 = gap.GapStartSeconds;
        var t2 = gap.GapEndSeconds;
        var g = Math.Max(0.0, t2 - t1);
        if (g <= target) return "无需处理";

        var resolved = mode;
        if (mode == "auto")
        {
            resolved = gap.Position is "head" or "tail"
                ? "trim"
                : (g / target) <= maxSpeed ? "speedup" : "trim";
        }

        var ffmpeg = MediaBinaryResolver.ResolveFfmpeg();
        var ffprobe = MediaBinaryResolver.ResolveFfprobe();

        if (resolved == "speedup")
        {
            var factor = Math.Min(maxSpeed, g / target);
            await FfmpegSpeedupSegmentAsync(ffmpeg, ffprobe, uploadPath, t1, t2, factor, ct)
                .ConfigureAwait(false);
            return $"{PositionLabel(gap.Position)}段变速 {factor:F2}×（{g:F1}s→{g / factor:F1}s）";
        }

        var remove = g - target;
        double cutStart, cutEnd;
        if (gap.Position == "head")
        {
            cutStart = 0;
            cutEnd = Math.Min(t2, remove);
        }
        else if (gap.Position == "tail")
        {
            cutStart = Math.Max(t1, duration - remove);
            cutEnd = duration;
        }
        else
        {
            var mid = (t1 + t2) / 2.0;
            cutStart = Math.Max(t1, mid - remove / 2.0);
            cutEnd = Math.Min(t2, cutStart + remove);
        }

        await FfmpegCutOutAsync(ffmpeg, ffprobe, uploadPath, cutStart, cutEnd, duration, ct)
            .ConfigureAwait(false);
        return $"{PositionLabel(gap.Position)}段裁剪 " +
               $"{TikTokSilenceAsrService.FormatTimestamp(cutStart)}–{TikTokSilenceAsrService.FormatTimestamp(cutEnd)}" +
               $"（-{cutEnd - cutStart:F1}s）";
    }

    private static async Task FfmpegSpeedupSegmentAsync(
        string ffmpeg, string ffprobe, string path,
        double t1, double t2, double factor, CancellationToken ct)
    {
        var hasAudio = await HasAudioStreamAsync(ffprobe, path, ct).ConfigureAwait(false);
        var parts = new List<string>();
        var labels = new List<int>();
        var seg = 0;
        var ci = CultureInfo.InvariantCulture;

        if (t1 > 0.05)
        {
            parts.Add($"[0:v]trim=0:{t1.ToString("F3", ci)},setpts=PTS-STARTPTS[v{seg}]");
            if (hasAudio) parts.Add($"[0:a]atrim=0:{t1.ToString("F3", ci)},asetpts=PTS-STARTPTS[a{seg}]");
            labels.Add(seg); seg++;
        }
        parts.Add($"[0:v]trim={t1.ToString("F3", ci)}:{t2.ToString("F3", ci)},setpts=(PTS-STARTPTS)/{factor.ToString("F6", ci)}[v{seg}]");
        if (hasAudio) parts.Add($"[0:a]atrim={t1.ToString("F3", ci)}:{t2.ToString("F3", ci)},asetpts=PTS-STARTPTS,atempo={factor.ToString("F6", ci)}[a{seg}]");
        labels.Add(seg); seg++;
        parts.Add($"[0:v]trim={t2.ToString("F3", ci)},setpts=PTS-STARTPTS[v{seg}]");
        if (hasAudio) parts.Add($"[0:a]atrim={t2.ToString("F3", ci)},asetpts=PTS-STARTPTS[a{seg}]");
        labels.Add(seg);

        await RunConcatFilterAsync(ffmpeg, path, parts, labels, hasAudio, ct).ConfigureAwait(false);
    }

    private static async Task FfmpegCutOutAsync(
        string ffmpeg, string ffprobe, string path,
        double cutStart, double cutEnd, double duration, CancellationToken ct)
    {
        var hasAudio = await HasAudioStreamAsync(ffprobe, path, ct).ConfigureAwait(false);
        var parts = new List<string>();
        var labels = new List<int>();
        var seg = 0;
        var ci = CultureInfo.InvariantCulture;

        if (cutStart > 0.05)
        {
            parts.Add($"[0:v]trim=0:{cutStart.ToString("F3", ci)},setpts=PTS-STARTPTS[v{seg}]");
            if (hasAudio) parts.Add($"[0:a]atrim=0:{cutStart.ToString("F3", ci)},asetpts=PTS-STARTPTS[a{seg}]");
            labels.Add(seg); seg++;
        }
        if (duration - cutEnd > 0.05)
        {
            parts.Add($"[0:v]trim={cutEnd.ToString("F3", ci)},setpts=PTS-STARTPTS[v{seg}]");
            if (hasAudio) parts.Add($"[0:a]atrim={cutEnd.ToString("F3", ci)},asetpts=PTS-STARTPTS[a{seg}]");
            labels.Add(seg);
        }
        if (labels.Count == 0)
            throw new InvalidOperationException("裁剪区间覆盖整段，已放弃");

        await RunConcatFilterAsync(ffmpeg, path, parts, labels, hasAudio, ct).ConfigureAwait(false);
    }

    private static async Task RunConcatFilterAsync(
        string ffmpeg, string path,
        List<string> parts, List<int> labels, bool hasAudio, CancellationToken ct)
    {
        string concat, outV, outA;
        var n = labels.Count;
        if (n == 1)
        {
            concat = "";
            outV = $"[v{labels[0]}]";
            outA = $"[a{labels[0]}]";
        }
        else
        {
            var inputs = string.Concat(labels.Select(i => hasAudio ? $"[v{i}][a{i}]" : $"[v{i}]"));
            concat = hasAudio
                ? $";{inputs}concat=n={n}:v=1:a=1[v][a]"
                : $";{inputs}concat=n={n}:v=1:a=0[v]";
            outV = "[v]";
            outA = "[a]";
        }
        var filterComplex = string.Join(";", parts) + concat;
        var tmp = path + ".silencefix.mp4";
        if (File.Exists(tmp)) File.Delete(tmp);

        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", path,
            "-filter_complex", filterComplex,
            "-map", outV,
        };
        if (hasAudio) args.AddRange(new[] { "-map", outA, "-c:a", "aac", "-b:a", "128k" });
        else args.Add("-an");
        args.AddRange(new[]
        {
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
            "-pix_fmt", "yuv420p", "-movflags", "+faststart",
            tmp,
        });

        try
        {
            await FfmpegRunner.RunAsync(ffmpeg, args, ct).ConfigureAwait(false);
            if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0)
                throw new InvalidOperationException("ffmpeg 处理失败");
            File.Copy(tmp, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch
            {
            }
        }
    }

    private static async Task<bool> HasAudioStreamAsync(string ffprobe, string path, CancellationToken ct)
    {
        var (_, stdout, _) = await FfmpegRunner.RunCaptureAsync(ffprobe, new[]
        {
            "-v", "error", "-select_streams", "a",
            "-show_entries", "stream=index",
            "-of", "csv=p=0", path,
        }, ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(stdout);
    }

    private static string NormalizeMode(string? mode)
    {
        var n = (mode ?? "auto").Trim().ToLowerInvariant();
        return n is "auto" or "trim" or "speedup" ? n : "auto";
    }

    private static string ModeLabel(string mode) => mode switch
    {
        "auto" => "自动(片头尾裁剪/中间变速)",
        "trim" => "一律裁剪",
        "speedup" => "一律变速",
        _ => mode,
    };

    private static string PositionLabel(string position) => position switch
    {
        "head" => "片头",
        "middle" => "中间",
        "tail" => "片尾",
        _ => position,
    };
}
