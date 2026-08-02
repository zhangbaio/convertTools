using System.Net;
using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Desktop;

public sealed class HongguoNewApiServiceTests
{
    [Fact]
    public void HongguoDeviceId_Normalize_Uppercases_Guid()
    {
        HongguoDeviceId.Normalize(" 64437e32-40bb-440c-8300-99232d63e8f7 ")
            .Should().Be("64437E32-40BB-440C-8300-99232D63E8F7");
        HongguoDeviceId.LooksLikeGuid("64437E32-40BB-440C-8300-99232D63E8F7")
            .Should().BeTrue();
        HongguoDeviceId.LooksLikeGuid("42ce0f9242ea893b241749e35cf894be")
            .Should().BeFalse();
    }

    [Fact]
    public void HongguoClientVersion_Allows_Only_14x()
    {
        HongguoClientVersion.Default.Should().Be("1.4.2");
        HongguoClientVersion.Normalize("1.4.1").Should().Be("1.4.1");
        HongguoClientVersion.Normalize("1.4.2").Should().Be("1.4.2");
        HongguoClientVersion.Normalize("1.3.9").Should().Be("1.4.2");
        HongguoClientVersion.Normalize("1.5.0").Should().Be("1.4.2");
        HongguoClientVersion.BuildAesBaseUrl("1.4.2")
            .Should().Be("https://au.s1o.cc/api/user/1000/win/1.4.2");
    }

    [Fact]
    public async Task SearchAsync_Uses_Aes_Host_And_Normalized_Guid()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var service = new HongguoNewApiService(client);
        var settings = new DramaSourceSettings
        {
            HgnewAccount = "test@example.com",
            HgnewPassword = "secret",
            HgnewUdid = "64437e32-40bb-440c-8300-99232d63e8f7",
            HgnewClientVersion = "1.4.2"
        };

        var act = () => service.SearchAsync(settings, "test", 1, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.Host.Should().Be("au.s1o.cc");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Contain("/win/1.4.2/");
        handler.Requests[0].Headers.GetValues("X-Client-Version").Single().Should().Be("1.4.2");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0,\"data\":\"invalid\"}")
            });
        }
    }
}
