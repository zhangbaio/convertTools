using FluentAssertions;
using ShortDrama.Infrastructure;
using Xunit;

namespace ShortDrama.Infrastructure.Tests;

public sealed class BundledToolResolverTests
{
    [Fact]
    public async Task TryResolveBinary_Should_Find_Windows_Rid_Tool_Directory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var toolName = $"codexbundledtooltest{Guid.NewGuid():N}";
        var toolDir = Path.Combine(AppContext.BaseDirectory, "tools", CurrentWindowsRid(), toolName);
        var toolPath = Path.Combine(toolDir, $"{toolName}.exe");

        try
        {
            Directory.CreateDirectory(toolDir);
            await File.WriteAllTextAsync(toolPath, string.Empty);

            var resolved = BundledToolResolver.TryResolveBinary(toolName);
            var bundledOnly = BundledToolResolver.TryResolveBundledBinary(toolName);

            resolved.Should().Be(toolPath);
            bundledOnly.Should().Be(toolPath);
        }
        finally
        {
            if (Directory.Exists(toolDir))
            {
                Directory.Delete(toolDir, recursive: true);
            }
        }
    }

    private static string CurrentWindowsRid()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return $"win-{arch}";
    }
}
