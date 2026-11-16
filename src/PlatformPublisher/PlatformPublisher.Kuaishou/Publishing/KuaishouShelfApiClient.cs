using System.Text.Json;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouShelfApiClient
{
    public async Task<(IReadOnlyList<KuaishouShelfApi.Candidate> Rows, int Raw)> ListOfflinePageAsync(
        IKuaishouKdjGateway gateway,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var text = await gateway.PostJsonAsync(
            KuaishouShelfApi.ListUrl,
            KuaishouShelfApi.OfflineListBody(page, pageSize),
            cancellationToken);
        using var body = JsonDocument.Parse(text);
        KuaishouSeriesDraft.EnsureSuccess(body.RootElement);
        var rows = new List<KuaishouShelfApi.Candidate>();
        var raw = KuaishouShelfApi.ReadOfflinePage(body.RootElement, rows, out _);
        return (rows, raw);
    }

    public async Task<(int AuditStatus, int SellingStatus)?> ReadStatusAsync(
        IKuaishouKdjGateway gateway,
        string seriesId,
        CancellationToken cancellationToken)
    {
        var text = await gateway.PostJsonAsync(
            KuaishouShelfApi.ListUrl,
            KuaishouShelfApi.StatusBody(seriesId),
            cancellationToken);
        using var body = JsonDocument.Parse(text);
        KuaishouSeriesDraft.EnsureSuccess(body.RootElement);
        return KuaishouShelfApi.TryReadStatus(body.RootElement, seriesId, out var audit, out var selling)
            ? (audit, selling)
            : null;
    }

    public async Task SetOnlineAsync(
        IKuaishouKdjGateway gateway,
        string seriesId,
        CancellationToken cancellationToken)
    {
        var text = await gateway.PostJsonAsync(
            KuaishouShelfApi.OnlineOfflineUrl,
            KuaishouShelfApi.OnlineBody(seriesId),
            cancellationToken);
        using var body = JsonDocument.Parse(text);
        KuaishouSeriesDraft.EnsureSuccess(body.RootElement);
    }
}
