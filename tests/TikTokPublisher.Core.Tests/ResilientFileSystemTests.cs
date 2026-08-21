using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ResilientFileSystemTests
{
    [Fact]
    public void DeleteDirectory_clears_readonly_content_and_path_can_be_recreated()
    {
        var root = CreateTempDirectory();
        var child = Path.Combine(root, "child");
        var file = Path.Combine(child, "readonly.txt");
        Directory.CreateDirectory(child);
        File.WriteAllText(file, "old");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        ResilientFileSystem.DeleteDirectory(root);
        ResilientFileSystem.EnsureDirectory(child);
        File.WriteAllText(Path.Combine(child, "new.txt"), "new");

        File.ReadAllText(Path.Combine(child, "new.txt")).Should().Be("new");
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task DeleteFile_retries_until_transient_exclusive_lock_is_released()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "locked.tmp");
        File.WriteAllText(path, "locked");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(220);
            stream.Dispose();
        });

        ResilientFileSystem.DeleteFile(path);
        await release;

        File.Exists(path).Should().BeFalse();
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void MoveDirectory_relocates_complete_directory()
    {
        var root = CreateTempDirectory();
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.txt"), "data");

        ResilientFileSystem.MoveDirectory(source, destination);

        Directory.Exists(source).Should().BeFalse();
        File.ReadAllText(Path.Combine(destination, "file.txt")).Should().Be("data");
        Directory.Delete(root, recursive: true);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"resilient-fs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
