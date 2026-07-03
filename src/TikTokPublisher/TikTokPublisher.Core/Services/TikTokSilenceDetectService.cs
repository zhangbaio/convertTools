using TikTokPublisher.Core.Media;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// TikTok 静音检测：优先走 ASR 无台词间隔（对齐 Python <c>detect_tiktok_silence</c>）；
/// 未启用火山凭据时退回电平静音（ffmpeg silencedetect）。检测不阻断队列，只写日志。
/// </summary>
public static class TikTokSilenceDetectService
{
    public static async Task DetectAsync(
        string sourceProjectDir,
        string title,
        string originalTitle,
        TikTokMaterialValidationService.Options options,
        Action<string>? log,
        CancellationToken ct)
    {
        var payload = TikTokUploadStagingService.BuildPayload(
            sourceProjectDir, title, originalTitle,
            rebuildStaging: false, repairSmallVideos: false, log);
        if (payload.UploadPaths.Count == 0)
        {
            log?.Invoke("跳过：未找到可检测的上传视频。");
            return;
        }

        var settings = ClientSettingsStore.Load();
        var threshold = Math.Max(5, settings.TiktokSilenceAsrThresholdSeconds);
        var engine = TikTokAsrEngine.Normalize(settings.TiktokSilenceAsrEngine);
        var (ok, reason) = TikTokSilenceAsrService.CheckAvailable(settings);
        if (!ok)
        {
            log?.Invoke($"⚠️ 跳过静音检测：{reason}");
            return;
        }

        log?.Invoke(
            $"开始：用 {TikTokAsrEngine.Label(engine)} 检测 {payload.UploadPaths.Count} 集，" +
            $"阈值 ≥{threshold} 秒无台词视为风险。");

        IReadOnlyList<SilenceGapReport> reports;
        try
        {
            reports = await TikTokSilenceAsrService.DetectAsync(payload.UploadPaths, settings, log, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"⚠️ 静音检测失败（{ex.Message}），已跳过。");
            return;
        }

        var flagged = 0;
        for (var i = 0; i < reports.Count; i++)
        {
            var report = reports[i];
            if (report is null || report.MaxGapSeconds <= 0) continue;
            if (report.MaxGapSeconds < threshold) continue;
            flagged++;
            log?.Invoke(
                $"⚠️ 风险 第{report.EpisodeIndex}集 | {report.Name} | " +
                $"{TikTokSilenceAsrService.FormatTimestamp(report.GapStartSeconds)}–" +
                $"{TikTokSilenceAsrService.FormatTimestamp(report.GapEndSeconds)} " +
                $"连续无台词 {report.MaxGapSeconds:F1} 秒（{PositionLabel(report.Position)}）");
        }

        log?.Invoke(flagged > 0
            ? $"完成：共 {payload.UploadPaths.Count} 集，其中 {flagged} 集存在 ≥{threshold} 秒无台词风险。检测不阻断后续步骤；如需自动处理请启用「静音修复」。"
            : $"完成：共 {payload.UploadPaths.Count} 集，均未发现 ≥{threshold} 秒无台词风险。");
    }

    private static string PositionLabel(string position) => position switch
    {
        "head" => "片头",
        "middle" => "中间",
        "tail" => "片尾",
        _ => position,
    };
}
