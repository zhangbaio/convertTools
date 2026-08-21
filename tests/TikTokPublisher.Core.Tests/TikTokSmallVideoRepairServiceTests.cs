using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokSmallVideoRepairServiceTests
{
    [Fact]
    public void Repair_Rebuilds_When_Current_Source_Is_Small_Even_If_Old_Staging_Is_Large()
    {
        var workspace = CreateWorkspace(out var sourceDir, out var stagingDir);
        var sourcePath = Path.Combine(sourceDir, "episode-1.avi");
        var stagingPath = Path.Combine(stagingDir, "Current-第1集.avi");
        File.WriteAllBytes(sourcePath, [1]);
        WriteLargeFile(stagingPath);

        try
        {
            var action = () => TikTokSmallVideoRepairService.Repair(
                sourceDir,
                "Current",
                "Original",
                log: null,
                CancellationToken.None);

            action.Should().Throw<InvalidOperationException>();
            new FileInfo(stagingPath).Length.Should().Be(1);
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public void Repair_Uses_Resolved_Staging_Fallback_When_Source_Is_Missing_And_Title_Changed()
    {
        var workspace = CreateWorkspace(out var sourceDir, out var stagingDir);
        var stagingPath = Path.Combine(stagingDir, "OldTitle-第1集.avi");
        File.WriteAllBytes(stagingPath, [1]);

        try
        {
            var action = () => TikTokSmallVideoRepairService.Repair(
                sourceDir,
                "NewTitle",
                "Original",
                log: null,
                CancellationToken.None);

            action.Should().Throw<InvalidOperationException>()
                .WithMessage("*NewTitle-第1集.avi*");
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public void Repair_Rebuilds_When_Staging_Is_Small_Even_If_Source_Is_Large()
    {
        var workspace = CreateWorkspace(out var sourceDir, out var stagingDir);
        var sourcePath = Path.Combine(sourceDir, "episode-1.avi");
        var stagingPath = Path.Combine(stagingDir, "Current-第1集.avi");
        WriteLargeFile(sourcePath);
        File.WriteAllBytes(stagingPath, [1]);

        try
        {
            TikTokSmallVideoRepairService.NeedsRepair(sourceDir).Should().BeTrue();

            TikTokSmallVideoRepairService.Repair(
                sourceDir,
                "Current",
                "Original",
                log: null,
                CancellationToken.None);

            new FileInfo(stagingPath).Length.Should().BeGreaterThanOrEqualTo(TikTokVideoConstraints.MinSizeBytes);
            TikTokSmallVideoRepairService.NeedsRepair(sourceDir).Should().BeFalse();
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    private static string CreateWorkspace(out string sourceDir, out string stagingDir)
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"small-video-repair-{Guid.NewGuid():N}");
        sourceDir = Path.Combine(workspace, "source");
        stagingDir = Path.Combine(
            workspace,
            "workflow",
            "source",
            TikTokUploadStagingService.StagingDirName);
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(stagingDir);
        return workspace;
    }

    private static void WriteLargeFile(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength(TikTokVideoConstraints.MinSizeBytes + 1);
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
    }
}
