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
    public void Generated_material_validation_does_not_require_unchecked_role_vector_step()
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
            var selection = TikTokSourceFileInfoPackageSelection.FromEnabledSteps(
                [QueueStepRegistry.GenerateProofMaterial],
                includeRoleSceneScreenshot: false);
            TikTokSourceFileInfoUploadPackageService.Validate(
                workflow,
                selection: selection);
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

            action.Should().NotThrow();
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
