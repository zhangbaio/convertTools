using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ShortDrama.Core.Interfaces;
using ShortDrama.Infrastructure.Automation;
using ShortDrama.Infrastructure.DependencyInjection;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.DependencyInjection;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddShortDramaServices_Should_Use_CSharp_Weixin_Uploader()
    {
        var services = new ServiceCollection();

        services.AddShortDramaServices();

        using var provider = services.BuildServiceProvider();
        var uploader = provider.GetRequiredService<IWeixinChannelUploader>();

        uploader.Should().BeOfType<WeixinChannelUploader>();
    }
}
