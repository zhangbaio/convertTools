using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokSourceFileInfoUploadPackageServiceTests
{
    [Fact]
    public void Generate_omits_role_vector_when_upload_option_is_not_enabled()
    {
        var workflow = CreateWorkflow();
        try
        {
            var selection = TikTokSourceFileInfoPackageSelection.FromEnabledSteps(
                [TikTokPublisher.Core.Queue.QueueStepRegistry.GenerateAiScriptOutline,
                 TikTokPublisher.Core.Queue.QueueStepRegistry.GenerateEpisodeScript,
                 TikTokPublisher.Core.Queue.QueueStepRegistry.GenerateProofMaterial],
                includeRoleVector: false,
                includeRoleSceneScreenshot: false);
            File.Delete(Path.Combine(
                TikTokReferenceSourcePackageService.GetRoot(workflow),
                TikTokReferenceSourcePackageService.CharacterWorkbenchFileName));

            var files = TikTokSourceFileInfoUploadPackageService.Generate(
                workflow,
                selection: selection);

            files.Select(Path.GetFileName).Should().Equal(
                TikTokSourceFileInfoUploadPackageService.OutlineFileName,
                TikTokSourceFileInfoUploadPackageService.ScriptFileName,
                TikTokSourceFileInfoUploadPackageService.ProjectInfoImageFileName);
            files.Should().NotContain(path =>
                Path.GetFileName(path) == TikTokSourceFileInfoUploadPackageService.RoleVectorImageFileName);
            TikTokSourceFileInfoUploadPackageService.HasCurrentOutput(
                workflow,
                selection: selection).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Generate_removes_stale_role_vector_when_upload_option_is_disabled()
    {
        var workflow = CreateWorkflow();
        try
        {
            TikTokSourceFileInfoUploadPackageService.Generate(workflow);
            var roleVectorCopy = Path.Combine(
                TikTokSourceFileInfoUploadPackageService.GetOutputDirectory(workflow),
                TikTokSourceFileInfoUploadPackageService.RoleVectorImageFileName);
            File.Exists(roleVectorCopy).Should().BeTrue();

            var selection = new TikTokSourceFileInfoPackageSelection(
                IncludeOutline: true,
                IncludeScript: true,
                IncludeRoleVector: false,
                IncludeRoleSceneScreenshot: false);
            var files = TikTokSourceFileInfoUploadPackageService.Generate(
                workflow,
                selection: selection);

            files.Should().NotContain(path =>
                Path.GetFileName(path) == TikTokSourceFileInfoUploadPackageService.RoleVectorImageFileName);
            File.Exists(roleVectorCopy).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Generate_tolerates_missing_selected_file_until_material_validation()
    {
        var workflow = CreateWorkflow();
        try
        {
            File.Delete(Path.Combine(
                TikTokReferenceSourcePackageService.GetRoot(workflow),
                TikTokReferenceSourcePackageService.CharacterWorkbenchFileName));
            var logs = new List<string>();

            var files = TikTokSourceFileInfoUploadPackageService.Generate(
                workflow,
                log: logs.Add,
                validateComplete: false);

            files.Should().HaveCount(3);
            logs.Should().Contain(message =>
                message.Contains("由成片检查统一校验", StringComparison.Ordinal));
            var validate = () => TikTokSourceFileInfoUploadPackageService.Validate(workflow);
            validate.Should().Throw<FileNotFoundException>()
                .WithMessage("*角色矢量图.png*");
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }


    [Fact]
    public void Generate_creates_exact_ordered_mixed_four_file_package()
    {
        var workflow = CreateWorkflow();
        try
        {
            var files = TikTokSourceFileInfoUploadPackageService.Generate(workflow);

            files.Select(Path.GetFileName).Should().Equal(
                TikTokSourceFileInfoUploadPackageService.OutlineFileName,
                TikTokSourceFileInfoUploadPackageService.ScriptFileName,
                TikTokSourceFileInfoUploadPackageService.ProjectInfoImageFileName,
                TikTokSourceFileInfoUploadPackageService.RoleVectorImageFileName);
            files.Should().HaveCount(TikTokSourceFileInfoUploadPackageService.RequiredFileCount);
            files.Should().OnlyContain(path => File.Exists(path));
            Directory.EnumerateFiles(TikTokSourceFileInfoUploadPackageService.GetOutputDirectory(workflow))
                .Should().HaveCount(4);
            TikTokSourceFileInfoUploadPackageService.HasCurrentOutput(workflow).Should().BeTrue();

            File.WriteAllText(
                Path.Combine(TikTokSourceFileInfoUploadPackageService.GetOutputDirectory(workflow), "额外文件.txt"),
                "unexpected");
            TikTokSourceFileInfoUploadPackageService.HasCurrentOutput(workflow)
                .Should().BeFalse("上传目录只能包含规定的四个文件");
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Final_validation_rebuilds_partial_package_from_existing_products()
    {
        var workflow = CreateWorkflow();
        try
        {
            var logs = new List<string>();

            var repaired = TikTokSourceFileInfoUploadPackageService.EnsureCurrentFromExistingOutputs(
                workflow,
                log: logs.Add);

            repaired.Should().BeTrue();
            TikTokSourceFileInfoUploadPackageService.Validate(workflow);
            logs.Should().Contain(message => message.Contains("自动修复原始文件信息上传包"));
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Final_validation_migrates_legacy_project_info_screenshot_name()
    {
        var workflow = CreateWorkflow();
        var output = TikTokSourceFileInfoUploadPackageService.GetOutputDirectory(workflow);
        var current = Path.Combine(output, TikTokSourceFileInfoUploadPackageService.ProjectInfoImageFileName);
        var legacy = Path.Combine(output, TikTokSourceFileInfoUploadPackageService.LegacyProjectInfoImageFileName);
        File.Move(current, legacy);
        try
        {
            TikTokSourceFileInfoUploadPackageService.EnsureCurrentFromExistingOutputs(workflow);

            TikTokSourceFileInfoUploadPackageService.Validate(workflow);
            File.Exists(current).Should().BeTrue();
            File.Exists(legacy).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Generate_optionally_includes_role_scene_screenshot()
    {
        var workflow = CreateWorkflow();
        try
        {
            SaveImage(
                Path.Combine(
                    TikTokSourceFileInfoUploadPackageService.GetOutputDirectory(workflow),
                    TikTokSourceFileInfoUploadPackageService.RoleSceneImageFileName),
                1280,
                720);

            var files = TikTokSourceFileInfoUploadPackageService.Generate(
                workflow,
                includeRoleSceneScreenshot: true);

            files.Select(Path.GetFileName).Should().ContainInOrder(
                TikTokSourceFileInfoUploadPackageService.ProjectInfoImageFileName,
                TikTokSourceFileInfoUploadPackageService.RoleVectorImageFileName,
                TikTokSourceFileInfoUploadPackageService.RoleSceneImageFileName);
            files.Should().HaveCount(5);
            TikTokSourceFileInfoUploadPackageService.HasCurrentOutput(
                workflow,
                includeRoleSceneScreenshot: true).Should().BeTrue();
            TikTokSourceFileInfoUploadPackageService.ListFiles(
                workflow,
                includeRoleSceneScreenshot: false)
                .Should().NotContain(path =>
                    Path.GetFileName(path).Equals(
                        TikTokSourceFileInfoUploadPackageService.RoleSceneImageFileName,
                        StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Generate_rejects_role_vector_with_wrong_dimensions()
    {
        var workflow = CreateWorkflow(roleVectorWidth: 800, roleVectorHeight: 600);
        try
        {
            var action = () => TikTokSourceFileInfoUploadPackageService.Generate(workflow);

            action.Should().Throw<InvalidDataException>()
                .WithMessage("*角色矢量图尺寸必须为 2342×1280*");
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void SyncRoleVectorCopy_overwrites_stale_upload_image()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sync-role-vector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "new.png");
        var destination = Path.Combine(root, "upload", TikTokSourceFileInfoUploadPackageService.RoleVectorImageFileName);
        using (var image = new Image<Rgba32>(32, 32, new Rgba32(220, 30, 30))) image.SaveAsPng(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var image = new Image<Rgba32>(32, 32, new Rgba32(30, 30, 220))) image.SaveAsPng(destination);

        try
        {
            TikTokSourceFileInfoUploadPackageService.SyncRoleVectorCopy(source, destination);

            using var synchronized = Image.Load<Rgba32>(destination);
            synchronized[16, 16].R.Should().BeGreaterThan(200);
            synchronized[16, 16].B.Should().BeLessThan(50);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Generate_directs_missing_role_vector_to_independent_step()
    {
        var workflow = CreateWorkflow();
        try
        {
            File.Delete(Path.Combine(
                TikTokReferenceSourcePackageService.GetRoot(workflow),
                TikTokReferenceSourcePackageService.CharacterWorkbenchFileName));

            var action = () => TikTokSourceFileInfoUploadPackageService.Generate(workflow);

            action.Should().Throw<FileNotFoundException>()
                .WithMessage("*生成角色矢量图*步骤*");
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Find_script_pdf_prefers_standard_front_five_episode_file()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"source-upload-script-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            var other = Path.Combine(workflow, "测试剧前3集剧本.pdf");
            var standard = Path.Combine(workflow, "测试剧前5集剧本.pdf");
            WritePdf(other);
            WritePdf(standard);
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddMinutes(1));

            TikTokSourceFileInfoUploadPackageService.FindScriptPdf(workflow)
                .Should().Be(standard);
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    private static string CreateWorkflow(int roleVectorWidth = 2342, int roleVectorHeight = 1280)
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"source-upload-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        WritePdf(Path.Combine(workflow, TikTokAiScriptOutlineService.OutputFileName));
        WritePdf(Path.Combine(workflow, "测试剧前5集剧本.pdf"));

        var screenshotDirectory = TikTokSourceFileInfoScreenshotService.GetOutputDirectory(workflow);
        Directory.CreateDirectory(screenshotDirectory);
        SaveImage(
            Path.Combine(screenshotDirectory, TikTokSourceFileInfoUploadPackageService.ProjectInfoImageFileName),
            1280,
            720);
        var referenceRoot = TikTokReferenceSourcePackageService.GetRoot(workflow);
        Directory.CreateDirectory(referenceRoot);
        SaveImage(
            Path.Combine(referenceRoot, TikTokReferenceSourcePackageService.CharacterWorkbenchFileName),
            roleVectorWidth,
            roleVectorHeight);
        return workflow;
    }

    private static void WritePdf(string path) =>
        File.WriteAllBytes(path, "%PDF-1.7\nvalid test pdf"u8.ToArray());

    private static void SaveImage(string path, int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(60, 80, 100));
        image.SaveAsPng(path);
    }
}
