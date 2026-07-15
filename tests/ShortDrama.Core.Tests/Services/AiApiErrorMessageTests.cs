using System.Net;
using FluentAssertions;
using ShortDrama.Core.Services;
using Xunit;

namespace ShortDrama.Core.Tests.Services;

public sealed class AiApiErrorMessageTests
{
    [Fact]
    public void Create_Should_Return_Friendly_Message_For_AccountOverdueError()
    {
        var body = """
            {"error":{"code":"AccountOverdueError","message":"The request failed because your account has an overdue balance."},"request_id":"req-123"}
            """;

        var message = AiApiErrorMessage.Create("AI 海报标题检测接口", HttpStatusCode.Forbidden, "Forbidden", body);

        message.Should().Contain("AI 海报标题检测接口失败");
        message.Should().Contain("AI 账号余额不足或已欠费");
        message.Should().Contain("充值/续费");
        message.Should().Contain("req-123");
        message.Should().Contain("HTTP 403 Forbidden");
        message.Should().NotContain("AccountOverdueError");
    }

    [Fact]
    public void Create_Should_Keep_Generic_Http_Message_For_Other_Failures()
    {
        var message = AiApiErrorMessage.Create(
            "AI 改写接口",
            HttpStatusCode.InternalServerError,
            "Internal Server Error",
            "{\"error\":\"temporary\"}");

        message.Should().Contain("AI 改写接口失败");
        message.Should().Contain("HTTP 500 Internal Server Error");
        message.Should().Contain("temporary");
    }
}
