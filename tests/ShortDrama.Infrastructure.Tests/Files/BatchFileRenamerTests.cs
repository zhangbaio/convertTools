using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShortDrama.Infrastructure.Files;
using ShortDrama.Infrastructure.Parsing;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Files;

public sealed class BatchFileRenamerTests
{
    [Fact]
    public async Task RenameAsync_Should_Use_Default_Template_And_Rename_In_Sorted_Order()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var videosDir = Directory.CreateDirectory(Path.Combine(projectDir, "videos")).FullName;

        await File.WriteAllTextAsync(Path.Combine(projectDir, "短剧信息.txt"), """
原剧名: 原剧名
新剧名: 末世禁区求生局
时长: 8 分钟
集数: 3
成本: 1 万元
制作公司: 湖北云漫科技有限公司
""");

        await File.WriteAllBytesAsync(Path.Combine(videosDir, "b.mp4"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(videosDir, "a.mp4"), [1]);

        var renamer = new BatchFileRenamer(
            new TxtProjectInfoParser(),
            NullLogger<BatchFileRenamer>.Instance);

        var result = await renamer.RenameAsync(
            new(
                projectDir,
                videosDir),
            CancellationToken.None);

        result.RenamedCount.Should().Be(2);
        result.Items.Select(item => Path.GetFileName(item.OutputFilePath)).Should().ContainInOrder(
            "末世禁区求生局-第1集.mp4",
            "末世禁区求生局-第2集.mp4");

        File.Exists(Path.Combine(videosDir, "末世禁区求生局-第1集.mp4")).Should().BeTrue();
        File.Exists(Path.Combine(videosDir, "末世禁区求生局-第2集.mp4")).Should().BeTrue();
    }

    [Fact]
    public async Task RenameAsync_Should_Read_VideoNameTemplate_From_Config()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        var videosDir = Directory.CreateDirectory(Path.Combine(projectDir, "videos")).FullName;
        var configPath = Path.Combine(projectDir, "config.txt");

        await File.WriteAllTextAsync(Path.Combine(projectDir, "短剧信息.txt"), """
原剧名: 原剧名
新剧名: 末世禁区求生局
时长: 8 分钟
集数: 3
成本: 1 万元
制作公司: 湖北云漫科技有限公司
""");

        await File.WriteAllTextAsync(configPath, "VideoNameTemplate={index}-{name}");
        await File.WriteAllBytesAsync(Path.Combine(videosDir, "episode1.mov"), [1]);

        var renamer = new BatchFileRenamer(
            new TxtProjectInfoParser(),
            NullLogger<BatchFileRenamer>.Instance);

        var result = await renamer.RenameAsync(
            new(
                projectDir,
                videosDir,
                configPath),
            CancellationToken.None);

        result.RenamedCount.Should().Be(1);
        result.Items.Should().ContainSingle();
        Path.GetFileName(result.Items[0].OutputFilePath).Should().Be("1-末世禁区求生局.mov");
        File.Exists(Path.Combine(videosDir, "1-末世禁区求生局.mov")).Should().BeTrue();
    }
}
