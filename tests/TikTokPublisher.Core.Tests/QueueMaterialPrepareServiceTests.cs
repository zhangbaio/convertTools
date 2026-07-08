using FluentAssertions;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueMaterialPrepareServiceTests
{
    [Fact]
    public async Task PrepareMaterialInputsAsync_DoesNotExtractFrameWhenOnlyVideoExists()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"queue-material-prepare-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "source-project");
        Directory.CreateDirectory(sourceDir);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(sourceDir, "demo.mp4"), [1, 2, 3]);

            var logs = new List<string>();
            var result = await QueueMaterialPrepareService.PrepareMaterialInputsAsync(
                sourceDir,
                logs.Add,
                CancellationToken.None);

            result.Should().BeNull();
            File.Exists(Path.Combine(sourceDir, "海报图片.jpg")).Should().BeFalse();
            logs.Should().NotContain(log => log.Contains("抽帧", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareMaterialInputsAsync_PrefersRealImageOverExistingPosterAlias()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"queue-material-prepare-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "source-project");
        Directory.CreateDirectory(sourceDir);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(sourceDir, "海报图片.jpg"), [1]);
            await File.WriteAllBytesAsync(Path.Combine(sourceDir, "real-poster.heic"), [2]);

            var result = await QueueMaterialPrepareService.PrepareMaterialInputsAsync(
                sourceDir,
                _ => { },
                CancellationToken.None);

            result.Should().Be(Path.Combine(sourceDir, "海报图片.heic"));
            File.ReadAllBytes(result!).Should().Equal([2]);
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }
}
