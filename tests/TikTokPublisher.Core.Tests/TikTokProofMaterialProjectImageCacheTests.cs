using FluentAssertions;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokProofMaterialProjectImageCacheTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void Proof_material_rejects_old_project_images_when_current_fablecut_configuration_is_invalid()
    {
        var workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            "tiktok-proof-project-images-" + Guid.NewGuid().ToString("N"));
        var sourceProjectDir = Path.Combine(workspaceRoot, "测试项目");
        var workflowProjectDir = Path.Combine(workspaceRoot, "workflow", "测试项目");

        try
        {
            Directory.CreateDirectory(sourceProjectDir);
            var outputDir = TikTokProjectImageService.GetOutputDirectory(workflowProjectDir);
            Directory.CreateDirectory(outputDir);
            for (var index = 1; index <= TikTokProjectImageService.MinUploadImageCount; index++)
            {
                File.WriteAllBytes(Path.Combine(outputDir, $"工程图_{index}.png"), OnePixelPng);
            }

            var settings = new ClientSettings
            {
                TiktokProjectImageGenerationMode = "fablecut",
                TiktokProjectImageCount = TikTokProjectImageService.MinUploadImageCount,
                TiktokProjectImageFableCutRoot = Path.Combine(workspaceRoot, "missing-fablecut"),
            };
            var account = new TikTokAccountProfile
            {
                TiktokProofAccountConfigMigrated = true,
                TiktokCopyrightMaterialTypes =
                [
                    TikTokPublishConstants.EditingProjectFilesMaterialType,
                ],
            };
            var item = new QueueProjectItem
            {
                ProjectDir = sourceProjectDir,
                NewTitle = "缓存切换测试剧",
                ProofMaterialStatementDate = "2026-08-11",
            };
            var request = TikTokProofMaterialService.CreateQueueRequest(
                item,
                settings,
                account,
                workflowProjectDir,
                new DateOnly(2026, 8, 11));
            ProjectStateDocumentStore.SaveDocument(
                workspaceRoot,
                sourceProjectDir,
                TikTokProofMaterialService.StateDocumentType,
                new Dictionary<string, object?>
                {
                    ["fingerprint"] = TikTokProofMaterialService.ComputeFingerprint(request),
                    ["editing_project_files_completed"] = true,
                },
                workflowProjectDir);

            // The legacy count-only check sees the stale images, but proof-material reuse
            // must honor the selected backend and its complete configuration fingerprint.
            TikTokProjectImageService.HasCurrentOutput(workflowProjectDir).Should().BeTrue();
            TikTokProofMaterialService.HasReusableProofMaterialForCopyrightCompletion(
                    item, settings, account)
                .Should().BeFalse();
            TikTokProofMaterialService.NeedsGenerateProofMaterial(item, settings, account)
                .Should().BeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }
}
