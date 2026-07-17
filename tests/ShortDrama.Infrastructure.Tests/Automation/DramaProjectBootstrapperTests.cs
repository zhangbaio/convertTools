using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using System.Text.Json;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class DramaProjectBootstrapperTests
{
    [Fact]
    public async Task BootstrapAsync_Should_Create_Project_Metadata_Without_ProjectInfo()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), $"shortdrama-bootstrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootDir);

        try
        {
            var bootstrapper = new DramaProjectBootstrapper();
            var result = await bootstrapper.BootstrapAsync(
                new DramaProjectBootstrapRequest(
                    RootDir: rootDir,
                    Drama: new DramaSearchItem(
                        BookId: "bk123",
                        Title: "离婚后她杀疯了",
                        Category: "逆袭",
                        EpisodeTotal: 80,
                        Intro: "简介示例",
                        PosterUrl: "https://example.com/poster.jpg"),
                    CompanyName: "测试公司"),
                CancellationToken.None);

            result.Created.Should().BeTrue();
            result.ProjectKey.Should().Be("离婚后她杀疯了");
            result.SourceProjectDir.Should().Be(Path.Combine(rootDir, "离婚后她杀疯了"));

            var metadataPath = Path.Combine(result.SourceProjectDir, "shortdrama-project.json");
            File.Exists(metadataPath).Should().BeTrue();
            File.Exists(Path.Combine(result.SourceProjectDir, "短剧信息.txt")).Should().BeFalse();

            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
            metadata.RootElement.GetProperty("bookId").GetString().Should().Be("bk123");
            metadata.RootElement.GetProperty("projectKey").GetString().Should().Be("离婚后她杀疯了");
            metadata.RootElement.GetProperty("sourceName").GetString().Should().Be("离婚后她杀疯了");
            metadata.RootElement.GetProperty("displayName").GetString().Should().Be("离婚后她杀疯了");
            metadata.RootElement.GetProperty("title").GetString().Should().Be("离婚后她杀疯了");
            metadata.RootElement.GetProperty("episodeCount").GetInt32().Should().Be(80);
            metadata.RootElement.GetProperty("posterUrl").GetString().Should().Be("https://example.com/poster.jpg");
            metadata.RootElement.GetProperty("workflowDirName").GetString().Should().Be("_离婚后她杀疯了");
            metadata.RootElement.GetProperty("workflowProjectDir").GetString().Should().Be(
                Path.Combine(rootDir, "workflow", "_离婚后她杀疯了"));
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }
}
