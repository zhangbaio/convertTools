using FluentAssertions;
using ShortDrama.Infrastructure.Config;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Config;

public sealed class UpwardConfigLocatorTests
{
    [Fact]
    public async Task FindConfigDirAsync_Should_Find_Config_In_Ancestor()
    {
        var root = Directory.CreateTempSubdirectory();
        var configDir = Directory.CreateDirectory(Path.Combine(root.FullName, "config"));
        var workflowDir = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow"));
        var projectDir = Directory.CreateDirectory(Path.Combine(workflowDir.FullName, "项目A"));

        await File.WriteAllBytesAsync(Path.Combine(configDir.FullName, "sign.png"), new byte[] { 1, 2, 3 });
        await File.WriteAllBytesAsync(Path.Combine(configDir.FullName, "seal.png"), new byte[] { 4, 5, 6 });

        var locator = new UpwardConfigLocator();

        var result = await locator.FindConfigDirAsync(projectDir.FullName, CancellationToken.None);

        result.Should().Be(configDir.FullName);
    }

    [Fact]
    public async Task FindConfigDirAsync_Should_Throw_When_Config_Not_Found()
    {
        var root = Directory.CreateTempSubdirectory();
        var workflowDir = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow"));
        var projectDir = Directory.CreateDirectory(Path.Combine(workflowDir.FullName, "项目A"));

        var locator = new UpwardConfigLocator();

        var action = async () => await locator.FindConfigDirAsync(projectDir.FullName, CancellationToken.None);

        await action.Should().ThrowAsync<DirectoryNotFoundException>()
            .WithMessage("*config*");
    }
}
