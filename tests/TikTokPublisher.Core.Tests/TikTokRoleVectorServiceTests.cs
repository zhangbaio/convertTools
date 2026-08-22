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
    public void Load_treats_removed_manual_final_mode_as_automatic()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"removed-manual-final-{Guid.NewGuid():N}");
        var configurationPath = ManualRoleVectorMaterialService.GetConfigurationPath(workflow);
        Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
        File.WriteAllText(
            configurationPath,
            """
            {
              "version": "v1",
              "mode": "manual-final",
              "locked": true,
              "fingerprint": "legacy",
              "finalImagePath": "手动角色矢量图.png",
              "characters": []
            }
            """);

        try
        {
            var configuration = ManualRoleVectorMaterialService.Load(workflow);

            configuration.Mode.Should().Be(ManualRoleVectorMode.Auto);
            configuration.Locked.Should().BeTrue();
            configuration.Characters.Should().BeEmpty();
            configuration.Fingerprint.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateAsync_UsesManualReferencesAndCachedGeneratedCharacters()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-role-references-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "原剧名");
        var workflow = Path.Combine(workspace, "workflow", "_新剧名");
        var input = Path.Combine(workspace, "用户截图");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        Directory.CreateDirectory(input);
        WriteMetadata(source, source, workflow);
        WriteMetadata(workflow, source, workflow);
        var references = Enumerable.Range(1, 3).Select(index =>
        {
            var reference = Path.Combine(input, $"参考{index}.png");
            SaveImage(reference, 360, 640, new Rgba32((byte)(index * 60), 70, 120));
            return new ManualRoleCharacter(index, $"人物{index}", string.Empty, reference);
        }).ToArray();

        try
        {
            var saved = ManualRoleVectorMaterialService.SaveReferences(workflow, references);
            saved.Mode.Should().Be(ManualRoleVectorMode.ReferencesOnly);
            saved.Characters.Should().HaveCount(3);
            foreach (var (character, index) in saved.Characters.Select((value, index) => (value, index)))
            {
                SaveImage(character.CharacterPath, 768, 1024,
                    new Rgba32(40, (byte)((index + 1) * 55), 150));
                ManualRoleVectorMaterialService.MarkGeneratedCharacterCurrent(
                    character.CharacterPath,
                    ManualRoleVectorMaterialService.ComputeSha256(character.ReferencePath));
            }
            saved = ManualRoleVectorMaterialService.SaveReferences(workflow, saved.Characters);
            saved.Characters.Should().OnlyContain(character =>
                ManualRoleVectorMaterialService.IsGeneratedCharacterCurrent(
                    character.CharacterPath,
                    ManualRoleVectorMaterialService.ComputeSha256(character.ReferencePath)),
                "重复保存未变化的参考图应保留已生成定妆图");

            var logs = new List<string>();
            var output = await TikTokRoleVectorService.GenerateAsync(
                new QueueProjectItem { ProjectDir = source, NewTitle = "新剧名" },
                new ClientSettings(),
                3,
                forceRerun: true,
                logs.Add,
                CancellationToken.None);

            File.Exists(output).Should().BeTrue();
            TikTokRoleVectorService.HasCurrentOutput(workflow, 3).Should().BeTrue();
            logs.Should().Contain(message => message.Contains("参考图未变化", StringComparison.Ordinal));
            using var manifest = JsonDocument.Parse(File.ReadAllText(
                TikTokReferenceSourcePackageService.GetCharacterManifestPath(workflow)));
            manifest.RootElement.GetProperty("sourceMode").GetString().Should().Be("manual-references");

            SaveImage(saved.Characters[0].ReferencePath, 720, 1280, new Rgba32(250, 20, 20));
            TikTokRoleVectorService.HasCurrentOutput(workflow, 3)
                .Should().BeFalse("人工参考图变化后矢量图哈希应失效");
            var regenerate = () => TikTokRoleVectorService.GenerateAsync(
                new QueueProjectItem { ProjectDir = source },
                new ClientSettings(),
                3,
                forceRerun: true,
                logs.Add,
                CancellationToken.None);
            await regenerate.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*配置豆包或 Ofox Image2*");
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void SaveReferences_RejectsDuplicatePeopleImages()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"duplicate-role-references-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        var reference = Path.Combine(workflow, "同一人物.png");
        SaveImage(reference, 360, 640, new Rgba32(40, 70, 120));
        var characters = Enumerable.Range(1, 3)
            .Select(index => new ManualRoleCharacter(index, $"人物{index}", string.Empty, reference))
            .ToArray();
        try
        {
            var action = () => ManualRoleVectorMaterialService.SaveReferences(workflow, characters);
            action.Should().Throw<InvalidOperationException>().WithMessage("*重复*");
        }
        finally
        {
            if (Directory.Exists(workflow)) Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateAsync_UsesLockedManualPairsWithoutImageModel()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-role-pairs-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "原剧名");
        var workflow = Path.Combine(workspace, "workflow", "_新剧名");
        var input = Path.Combine(workspace, "用户截图");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        Directory.CreateDirectory(input);
        WriteMetadata(source, source, workflow);
        WriteMetadata(workflow, source, workflow);
        var pairs = Enumerable.Range(1, 3).Select(index =>
        {
            var character = Path.Combine(input, $"角色{index}.png");
            var reference = Path.Combine(input, $"参考{index}.jpg");
            SaveImage(character, 300, 500, new Rgba32((byte)(index * 60), 80, 100));
            SaveImage(reference, 360, 640, new Rgba32(40, (byte)(index * 60), 120));
            return new ManualRoleCharacter(index, $"人物{index}", character, reference);
        }).ToArray();

        try
        {
            var saved = ManualRoleVectorMaterialService.SavePaired(workflow, pairs);
            saved.Mode.Should().Be(ManualRoleVectorMode.Paired);
            saved.Characters.Should().HaveCount(3);
            saved.Characters.Should().OnlyContain(character =>
                character.CharacterPath.StartsWith(
                    ManualRoleVectorMaterialService.GetRoot(workflow),
                    StringComparison.OrdinalIgnoreCase));
            saved = ManualRoleVectorMaterialService.SavePaired(workflow, saved.Characters);
            saved.Characters.Should().HaveCount(3, "已受管的素材应允许再次保存");

            var logs = new List<string>();
            var output = await TikTokRoleVectorService.GenerateAsync(
                new QueueProjectItem { ProjectDir = source, NewTitle = "新剧名" },
                new ClientSettings(),
                3,
                forceRerun: true,
                logs.Add,
                CancellationToken.None);

            File.Exists(output).Should().BeTrue();
            TikTokRoleVectorService.HasCurrentOutput(workflow, 3).Should().BeTrue();
            logs.Should().Contain(message => message.Contains("人工", StringComparison.Ordinal));
            using var manifest = JsonDocument.Parse(File.ReadAllText(
                TikTokReferenceSourcePackageService.GetCharacterManifestPath(workflow)));
            manifest.RootElement.GetProperty("sourceMode").GetString().Should().Be("manual-paired");
            manifest.RootElement.GetProperty("characters")[1]
                .GetProperty("name").GetString().Should().Be("人物2");

            SaveImage(saved.Characters[0].ReferencePath, 360, 640, new Rgba32(250, 20, 20));
            TikTokRoleVectorService.HasCurrentOutput(workflow, 3)
                .Should().BeFalse("人工参考图变化后角色矢量图状态必须失效");
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

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
            var statePath = TikTokRoleVectorService.GetStatePath(workflow);
            var originalState = File.ReadAllText(statePath);
            File.WriteAllText(
                statePath,
                originalState.Replace("\"characterCount\": 3", "\"characterCount\": 2", StringComparison.Ordinal));
            TikTokRoleVectorService.HasCurrentOutput(workflow, 3)
                .Should().BeFalse("三人配置不能继续复用双人回退产物");
            File.WriteAllText(statePath, originalState);
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
    public async Task EnsureCharacterImagesAsync_RequiresVisionModelBeforeAssigningEpisodeActors()
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
        var colors = new[]
        {
            new Rgba32(20, 210, 40),
            new Rgba32(30, 50, 220),
            new Rgba32(220, 180, 30),
        };
        for (var index = 0; index < colors.Length; index++)
            SaveImage(Path.Combine(episodeCharacterDir, $"角色设定_{index + 1:D2}.jpg"), 128, 128, colors[index]);

        var logs = new List<string>();
        try
        {
            var action = () => TikTokReferenceSourcePackageService.EnsureCharacterImagesAsync(
                new QueueProjectItem { ProjectDir = source },
                new ClientSettings(),
                3,
                logs.Add,
                CancellationToken.None);

            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*配置文本/视觉模型*");
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData(6, 6, 6)]
    [InlineData(5, 6, 2)]
    [InlineData(5, 5, 5)]
    [InlineData(4, 5, 2)]
    [InlineData(4, 4, 4)]
    [InlineData(3, 6, 2)]
    [InlineData(2, 6, 2)]
    [InlineData(2, 2, 2)]
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
