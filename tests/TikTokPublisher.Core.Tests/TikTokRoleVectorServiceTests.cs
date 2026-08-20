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
                item, new ClientSettings(), forceRerun: true, logs.Add, CancellationToken.None);

            result.Should().Be(output);
            TikTokRoleVectorService.HasCurrentOutput(workflow).Should().BeTrue();
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
