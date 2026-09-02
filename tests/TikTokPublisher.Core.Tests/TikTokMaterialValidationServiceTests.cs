using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokMaterialValidationServiceTests
{
    [Fact]
    public void Generated_material_validation_repairs_partial_source_info_package()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"material-package-repair-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "source");
        var workflow = Path.Combine(workspace, "workflow", "source");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        File.WriteAllText(
            Path.Combine(source, "shortdrama-project.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                sourceProjectDir = source,
                workflowProjectDir = workflow,
            }));
        File.WriteAllBytes(
            Path.Combine(workflow, TikTokAiScriptOutlineService.OutputFileName),
            "%PDF-1.7\noutline"u8.ToArray());
        File.WriteAllBytes(
            Path.Combine(workflow, "测试剧前5集剧本.pdf"),
            "%PDF-1.7\nscript"u8.ToArray());
        var output = TikTokSourceFileInfoUploadPackageService.GetOutputDirectory(workflow);
        Directory.CreateDirectory(output);
        using (var image = new Image<Rgba32>(1280, 720))
            image.SaveAsPng(Path.Combine(
                output,
                TikTokSourceFileInfoUploadPackageService.ProjectInfoImageFileName));
        using (var image = new Image<Rgba32>(1280, 720))
            image.SaveAsPng(Path.Combine(
                output,
                TikTokSourceFileInfoUploadPackageService.RoleSceneImageFileName));
        var referenceRoot = TikTokReferenceSourcePackageService.GetRoot(workflow);
        Directory.CreateDirectory(referenceRoot);
        using (var image = new Image<Rgba32>(2342, 1280))
            image.SaveAsPng(Path.Combine(
                referenceRoot,
                TikTokReferenceSourcePackageService.CharacterWorkbenchFileName));
        try
        {
            var account = new TikTokAccountProfile
            {
                TiktokCopyrightMaterialTypes =
                    [TikTokPublishConstants.SourceFileInformationMaterialType],
            };
            var options = new TikTokMaterialValidationService.Options
            {
                EnabledSteps = new HashSet<string>(
                    [
                        QueueStepRegistry.GenerateEpisodeScript,
                        QueueStepRegistry.GenerateAiScriptOutline,
                        QueueStepRegistry.GenerateRoleVector,
                        QueueStepRegistry.GenerateProofMaterial,
                    ],
                    StringComparer.Ordinal),
            };

            TikTokMaterialValidationService.ValidateGeneratedUploadMaterials(
                source,
                account,
                options,
                log: null);

            var selection = TikTokSourceFileInfoPackageSelection.FromEnabledSteps(
                options.EnabledSteps,
                account.TiktokUploadSourceInfoRoleVector,
                account.TiktokUploadSourceInfoRoleSceneScreenshot);
            TikTokSourceFileInfoUploadPackageService.Validate(
                workflow,
                selection: selection);
            selection.IncludeRoleVector.Should().BeFalse();
            selection.IncludeRoleSceneScreenshot.Should().BeTrue();
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public void Generated_material_validation_rejects_checked_source_info_when_configuration_cannot_supply_four_files()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"material-proof-validation-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "source");
        var workflow = Path.Combine(workspace, "workflow", "source");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        File.WriteAllText(
            Path.Combine(source, "shortdrama-project.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                sourceProjectDir = source,
                workflowProjectDir = workflow,
            }));
        try
        {
            var output = TikTokSourceFileInfoUploadPackageService.GetOutputDirectory(workflow);
            Directory.CreateDirectory(output);
            using (var image = new Image<Rgba32>(1280, 720))
                image.SaveAsPng(Path.Combine(
                    output,
                    TikTokSourceFileInfoUploadPackageService.ProjectInfoImageFileName));
            var account = new TikTokAccountProfile
            {
                TiktokCopyrightMaterialTypes =
                    [TikTokPublishConstants.SourceFileInformationMaterialType],
            };
            var options = new TikTokMaterialValidationService.Options
            {
                EnabledSteps = new HashSet<string>(
                    [QueueStepRegistry.GenerateProofMaterial],
                    StringComparer.Ordinal),
            };

            var action = () => TikTokMaterialValidationService.ValidateGeneratedUploadMaterials(
                source,
                account,
                options,
                log: null);

            action.Should().Throw<InvalidOperationException>()
                .WithMessage("*原始文件信息上传包无效*至少需要 4 个文件*");
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public void Generated_material_validation_skips_source_info_when_upload_material_is_not_checked()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"material-proof-unchecked-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "source");
        var workflow = Path.Combine(workspace, "workflow", "source");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        File.WriteAllText(
            Path.Combine(source, "shortdrama-project.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                sourceProjectDir = source,
                workflowProjectDir = workflow,
            }));
        try
        {
            File.WriteAllBytes(
                TikTokProofMaterialService.GetPdfPath(workflow),
                "%PDF-1.7\nproof"u8.ToArray());
            var account = new TikTokAccountProfile
            {
                TiktokCopyrightMaterialTypes =
                    [TikTokPublishConstants.ProductionAgreementMaterialType],
            };
            var options = new TikTokMaterialValidationService.Options
            {
                EnabledSteps = new HashSet<string>(
                    [QueueStepRegistry.GenerateProofMaterial],
                    StringComparer.Ordinal),
            };

            var action = () => TikTokMaterialValidationService.ValidateGeneratedUploadMaterials(
                source,
                account,
                options,
                log: null);

            action.Should().NotThrow(
                "未在上传材料中勾选原始文件信息时，不应检查其目录或文件数量");
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public void HasCurrentValidationState_Uses_Actual_Staged_Videos_When_Their_Title_Differs()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"material-validation-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "source");
        var workflowDir = Path.Combine(workspace, "workflow", "source");
        var stagingDir = Path.Combine(workflowDir, TikTokUploadStagingService.StagingDirName);
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(stagingDir);

        var sourcePath = Path.Combine(sourceDir, "episode-1.mp4");
        var stagingPath = Path.Combine(stagingDir, "RenamedShow-第1集.mp4");
        File.WriteAllBytes(sourcePath, [1]);
        File.WriteAllBytes(stagingPath, [2, 3]);

        try
        {
            ProjectStateDocumentStore.SaveDocument(
                workspace,
                sourceDir,
                "material_validation_state",
                new Dictionary<string, object?>
                {
                    ["fingerprint"] = TikTokMaterialValidationService.ComputeMaterialFingerprint([stagingPath]),
                },
                workflowDir);

            TikTokMaterialValidationService.HasCurrentValidationState(sourceDir).Should().BeTrue();
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
    }
}
