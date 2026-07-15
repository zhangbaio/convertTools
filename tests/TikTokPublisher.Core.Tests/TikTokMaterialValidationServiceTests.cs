using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokMaterialValidationServiceTests
{
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
