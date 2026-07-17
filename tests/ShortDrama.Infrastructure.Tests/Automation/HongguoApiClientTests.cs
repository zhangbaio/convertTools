using FluentAssertions;
using ShortDrama.Infrastructure.Automation;
using ShortDrama.Infrastructure.Config;
using System.Net;
using System.Text;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class HongguoApiClientTests
{
    [Fact]
    public async Task SearchAsync_RetriesWhenServerClosesTransportPrematurely()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                throw new HttpRequestException(
                    "The SSL connection could not be established.",
                    new IOException("Received an unexpected EOF or 0 bytes from the transport stream."));
            }

            return JsonResponse("""{"code":200,"msg":"ok","data":[{"book_id":"123"}]}""");
        }));
        var client = new HongguoApiClient(httpClient, new HongguoAccessOptions(null, "machine"));

        var result = await client.SearchAsync("球球请深爱", 1, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].GetProperty("book_id").GetString().Should().Be("123");
        requestCount.Should().Be(2);
    }

    [Fact]
    public async Task SearchAsync_DoesNotRetryNonTransientClientError()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }));
        var client = new HongguoApiClient(httpClient, new HongguoAccessOptions(null, "machine"));

        var action = () => client.SearchAsync("测试", 1, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*HTTP 400*");
        requestCount.Should().Be(1);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                return Task.FromResult(handle(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }
}
