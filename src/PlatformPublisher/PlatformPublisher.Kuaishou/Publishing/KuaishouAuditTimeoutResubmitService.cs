using System.Text.Json;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Kuaishou.Publishing;

public enum KuaishouAuditTimeoutSubmitStatus
{
    Accepted,
    PendingConfirmation,
    Failed,
    Skipped,
}

public sealed record KuaishouAuditTimeoutSubmitResult(
    KuaishouAuditTimeoutTarget Target,
    KuaishouAuditTimeoutSubmitStatus Status,
    string Message);

public sealed class KuaishouAuditTimeoutResubmitService
{
    private const string PendingKey = "kuaishou.audit-timeout.pending";
    private readonly AccountJsonSettingStore _settings;
    private readonly string _accountId;
    private HashSet<string> _pending;

    public KuaishouAuditTimeoutResubmitService(AccountJsonSettingStore settings, string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        _settings = settings;
        _accountId = accountId;
        _pending = settings.TryLoad<string[]>(accountId, PendingKey, out var pending) && pending is not null
            ? pending.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<KuaishouAuditTimeoutTarget>> ListAsync(
        IKuaishouKdjGateway gateway,
        IProgress<string>? progress,
        CancellationToken ct,
        int pageSize = KuaishouCommitmentResubmit.PageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        foreach (var seriesId in _pending.ToArray())
        {
            if (await TryCheckAcceptedAsync(gateway, seriesId, ct).ConfigureAwait(false))
                SetPending(seriesId, false);
        }

        var found = new List<KuaishouAuditTimeoutTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rowsRead = 0;
        for (var page = 1; ; page++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"正在读取审核未通过剧集，第 {page} 页。");
            var payload = KuaishouShelfApi.ListBody(page, pageSize, KuaishouShelfApi.OfflineSellingStatus,
                KuaishouDraftReplace.RejectedAuditStatus, []);
            using var document = await PostAsync(gateway, KuaishouShelfApi.ListUrl, payload, ct).ConfigureAwait(false);
            var data = KuaishouAuditTimeoutResubmit.Property(document.RootElement, "data");
            var list = KuaishouAuditTimeoutResubmit.Property(data, "data");
            if (list.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("快手审核列表返回不完整，请重新查询。");

            var newRows = 0;
            foreach (var row in list.EnumerateArray())
            {
                var id = KuaishouAuditTimeoutResubmit.Text(row, "miniSeriesId");
                if (id.Length == 0 || !seen.Add(id))
                    continue;
                newRows++;
                if (KuaishouAuditTimeoutResubmit.TryReadTarget(row, out var target) && target is not null)
                    found.Add(target with { Pending = _pending.Contains(id) });
            }

            rowsRead += list.GetArrayLength();
            var total = KuaishouAuditTimeoutResubmit.ReadNumber(data, "totalCount");
            if (list.GetArrayLength() > 0 && newRows == 0)
                throw new InvalidOperationException("快手审核列表重复返回同一页，请重新查询。");
            if ((total >= 0 && rowsRead >= total) || list.GetArrayLength() < pageSize)
                break;
        }

        return found;
    }

    public async Task<IReadOnlyList<KuaishouAuditTimeoutSubmitResult>> SubmitAsync(
        IKuaishouKdjGateway gateway,
        IReadOnlyList<KuaishouAuditTimeoutTarget> selected,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var targets = selected.DistinctBy(target => target.SeriesId, StringComparer.Ordinal).ToArray();
        var result = new List<KuaishouAuditTimeoutSubmitResult>();
        KuaishouCommitmentResubmit.SaveContext? saveContext = null;
        foreach (var selectedTarget in targets)
        {
            if (ct.IsCancellationRequested)
                break;

            void Report(KuaishouAuditTimeoutSubmitStatus status, string detail)
            {
                var message = $"《{selectedTarget.Title}》（{selectedTarget.SeriesId}）{detail}";
                result.Add(new(selectedTarget, status, message));
                progress?.Report(message);
            }

            if (_pending.Contains(selectedTarget.SeriesId))
            {
                Report(KuaishouAuditTimeoutSubmitStatus.PendingConfirmation, "已有一笔提交待确认。");
                continue;
            }

            Exception? submitRejection = null;
            var startedSubmit = false;
            try
            {
                using var current = await ReadStatusAsync(gateway, selectedTarget.SeriesId, ct).ConfigureAwait(false);
                var row = FindRow(current.RootElement, selectedTarget.SeriesId);
                if (!selectedTarget.CanSelect ||
                    !KuaishouAuditTimeoutResubmit.TryReadTarget(row, out var target) || target is null ||
                    !target.CanSelect || target.Eligibility != selectedTarget.Eligibility ||
                    !target.EpisodeIds.ToHashSet(StringComparer.Ordinal).SetEquals(selectedTarget.EpisodeIds))
                {
                    Report(KuaishouAuditTimeoutSubmitStatus.Skipped, "审核状态或原因已变化，请重新查询。");
                    continue;
                }

                if (target.Eligibility == KuaishouAuditTimeoutEligibility.MixedSummary && targets.Length != 1)
                {
                    Report(KuaishouAuditTimeoutSubmitStatus.Skipped, "摘要混合提示的剧集请单独选择一部重提。");
                    continue;
                }

                progress?.Report($"正在核对《{target.Title}》的现有资料。");
                using var info = await PostAsync(gateway, KuaishouCommitmentResubmit.BaseInfoUrl,
                    KuaishouCommitmentResubmit.SeriesIdBody(target.SeriesId), ct).ConfigureAwait(false);
                using var episodes = await PostAsync(gateway, KuaishouCommitmentResubmit.EpisodeListUrl,
                    KuaishouCommitmentResubmit.EpisodeListBody(target.SeriesId), ct).ConfigureAwait(false);
                if (!KuaishouAuditTimeoutResubmit.CanSaveOriginal(info.RootElement, episodes.RootElement, target))
                {
                    Report(KuaishouAuditTimeoutSubmitStatus.Skipped, "资料或编辑权限核验未通过，请查看平台详情。");
                    continue;
                }

                saveContext ??= await KuaishouCommitmentResubmitService.ReadContextAsync(gateway, ct).ConfigureAwait(false);
                using var updated = await PostAsync(gateway, KuaishouCommitmentResubmit.UpdateUrl,
                    KuaishouCommitmentResubmit.UpdateBody(info.RootElement, episodes.RootElement, saveContext.Value,
                        target.SeriesId), ct).ConfigureAwait(false);
                if (!KuaishouCommitmentResubmit.CanModifyVideo(updated.RootElement))
                {
                    Report(KuaishouAuditTimeoutSubmitStatus.Failed, "原资料已保存，平台未开放重新提审。");
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                SetPending(target.SeriesId, true);
                startedSubmit = true;
                progress?.Report($"正在重新提审《{target.Title}》。");
                try
                {
                    using var submitted = await PostAsync(gateway, KuaishouApiVideoUploader.SubmitUrl,
                        KuaishouCommitmentResubmit.SubmitBody(target.SeriesId), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (KuaishouDailyUploadLimit.Is(ex))
                {
                    SetPending(target.SeriesId, false);
                    throw;
                }
                catch (InvalidOperationException ex)
                {
                    // The API gateway uses InvalidOperationException for explicit HTTP and business rejection.
                    submitRejection = ex;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TimeoutException ||
                    (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    progress?.Report($"《{target.Title}》提交响应不确定，正在查询平台状态。");
                }

                if (await ConfirmAfterSubmitAsync(gateway, target.SeriesId, ct,
                    attempts: submitRejection is null ? 3 : 1).ConfigureAwait(false))
                {
                    SetPending(target.SeriesId, false);
                    Report(KuaishouAuditTimeoutSubmitStatus.Accepted, "已进入审核或已审核通过。");
                }
                else if (submitRejection is not null)
                {
                    SetPending(target.SeriesId, false);
                    Report(KuaishouAuditTimeoutSubmitStatus.Failed, $"平台拒绝重提：{submitRejection.Message}");
                }
                else
                {
                    Report(KuaishouAuditTimeoutSubmitStatus.PendingConfirmation, "提交结果待确认，已保留请求记录。");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (startedSubmit)
                    Report(KuaishouAuditTimeoutSubmitStatus.PendingConfirmation, "停止时提交结果尚未确认。");
                break;
            }
            catch (Exception ex) when (KuaishouDailyUploadLimit.Is(ex))
            {
                Report(KuaishouAuditTimeoutSubmitStatus.Failed, "平台达到当日限制，已停止后续重提。");
                break;
            }
            catch (Exception ex)
            {
                Report(startedSubmit ? KuaishouAuditTimeoutSubmitStatus.PendingConfirmation : KuaishouAuditTimeoutSubmitStatus.Failed,
                    $"重提发生异常：{ex.Message}");
            }
        }

        return result;
    }

    private static async Task<bool> ConfirmAfterSubmitAsync(
        IKuaishouKdjGateway gateway, string seriesId, CancellationToken ct, int attempts)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            if (await TryCheckAcceptedAsync(gateway, seriesId, ct).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    private static async Task<bool> TryCheckAcceptedAsync(
        IKuaishouKdjGateway gateway, string seriesId, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var doc = await ReadStatusAsync(gateway, seriesId, timeout.Token).ConfigureAwait(false);
            return KuaishouShelfApi.TryReadStatus(doc.RootElement, seriesId, out var auditStatus, out _) &&
                   auditStatus is KuaishouDraftReplace.ReviewingAuditStatus or KuaishouDraftReplace.PublishedAuditStatus;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException or TimeoutException))
        {
            return false;
        }
    }

    private static JsonElement FindRow(JsonElement root, string seriesId) =>
        KuaishouAuditTimeoutResubmit.Items(KuaishouAuditTimeoutResubmit.Property(
                KuaishouAuditTimeoutResubmit.Property(root, "data"), "data"))
            .FirstOrDefault(row => KuaishouAuditTimeoutResubmit.Text(row, "miniSeriesId") == seriesId);

    private static Task<JsonDocument> ReadStatusAsync(IKuaishouKdjGateway gateway, string seriesId, CancellationToken ct) =>
        PostAsync(gateway, KuaishouShelfApi.ListUrl, KuaishouShelfApi.StatusBody(seriesId), ct);

    private static async Task<JsonDocument> PostAsync(
        IKuaishouKdjGateway gateway, string url, string payload, CancellationToken ct)
    {
        var raw = await gateway.PostJsonAsync(url, payload, ct).ConfigureAwait(false);
        var document = JsonDocument.Parse(raw);
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("快手接口返回的响应不是对象。");
            if (url == KuaishouApiVideoUploader.SubmitUrl && !KuaishouSeriesDraft.IsSuccess(document.RootElement) &&
                KuaishouAuditTimeoutResubmit.ReadNumber(document.RootElement, "result") == -1)
                throw new JsonException("快手提审响应缺少有效结果码。");

            KuaishouSeriesDraft.EnsureSuccess(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private void SetPending(string seriesId, bool pending)
    {
        var next = _pending.ToHashSet(StringComparer.Ordinal);
        if (!(pending ? next.Add(seriesId) : next.Remove(seriesId)))
            return;
        _settings.Save(_accountId, PendingKey, next.ToArray());
        _pending = next;
    }
}
