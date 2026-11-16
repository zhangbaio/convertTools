using System.Text.Json;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouCommitmentResubmitResult(
    int Found,
    int Submitted,
    int Failed,
    bool StoppedByDailyLimit,
    IReadOnlyList<string> Lines);

public sealed class KuaishouCommitmentResubmitService
{
    public async Task<IReadOnlyList<KuaishouCommitmentResubmit.Target>> ListAsync(
        IKuaishouKdjGateway gateway,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        int pageSize = KuaishouCommitmentResubmit.PageSize)
    {
        if (pageSize < 1)
            pageSize = KuaishouCommitmentResubmit.PageSize;
        var found = new List<KuaishouCommitmentResubmit.Target>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var page = 1;
        var total = int.MaxValue;
        while ((page - 1) * pageSize < total && page <= 100)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(page == 1
                ? "正在查找已上架且承诺函未加盖公章的短剧。"
                : $"正在查找已上架且承诺函未加盖公章的短剧，第 {page} 页。");
            var text = await gateway.PostJsonAsync(
                KuaishouCommitmentResubmit.QueryListUrl,
                KuaishouCommitmentResubmit.QueryBody(page, pageSize),
                cancellationToken);
            using var body = JsonDocument.Parse(text);
            KuaishouSeriesDraft.EnsureSuccess(body.RootElement);
            var pageMatches = new List<KuaishouCommitmentResubmit.Target>();
            var raw = KuaishouCommitmentResubmit.ReadPage(body.RootElement, pageMatches, out var reported);
            if (reported > 0)
                total = reported;
            foreach (var target in pageMatches)
            {
                if (seen.Add(target.SeriesId))
                    found.Add(target);
            }

            if (raw == 0 || raw < pageSize)
                break;
            page++;
        }

        return found;
    }

    public async Task<KuaishouCommitmentResubmitResult> SubmitAsync(
        IKuaishouKdjGateway gateway,
        IReadOnlyList<KuaishouCommitmentResubmit.Target> targets,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var submitted = 0;
        var failed = 0;
        var stopped = false;
        KuaishouCommitmentResubmit.SaveContext context;
        try
        {
            context = await ReadContextAsync(gateway, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            lines.Add("已停止重新提交。");
            return new KuaishouCommitmentResubmitResult(targets.Count, submitted, failed, stopped, lines);
        }

        for (var index = 0; index < targets.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                lines.Add("已停止重新提交。");
                break;
            }

            var target = targets[index];
            progress?.Report($"正在按原内容保存《{target.Title}》。");
            try
            {
                var infoText = await gateway.PostJsonAsync(
                    KuaishouCommitmentResubmit.BaseInfoUrl,
                    KuaishouCommitmentResubmit.SeriesIdBody(target.SeriesId),
                    cancellationToken);
                using var info = JsonDocument.Parse(infoText);
                KuaishouSeriesDraft.EnsureSuccess(info.RootElement);
                var episodeText = await gateway.PostJsonAsync(
                    KuaishouCommitmentResubmit.EpisodeListUrl,
                    KuaishouCommitmentResubmit.EpisodeListBody(target.SeriesId),
                    cancellationToken);
                using var episodes = JsonDocument.Parse(episodeText);
                KuaishouSeriesDraft.EnsureSuccess(episodes.RootElement);
                var updateText = await gateway.PostJsonAsync(
                    KuaishouCommitmentResubmit.UpdateUrl,
                    KuaishouCommitmentResubmit.UpdateBody(info.RootElement, episodes.RootElement, context, target.SeriesId),
                    cancellationToken);
                using var updated = JsonDocument.Parse(updateText);
                KuaishouSeriesDraft.EnsureSuccess(updated.RootElement);
                if (!KuaishouCommitmentResubmit.CanModifyVideo(updated.RootElement))
                {
                    failed++;
                    var saved = $"《{target.Title}》已按原内容保存，平台认为这次不需要重新审核。";
                    lines.Add(saved);
                    progress?.Report(saved);
                    continue;
                }

                progress?.Report($"正在重新提交《{target.Title}》。");
                var text = await gateway.PostJsonAsync(
                    KuaishouApiVideoUploader.SubmitUrl,
                    KuaishouCommitmentResubmit.SubmitBody(target.SeriesId),
                    cancellationToken);
                using var body = JsonDocument.Parse(text);
                KuaishouSeriesDraft.EnsureSuccess(body.RootElement);
                submitted++;
                var line = $"《{target.Title}》已重新提交。";
                lines.Add(line);
                progress?.Report(line);
            }
            catch (OperationCanceledException)
            {
                lines.Add("已停止重新提交。");
                break;
            }
            catch (Exception ex) when (KuaishouDailyUploadLimit.Is(ex))
            {
                stopped = true;
                var remain = targets.Count - index - 1;
                var line = remain > 0
                    ? $"今日新建短剧已达 10 部，其余 {remain} 部未再提交。"
                    : "今日新建短剧已达 10 部，已停止重新提交。";
                lines.Add(line);
                progress?.Report(line);
                break;
            }
            catch (Exception ex)
            {
                failed++;
                var line = $"《{target.Title}》重新提交失败：{OneLine(ex.Message)}";
                lines.Add(line);
                progress?.Report(line);
            }
        }

        return new KuaishouCommitmentResubmitResult(targets.Count, submitted, failed, stopped, lines);
    }

    private static async Task<KuaishouCommitmentResubmit.SaveContext> ReadContextAsync(
        IKuaishouKdjGateway gateway,
        CancellationToken cancellationToken)
    {
        var startupText = await gateway.GetAsync(KuaishouCommitmentResubmit.StartupUrl, cancellationToken);
        using var startup = JsonDocument.Parse(startupText);
        KuaishouSeriesDraft.EnsureSuccess(startup.RootElement);
        var teacherText = await gateway.GetAsync(KuaishouCommitmentResubmit.TeacherInfoUrl, cancellationToken);
        using var teacher = JsonDocument.Parse(teacherText);
        KuaishouSeriesDraft.EnsureSuccess(teacher.RootElement);
        return KuaishouCommitmentResubmit.ReadSaveContext(startup.RootElement, teacher.RootElement);
    }

    private static string OneLine(string? message)
    {
        var text = (message ?? "").Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return text.Length <= 180 ? text : text[..180];
    }
}
