using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Notifications;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Notifications;

public sealed class FeishuNotificationServiceTests
{
    [Fact]
    public async Task SendTextAsync_Should_Request_Token_And_Post_Message()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson("""{"code":0,"tenant_access_token":"token-1","expire":7200}""");
        handler.EnqueueJson("""{"code":0,"data":{"message_id":"om_xxx"}}""");

        var service = new FeishuNotificationService(new HttpClient(handler));
        var settings = new FeishuNotificationSettings(
            Enabled: true,
            AppId: "cli_a",
            AppSecret: "sec_b",
            ReceiveId: "oc_group",
            ReceiveIdType: "chat_id",
            NotifyOnStepStart: false,
            NotifyOnStepSuccess: true,
            NotifyOnStepFailure: true,
            NotifyOnQueueSummary: true,
            StepKeysText: "download");

        await service.SendTextAsync(settings, "标题", "正文", CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/open-apis/auth/v3/tenant_access_token/internal");
        handler.Requests[1].RequestUri!.Query.Should().Contain("receive_id_type=chat_id");

        var requestBody = handler.RequestBodies[1];
        using var document = JsonDocument.Parse(requestBody);
        document.RootElement.GetProperty("receive_id").GetString().Should().Be("oc_group");
        document.RootElement.GetProperty("msg_type").GetString().Should().Be("text");
        var contentJson = document.RootElement.GetProperty("content").GetString();
        contentJson.Should().NotBeNullOrWhiteSpace();
        using var contentDocument = JsonDocument.Parse(contentJson!);
        contentDocument.RootElement.GetProperty("text").GetString().Should().Contain("标题");
        contentDocument.RootElement.GetProperty("text").GetString().Should().Contain("正文");
    }

    [Fact]
    public async Task SendTextAsync_Should_Reuse_Cached_Token()
    {
        var handler = new RecordingHandler();
        handler.EnqueueJson("""{"code":0,"tenant_access_token":"token-1","expire":7200}""");
        handler.EnqueueJson("""{"code":0,"data":{"message_id":"om_1"}}""");
        handler.EnqueueJson("""{"code":0,"data":{"message_id":"om_2"}}""");

        var service = new FeishuNotificationService(new HttpClient(handler));
        var settings = new FeishuNotificationSettings(
            Enabled: true,
            AppId: "cli_a",
            AppSecret: "sec_b",
            ReceiveId: "oc_group",
            ReceiveIdType: "chat_id",
            NotifyOnStepStart: false,
            NotifyOnStepSuccess: true,
            NotifyOnStepFailure: true,
            NotifyOnQueueSummary: true,
            StepKeysText: "download");

        await service.SendTextAsync(settings, "标题1", "正文1", CancellationToken.None);
        await service.SendTextAsync(settings, "标题2", "正文2", CancellationToken.None);

        handler.Requests.Count(request => request.RequestUri!.AbsolutePath.Contains("tenant_access_token")).Should().Be(1);
        handler.Requests.Count(request => request.RequestUri!.AbsolutePath.Contains("/open-apis/im/v1/messages")).Should().Be(2);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        public void EnqueueJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responses.Enqueue(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.Dequeue();
        }
    }
}
