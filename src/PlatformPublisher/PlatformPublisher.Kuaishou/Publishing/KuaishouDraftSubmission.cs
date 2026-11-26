using System.Globalization;
using System.Text.Json;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouBoundDraftCheck(KuaishouDraftInspection Inspection, string BatchIdentity);

public sealed record KuaishouDraftInspection(
    string SeriesId, string Title, int AuditStatus, int ExpectedEpisodes, int OnlineEpisodes, int ReadyEpisodes,
    bool EpisodeLayoutMatches, IReadOnlyList<int> ProcessingEpisodes, IReadOnlyList<int> MissingOrFailedEpisodes)
{
    public bool IsPending => AuditStatus == KuaishouDraftReplace.PendingAuditStatus;
    public bool AlreadySubmitted => AuditStatus is KuaishouDraftReplace.ReviewingAuditStatus or
        KuaishouDraftReplace.PublishedAuditStatus or KuaishouDraftReplace.RejectedAuditStatus;
    public bool CanSubmit => IsPending && EpisodeLayoutMatches && ReadyEpisodes == ExpectedEpisodes;

    public string Describe()
    {
        var name = $"《{Title}》（{SeriesId}）";
        if (AlreadySubmitted)
        {
            var status = AuditStatus switch
            {
                KuaishouDraftReplace.ReviewingAuditStatus => "审核中",
                KuaishouDraftReplace.PublishedAuditStatus => "审核已通过",
                _ => "审核未通过",
            };
            return $"{name}已提交审核，平台状态为「{status}」。";
        }
        if (!IsPending)
            return $"{name}当前状态无法确认，请在快手后台核对草稿。";

        var details = new List<string> { $"{name}线上 {OnlineEpisodes}/{ExpectedEpisodes} 集，已处理完成 {ReadyEpisodes} 集。" };
        if (!EpisodeLayoutMatches)
            details.Add("线上分集数量或集序与绑定批次不一致。");
        if (ProcessingEpisodes.Count > 0)
            details.Add($"处理中：{string.Join('、', ProcessingEpisodes)} 集。");
        if (MissingOrFailedEpisodes.Count > 0)
            details.Add($"缺失或失败：{string.Join('、', MissingOrFailedEpisodes)} 集。");
        if (CanSubmit)
            details.Add("线上成片齐全，可仅提交审核。");
        return string.Join(Environment.NewLine, details);
    }
}

public static class KuaishouDraftSubmission
{
    private enum EpisodeState { Ready, Processing, Unavailable }

    public static async Task<KuaishouDraftInspection> InspectAsync(
        IKuaishouKdjGateway gateway, string seriesId, int expectedEpisodes, CancellationToken cancellationToken)
    {
        seriesId = seriesId.Trim();
        if (!long.TryParse(seriesId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) ||
            numericId <= 0 || expectedEpisodes <= 0)
            throw new InvalidOperationException("草稿缺少有效短剧 ID 或绑定批次集数。");

        var target = await ReadTargetAsync(gateway, seriesId, cancellationToken);
        if (target.AuditStatus != KuaishouDraftReplace.PendingAuditStatus)
            return new(seriesId, target.Title, target.AuditStatus, expectedEpisodes, 0, 0, false, [], []);

        var response = await gateway.PostJsonAsync(KuaishouApiVideoUploader.DetailUrl,
            KuaishouDraftReplace.EpisodeDetailBody(seriesId), cancellationToken);
        using var document = JsonDocument.Parse(response);
        KuaishouSeriesDraft.EnsureSuccess(document.RootElement);
        var slots = new Dictionary<int, EpisodeState>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rowCount = 0;
        foreach (var row in KuaishouApiVideoUploader.ReadEpisodeRows(document.RootElement))
        {
            rowCount++;
            var id = ReadField(row, "episodeId");
            if (id.Length == 0 || !ids.Add(id) ||
                !int.TryParse(ReadField(row, "orderNo"), NumberStyles.None, CultureInfo.InvariantCulture, out var order))
                continue;
            slots.TryAdd(order, ReadEpisodeState(row));
        }

        // The detail API stores orderNo as a zero-based index.
        var expected = Enumerable.Range(0, expectedEpisodes).ToArray();
        var matches = rowCount == expectedEpisodes && slots.Count == rowCount && expected.All(slots.ContainsKey);
        var processing = expected.Where(index => slots.GetValueOrDefault(index, EpisodeState.Unavailable) == EpisodeState.Processing)
            .Select(index => index + 1).ToArray();
        var unavailable = expected.Where(index => slots.GetValueOrDefault(index, EpisodeState.Unavailable) == EpisodeState.Unavailable)
            .Select(index => index + 1).ToArray();
        return new(seriesId, target.Title, target.AuditStatus, expectedEpisodes, rowCount,
            expectedEpisodes - processing.Length - unavailable.Length, matches, processing, unavailable);
    }

    public static async Task<bool> SubmitAsync(
        IKuaishouKdjGateway gateway, string seriesId, int expectedEpisodes, CancellationToken cancellationToken)
    {
        var check = await InspectAsync(gateway, seriesId, expectedEpisodes, cancellationToken);
        if (check.AlreadySubmitted)
            return false;
        if (!check.CanSubmit)
            throw new InvalidOperationException(check.Describe());

        Exception? requestError = null;
        try
        {
            await new KuaishouApiVideoUploader().SubmitAsync(gateway, check.SeriesId, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
        {
            requestError = ex;
        }

        // A response can be lost after the server accepts the submission. Only query again here.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var target = await ReadTargetAsync(gateway, check.SeriesId, cancellationToken);
                if (target.AuditStatus is KuaishouDraftReplace.ReviewingAuditStatus or
                    KuaishouDraftReplace.PublishedAuditStatus or KuaishouDraftReplace.RejectedAuditStatus)
                    return true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or InvalidOperationException or JsonException)
            {
                requestError ??= ex;
            }
            if (attempt < 2)
                await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);
        }

        throw new InvalidOperationException($"短剧 {check.SeriesId} 的提审结果未确认。请先检查线上审核状态，再决定是否重试。", requestError);
    }

    private static async Task<KuaishouDraftReplace.Target> ReadTargetAsync(
        IKuaishouKdjGateway gateway, string seriesId, CancellationToken cancellationToken)
    {
        var response = await gateway.PostJsonAsync(KuaishouShelfApi.ListUrl, KuaishouShelfApi.StatusBody(seriesId), cancellationToken);
        using var document = JsonDocument.Parse(response);
        KuaishouSeriesDraft.EnsureSuccess(document.RootElement);
        if (!KuaishouDraftReplace.TryRead(document.RootElement, seriesId, out var target))
            throw new InvalidOperationException($"当前登录账号未找到绑定短剧 {seriesId}，请核对账号与草稿。");
        return target;
    }

    private static EpisodeState ReadEpisodeState(JsonElement row)
    {
        if (KuaishouApiVideoUploader.EpisodeFailed(row))
            return EpisodeState.Unavailable;
        if (KuaishouApiVideoUploader.EpisodeReady(row))
            return EpisodeState.Ready;
        if (KuaishouApiVideoUploader.EpisodeProcessing(row) || ReadField(row, "videoUploadStatus") == "1")
            return EpisodeState.Processing;
        return EpisodeState.Unavailable;
    }

    private static string ReadField(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString().Trim() : "";
}
