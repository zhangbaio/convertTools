using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using System.Text.Json;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Automation;

public sealed class DramaProjectBootstrapperTests
{
    [Fact]
    public async Task BootstrapAsync_creates_a_separate_directory_for_the_same_title_from_another_source()
    {
        var root = Directory.CreateTempSubdirectory("shortdrama-bootstrap-sources-");
        try
        {
            var bootstrapper = new DramaProjectBootstrapper();
            var request = new DramaProjectBootstrapRequest(
                RootDir: root.FullName,
                Drama: new DramaSearchItem("dramabox:123", "测试剧", "", 70, "", ""),
                CompanyName: null);
            var first = await bootstrapper.BootstrapAsync(request, CancellationToken.None);
            var second = await bootstrapper.BootstrapAsync(
                request with { Drama = request.Drama with { BookId = "foreign:123" } }, CancellationToken.None);

            second.SourceProjectDir.Should().NotBe(first.SourceProjectDir);
            second.ProjectKey.IndexOfAny(Path.GetInvalidFileNameChars()).Should().Be(-1);
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(second.SourceProjectDir, "shortdrama-project.json")));
            metadata.RootElement.GetProperty("bookId").GetString().Should().Be("foreign:123");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BootstrapAsync_leaves_existing_metadata_when_project_guard_rejects_the_target()
    {
        var root = Directory.CreateTempSubdirectory("shortdrama-bootstrap-guard-");
        try
        {
            var project = root.CreateSubdirectory("测试剧");
            var metadataPath = Path.Combine(project.FullName, "shortdrama-project.json");
            const string existingMetadata = """{"bookId":"dramabox:123","intro":"已保存简介"}""";
            await File.WriteAllTextAsync(metadataPath, existingMetadata);
            var request = new DramaProjectBootstrapRequest(
                RootDir: root.FullName,
                Drama: new DramaSearchItem("dramabox:123", "测试剧", "", 70, "新简介", ""),
                CompanyName: null);

            var bootstrap = () => new DramaProjectBootstrapper().BootstrapAsync(request, CancellationToken.None, dir =>
            {
                dir.Should().Be(project.FullName);
                throw new InvalidOperationException("项目属于其它账号");
            });

            await bootstrap.Should().ThrowAsync<InvalidOperationException>().WithMessage("项目属于其它账号");
            (await File.ReadAllTextAsync(metadataPath)).Should().Be(existingMetadata);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

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

    [Fact]
    public async Task BootstrapAsync_Should_Preserve_Renamed_Workflow_When_Project_Already_Exists()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), $"shortdrama-bootstrap-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(rootDir, "真千金偏要又争又抢");
        var renamedWorkflowDir = Path.Combine(rootDir, "workflow", "_真千金归来夺回一切");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(renamedWorkflowDir);

        try
        {
            var metadataPath = Path.Combine(sourceDir, "shortdrama-project.json");
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(new
            {
                projectKey = "真千金偏要又争又抢",
                bookId = "bk-renamed",
                originalTitle = "真千金偏要又争又抢",
                newTitle = "真千金归来夺回一切",
                new_title = "真千金归来夺回一切",
                workflowDirName = "_真千金归来夺回一切",
                workflowProjectDir = renamedWorkflowDir,
                sourceProjectDir = sourceDir,
                createdAt = "2026-07-02T15:04:56+08:00",
                laterWorkflowField = "must-survive"
            }));

            var result = await new DramaProjectBootstrapper().BootstrapAsync(
                new DramaProjectBootstrapRequest(
                    RootDir: rootDir,
                    Drama: new DramaSearchItem(
                        BookId: "bk-renamed",
                        Title: "真千金偏要又争又抢",
                        Category: "都市",
                        EpisodeTotal: 76,
                        Intro: "更新后的简介",
                        PosterUrl: "https://example.com/new.jpg"),
                    CompanyName: null,
                    Episodes: "11"),
                CancellationToken.None);

            result.Created.Should().BeFalse();
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
            metadata.RootElement.GetProperty("workflowDirName").GetString().Should().Be("_真千金归来夺回一切");
            metadata.RootElement.GetProperty("workflowProjectDir").GetString().Should().Be(renamedWorkflowDir);
            metadata.RootElement.GetProperty("newTitle").GetString().Should().Be("真千金归来夺回一切");
            metadata.RootElement.GetProperty("new_title").GetString().Should().Be("真千金归来夺回一切");
            metadata.RootElement.GetProperty("createdAt").GetString().Should().Be("2026-07-02T15:04:56+08:00");
            metadata.RootElement.GetProperty("laterWorkflowField").GetString().Should().Be("must-survive");
            metadata.RootElement.GetProperty("episodes").GetString().Should().Be("11");
            metadata.RootElement.GetProperty("intro").GetString().Should().Be("更新后的简介");
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
