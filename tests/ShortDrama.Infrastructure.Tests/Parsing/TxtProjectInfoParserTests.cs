using FluentAssertions;
using ShortDrama.Infrastructure.Parsing;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Parsing;

public sealed class TxtProjectInfoParserTests
{
    [Fact]
    public async Task ParseAsync_Should_Parse_Current_Format()
    {
        var parser = new TxtProjectInfoParser();

        var tempDir = Directory.CreateTempSubdirectory();
        var filePath = Path.Combine(tempDir.FullName, "短剧信息.txt");

        await File.WriteAllTextAsync(filePath, """
原剧名: 末世乐园之繁殖
新剧名: 末世禁区求生局
推荐语: 绝境求生，爽感拉满
简介: 废土之上规则破碎，隐藏危机层层铺开。这场绝命博弈，究竟谁能活到最后？
时长: 8 分钟
集数: 3
成本: 1 万元
制作公司: 湖北云漫科技有限公司
""");

        var result = await parser.ParseAsync(tempDir.FullName, CancellationToken.None);

        result.OriginalTitle.Should().Be("末世乐园之繁殖");
        result.Title.Should().Be("末世禁区求生局");
        result.EpisodeCount.Should().Be(3);
        result.TotalMinutes.Should().Be(8);
        result.CostAmountWan.Should().Be(1m);
        result.CompanyName.Should().Be("湖北云漫科技有限公司");
    }

    [Fact]
    public async Task ParseAsync_Should_Reject_Cost_Over_Or_Equal_100Wan()
    {
        var parser = new TxtProjectInfoParser();

        var tempDir = Directory.CreateTempSubdirectory();
        var filePath = Path.Combine(tempDir.FullName, "短剧信息.txt");

        await File.WriteAllTextAsync(filePath, """
原剧名: A
新剧名: B
时长: 8 分钟
集数: 3
成本: 100 万元
制作公司: 测试公司
""");

        var action = async () => await parser.ParseAsync(tempDir.FullName, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*小于100万元*");
    }
}
