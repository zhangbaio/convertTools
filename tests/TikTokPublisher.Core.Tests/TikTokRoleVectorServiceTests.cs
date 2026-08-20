using System.Text.Json;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokRoleVectorServiceTests
{
    [Fact]
    public async Task GenerateAsync_ReusesCharactersCreatesBackupAndMigratesLegacyState()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"role-vector-step-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "原剧名");
        var workflow = Path.Combine(workspace, "workflow", "_新剧名");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        WriteMetadata(source, source, workflow);
        WriteMetadata(workflow, source, workflow);

        var packageRoot = TikTokReferenceSourcePackageService.GetRoot(workflow);
        var characterDir = Path.Combine(packageRoot, TikTokReferenceSourcePackageService.CharacterDirectoryName);
        Directory.CreateDirectory(characterDir);
        for (var index = 1; index <= 3; index++)
            SaveImage(Path.Combine(characterDir, $"角色{index}.png"), 128, 192, new Rgba32((byte)(index * 50), 80, 100));

        var sceneDir = Path.Combine(workflow, "AI漫剧制作素材", "02_场景设定");
        Directory.CreateDirectory(sceneDir);
        for (var index = 1; index <= 4; index++)
            SaveImage(Path.Combine(sceneDir, $"场景设定_{index:D2}.jpg"), 320, 180, new Rgba32(40, (byte)(index * 40), 100));

        var output = TikTokRoleVectorService.GetOutputPath(workflow);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        SaveImage(output, RoleVectorTemplateRenderer.CanvasWidth, RoleVectorTemplateRenderer.CanvasHeight, new Rgba32(180, 10, 10));
        var item = new QueueProjectItem { ProjectDir = source, NewTitle = "新剧名" };
        var logs = new List<string>();

        try
        {
            var result = await TikTokRoleVectorService.GenerateAsync(
                item,
                new ClientSettings(),
                TikTokAccountProfile.DefaultRoleVectorCharacterCount,
                forceRerun: true,
                logs.Add,
                CancellationToken.None);

            result.Should().Be(output);
            TikTokRoleVectorService.HasCurrentOutput(workflow).Should().BeTrue();
            TikTokRoleVectorService.HasCurrentOutput(workflow, 3).Should().BeTrue();
            TikTokRoleVectorService.HasCurrentOutput(workflow, 4)
                .Should().BeFalse("账号配置人数变化后状态指纹应失效");
            File.Exists(Path.Combine(packageRoot, TikTokRoleVectorService.BackupFileName)).Should().BeTrue();
            File.Exists(TikTokRoleVectorService.GetStatePath(workflow)).Should().BeTrue();
            File.Exists(TikTokReferenceSourcePackageService.GetCharacterManifestPath(workflow)).Should().BeTrue();
            logs.Should().Contain(message => message.Contains("不调用图片模型", StringComparison.Ordinal));
            logs.Should().Contain(message => message.Contains("三人居中模板", StringComparison.Ordinal));

            var legacy = new QueueProjectItem { ProjectDir = source };
            legacy.NormalizeStepStates();
            legacy.StepStates[QueueStepKeys.GenerateRoleVector].Should().Be(QueueStepStatus.Completed);

            SaveImage(Path.Combine(characterDir, "角色1.png"), 128, 192, new Rgba32(250, 10, 10));
            TikTokRoleVectorService.HasCurrentOutput(workflow)
                .Should().BeFalse("参与生成的角色图片变化后状态指纹应失效");
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void NormalizeStepStates_LeavesMissingRoleVectorPending()
    {
        var item = new QueueProjectItem { ProjectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) };

        item.NormalizeStepStates();

        item.StepStates[QueueStepKeys.GenerateRoleVector].Should().Be(QueueStepStatus.Pending);
    }

    [Fact]
    public async Task EnsureCharacterImagesAsync_LimitsExistingDirectoryToSixAndWritesManifest()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"role-vector-character-limit-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "原剧名");
        var workflow = Path.Combine(workspace, "workflow", "_新剧名");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        WriteMetadata(source, source, workflow);
        WriteMetadata(workflow, source, workflow);
        var characterDir = Path.Combine(
            TikTokReferenceSourcePackageService.GetRoot(workflow),
            TikTokReferenceSourcePackageService.CharacterDirectoryName);
        Directory.CreateDirectory(characterDir);
        for (var index = 1; index <= 7; index++)
            SaveImage(Path.Combine(characterDir, $"角色{index}.png"), 64, 96, new Rgba32((byte)(index * 25), 70, 90));
        var logs = new List<string>();

        try
        {
            var selected = await TikTokReferenceSourcePackageService.EnsureCharacterImagesAsync(
                new QueueProjectItem { ProjectDir = source },
                new ClientSettings(),
                6,
                logs.Add,
                CancellationToken.None);

            selected.Should().HaveCount(6);
            Directory.EnumerateFiles(characterDir, "*.png").Should().HaveCount(7, "多余角色图片应保留");
            logs.Should().Contain(message => message.Contains("限制为 6 人", StringComparison.Ordinal));
            using var manifest = JsonDocument.Parse(File.ReadAllText(
                TikTokReferenceSourcePackageService.GetCharacterManifestPath(workflow)));
            manifest.RootElement.GetProperty("characterCount").GetInt32().Should().Be(6);
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureCharacterImagesAsync_PrefersEpisodeCharacterSourcesOverOldGeneratedActors()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"role-vector-episode-characters-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "原剧名");
        var workflow = Path.Combine(workspace, "workflow", "_新剧名");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        WriteMetadata(source, source, workflow);
        WriteMetadata(workflow, source, workflow);

        var packageRoot = TikTokReferenceSourcePackageService.GetRoot(workflow);
        var characterDir = Path.Combine(packageRoot, TikTokReferenceSourcePackageService.CharacterDirectoryName);
        Directory.CreateDirectory(characterDir);
        for (var index = 1; index <= 3; index++)
            SaveImage(Path.Combine(characterDir, $"旧角色{index}.png"), 64, 96, new Rgba32(230, 20, 20));

        var episodeCharacterDir = Path.Combine(
            TikTokSourceFileInfoScreenshotService.GetEvidenceDirectory(workflow),
            TikTokAiDramaProductionMaterialService.OutputDirectoryName,
            TikTokAiDramaProductionMaterialService.CharacterDirectoryName);
        Directory.CreateDirectory(episodeCharacterDir);
        var expected = new[]
        {
            new Rgba32(20, 210, 40),
            new Rgba32(30, 50, 220),
            new Rgba32(220, 180, 30),
        };
        for (var index = 0; index < expected.Length; index++)
            SaveImage(Path.Combine(episodeCharacterDir, $"角色设定_{index + 1:D2}.jpg"), 128, 128, expected[index]);

        var logs = new List<string>();
        try
        {
            var selected = await TikTokReferenceSourcePackageService.EnsureCharacterImagesAsync(
                new QueueProjectItem { ProjectDir = source },
                new ClientSettings(),
                3,
                logs.Add,
                CancellationToken.None);

            selected.Should().HaveCount(3);
            Directory.EnumerateFiles(characterDir, "*.png").Should().HaveCount(3);
            logs.Should().Contain(message => message.Contains("剧集真实角色素材", StringComparison.Ordinal));
            var actualPixels = new List<Rgba32>();
            for (var index = 0; index < selected.Count; index++)
            {
                using var image = Image.Load<Rgba32>(selected[index]);
                actualPixels.Add(image[image.Width / 2, image.Height / 2]);
            }
            foreach (var expectedPixel in expected)
                actualPixels.Should().Contain(pixel =>
                    Math.Abs(pixel.R - expectedPixel.R) <= 4 &&
                    Math.Abs(pixel.G - expectedPixel.G) <= 4 &&
                    Math.Abs(pixel.B - expectedPixel.B) <= 4);
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData(6, 6, 6)]
    [InlineData(5, 6, 3)]
    [InlineData(5, 5, 5)]
    [InlineData(4, 5, 3)]
    [InlineData(4, 4, 4)]
    [InlineData(3, 6, 3)]
    [InlineData(8, 6, 6)]
    public void ResolveSelectedCharacterCount_UsesConfiguredCountOrMinimumFallback(
        int candidates,
        int configured,
        int expected)
    {
        TikTokReferenceSourcePackageService.ResolveSelectedCharacterCount(candidates, configured)
            .Should().Be(expected);
    }

    private static void WriteMetadata(string directory, string source, string workflow)
    {
        File.WriteAllText(
            Path.Combine(directory, "shortdrama-project.json"),
            JsonSerializer.Serialize(new
            {
                sourceProjectDir = source,
                workflowProjectDir = workflow,
                workflowDirName = Path.GetFileName(workflow),
                episodeCount = 3,
            }));
    }

    private static void SaveImage(string path, int width, int height, Rgba32 color)
    {
        using var image = new Image<Rgba32>(width, height, color);
        if (Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
            image.SaveAsJpeg(path);
        else
            image.SaveAsPng(path);
    }
}
