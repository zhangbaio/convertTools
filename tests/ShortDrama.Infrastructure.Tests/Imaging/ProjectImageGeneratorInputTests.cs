using FluentAssertions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Imaging;
using Xunit;

namespace ShortDrama.Infrastructure.Tests.Imaging;

public sealed class ProjectImageGeneratorInputTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"project-image-input-{Guid.NewGuid():N}");

    public ProjectImageGeneratorInputTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort cleanup on Windows.
        }
    }

    [Fact]
    public void ResolveSourceVideos_uses_explicit_paths_without_requiring_an_input_directory()
    {
        var first = CreateVideo(Path.Combine(_root, "source-a", "episode-10.mp4"));
        var second = CreateVideo(Path.Combine(_root, "source-b", "episode-02.mp4"));
        var request = new ProjectImageGenerateRequest(
            ProjectDir: _root,
            InputDir: Path.Combine(_root, "directory-does-not-exist"),
            OutputDir: Path.Combine(_root, "output"),
            SourceVideos: [second, first]);

        var resolved = ProjectImageGenerator.ResolveSourceVideos(request);

        resolved.Should().Equal(Path.GetFullPath(second), Path.GetFullPath(first));
        Directory.Exists(request.InputDir).Should().BeFalse();
    }

    [Fact]
    public void ResolveSourceVideos_keeps_legacy_directory_scanning_when_explicit_paths_are_absent()
    {
        var input = Path.Combine(_root, "legacy-input");
        var later = CreateVideo(Path.Combine(input, "002.mp4"));
        var earlier = CreateVideo(Path.Combine(input, "001.mp4"));
        File.WriteAllText(Path.Combine(input, "ignore.txt"), "not a video");
        var request = new ProjectImageGenerateRequest(
            ProjectDir: _root,
            InputDir: input,
            OutputDir: Path.Combine(_root, "output"));

        var resolved = ProjectImageGenerator.ResolveSourceVideos(request);

        resolved.Should().Equal(Path.GetFullPath(earlier), Path.GetFullPath(later));
    }

    [Fact]
    public void ResolveSourceVideos_reports_a_missing_explicit_video_instead_of_falling_back_to_copying()
    {
        var missing = Path.Combine(_root, "missing.mp4");
        var request = new ProjectImageGenerateRequest(
            ProjectDir: _root,
            InputDir: _root,
            OutputDir: Path.Combine(_root, "output"),
            SourceVideos: [missing]);

        var action = () => ProjectImageGenerator.ResolveSourceVideos(request);

        action.Should().Throw<FileNotFoundException>()
            .WithMessage("*直接输入视频不存在*");
    }

    private static string CreateVideo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0, 1, 2, 3]);
        return path;
    }
}
