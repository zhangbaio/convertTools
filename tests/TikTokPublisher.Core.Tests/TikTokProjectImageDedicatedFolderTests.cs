using FluentAssertions;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokProjectImageDedicatedFolderTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void Generated_images_are_sorted_by_numeric_suffix()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-project-img-sort-{Guid.NewGuid():N}");
        var output = TikTokProjectImageService.GetOutputDirectory(workflow);
        Directory.CreateDirectory(output);
        try
        {
            foreach (var number in new[] { 10, 2, 1 })
                File.WriteAllBytes(Path.Combine(output, $"工程图_{number}.png"), [1]);

            TikTokProjectImageService.ListGeneratedImages(workflow)
                .Select(Path.GetFileName)
                .Should().Equal("工程图_1.png", "工程图_2.png", "工程图_10.png");
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Current_output_rejects_corrupt_png_files()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-project-img-corrupt-{Guid.NewGuid():N}");
        var output = TikTokProjectImageService.GetOutputDirectory(workflow);
        Directory.CreateDirectory(output);
        try
        {
            File.WriteAllBytes(Path.Combine(output, "工程图_1.png"), [1, 2, 3]);

            TikTokProjectImageService.CountProjectImages(workflow).Should().Be(1);
            TikTokProjectImageService.HasCurrentOutput(workflow, requiredCount: 1).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Interrupted_commit_restores_backup_before_cleaning_staging()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-project-img-recover-{Guid.NewGuid():N}");
        var output = TikTokProjectImageService.GetOutputDirectory(workflow);
        var backup = Path.Combine(output, ".backup-crash");
        var staging = Path.Combine(output, ".staging-crash");
        var obsolete = Path.Combine(output, ".obsolete-backup-committed");
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(obsolete);
        try
        {
            File.WriteAllBytes(Path.Combine(output, "工程图_1.png"), [9, 9, 9]);
            File.WriteAllBytes(Path.Combine(backup, "工程图_1.png"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(staging, "工程图_1.png"), [5, 6]);
            File.WriteAllBytes(Path.Combine(obsolete, "工程图_1.png"), [7, 8]);

            TikTokProjectImageService.RecoverInterruptedOutput(output, log: null);

            File.ReadAllBytes(Path.Combine(output, "工程图_1.png")).Should().Equal(1, 2, 3, 4);
            Directory.Exists(backup).Should().BeFalse();
            Directory.Exists(staging).Should().BeFalse();
            Directory.Exists(obsolete).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(workflow))
                Directory.Delete(workflow, recursive: true);
        }
    }

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
            File.WriteAllBytes(imagePath, OnePixelPng);
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
