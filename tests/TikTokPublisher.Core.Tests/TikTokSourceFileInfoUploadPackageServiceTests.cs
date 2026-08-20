using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokSourceFileInfoUploadPackageServiceTests
{
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
