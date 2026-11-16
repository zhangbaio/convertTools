using System.Text.Json;
using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouShelfApiTests
{
    [Fact]
    public void Offline_published_series_with_an_enabled_shelf_button_is_selected()
    {
        using var document = JsonDocument.Parse(Page(
            Row("11", "可上架", selling: 2, audit: 3, canOnline: true),
            Row("22", "风控下架", selling: 2, audit: 3, canOnline: false, reason: "平台风控下架"),
            Row("33", "审核中", selling: 2, audit: 2, canOnline: true)));
        var rows = new List<KuaishouShelfApi.Candidate>();
        var raw = KuaishouShelfApi.ReadOfflinePage(document.RootElement, rows, out var total);

        Assert.Equal(3, raw);
        Assert.Equal(3, total);
        Assert.Equal("11", Assert.Single(rows, row => row.CanShelf).SeriesId);
        Assert.Contains(rows, row => row.SeriesId == "22" && row.SkipReason == "平台风控");
        Assert.Contains(rows, row => row.SeriesId == "33" && !row.CanShelf);
    }

    [Fact]
    public async Task Api_shelf_posts_online_only_for_the_enabled_series()
    {
        var gateway = new ShelfGateway(_ =>
            """{"result":1,"data":{"totalCount":2,"data":[{"miniSeriesId":11,"courseName":"可上架","sellingStatus":2,"auditStatus":3,"canOnlineOffline":true},{"miniSeriesId":22,"courseName":"不能上架","sellingStatus":2,"auditStatus":3,"canOnlineOffline":false}]}}""");
        var client = new KuaishouShelfApiClient();
        var (rows, raw) = await client.ListOfflinePageAsync(gateway, 1, 20, CancellationToken.None);
        Assert.Equal(2, raw);
        await client.SetOnlineAsync(gateway, Assert.Single(rows, row => row.CanShelf).SeriesId, CancellationToken.None);

        Assert.Contains("\"sellingStatus\":2", gateway.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"auditStatus\":3", gateway.Bodies[0], StringComparison.Ordinal);
        Assert.Equal("{\"miniSeriesId\":11,\"online\":true}", gateway.Bodies[1]);
    }

    [Fact]
    public async Task Status_query_reads_the_matching_series()
    {
        var gateway = new ShelfGateway(_ =>
            """{"result":1,"data":{"totalCount":1,"data":[{"miniSeriesId":12345,"auditStatus":3,"sellingStatus":2}]}}""");
        var status = await new KuaishouShelfApiClient().ReadStatusAsync(gateway, "12345", CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(3, status.Value.AuditStatus);
        Assert.Equal(2, status.Value.SellingStatus);
        Assert.Contains("12345", gateway.Bodies[0], StringComparison.Ordinal);
    }

    private static string Page(params string[] rows) =>
        "{\"result\":1,\"data\":{\"totalCount\":" + rows.Length + ",\"data\":[" + string.Join(",", rows) + "]}}";

    private static string Row(string id, string title, int selling, int audit, bool canOnline, string reason = "") =>
        "{\"miniSeriesId\":" + id +
        ",\"courseName\":\"" + title +
        "\",\"sellingStatus\":" + selling +
        ",\"auditStatus\":" + audit +
        ",\"canOnlineOffline\":" + (canOnline ? "true" : "false") +
        ",\"onlineDisableReason\":\"" + reason + "\"}";

    private sealed class ShelfGateway(Func<string, string> respond) : IKuaishouKdjGateway
    {
        public List<string> Bodies { get; } = [];

        public Task<string> PostJsonAsync(string url, string body, CancellationToken cancellationToken)
        {
            Bodies.Add(body);
            return Task.FromResult(respond(body));
        }

        public Task<string> UploadFileAsync(string url, string path, string label, int byteLimit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
