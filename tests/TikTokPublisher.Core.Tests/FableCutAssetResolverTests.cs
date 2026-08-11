using FluentAssertions;
using TikTokPublisher.Core.Services.ProjectImages.FableCut;

namespace TikTokPublisher.Core.Tests;

public sealed class FableCutAssetResolverTests
{
    private static readonly string[] RequiredFiles =
    ["index.html", "app.js", "style.css", "ruler-worker.js", "meter-worklet.js"];

    [Fact]
    public void Explicit_root_is_validated_and_fingerprinted_by_content()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fablecut-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in RequiredFiles)
                File.WriteAllText(Path.Combine(root, name), name);

            FableCutAssetResolver.Resolve(root).Should().Be(Path.GetFullPath(root));
            var before = FableCutAssetResolver.ComputeFingerprint(root);
            File.AppendAllText(Path.Combine(root, "app.js"), "changed");
            FableCutAssetResolver.ComputeFingerprint(root).Should().NotBe(before);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Explicit_root_reports_missing_runtime_files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fablecut-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "index.html"), "ok");
            var action = () => FableCutAssetResolver.Resolve(root);
            action.Should().Throw<InvalidOperationException>().WithMessage("*app.js*");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
