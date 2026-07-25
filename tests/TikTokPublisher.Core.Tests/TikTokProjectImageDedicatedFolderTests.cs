using FluentAssertions;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokProjectImageDedicatedFolderTests
{
    [Fact]
    public void Output_directory_is_dedicated_editing_project_folder()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-project-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            var dir = TikTokProjectImageService.GetOutputDirectory(workflow);
            Path.GetFileName(dir).Should().Be(TikTokProjectImageService.OutputDirectoryName);

            Directory.CreateDirectory(dir);
            var imagePath = Path.Combine(dir, "工程图_1.png");
            File.WriteAllBytes(imagePath, [1, 2, 3]);
            // also leave a legacy root file that should be ignored once dedicated files exist
            File.WriteAllBytes(Path.Combine(workflow, "工程图_legacy.png"), [9]);

            var listed = TikTokProjectImageService.ListGeneratedImages(workflow);
            listed.Should().ContainSingle().Which.Should().Be(imagePath);
            TikTokProjectImageService.HasCurrentOutput(workflow, requiredCount: 1).Should().BeTrue();

            var account = new Models.TikTokAccountProfile
            {
                TiktokCopyrightMaterialTypes =
                [
                    TikTokPublishConstants.ProductionAgreementMaterialType,
                    TikTokPublishConstants.EditingProjectFilesMaterialType,
                ],
            };
            File.WriteAllBytes(
                TikTokProofMaterialService.GetPdfPath(workflow),
                "%PDF-1.7\nproof"u8.ToArray());
            var options = TikTokPublishOptionsBuilder.FromAccount(account, workflow);
            options.CopyrightMaterialFilePaths[TikTokPublishConstants.EditingProjectFilesMaterialType]
                .Should().Be(dir);
            options.ResolveCopyrightMaterialFilePaths(TikTokPublishConstants.EditingProjectFilesMaterialType)
                .Should().Contain(imagePath);
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }
}
