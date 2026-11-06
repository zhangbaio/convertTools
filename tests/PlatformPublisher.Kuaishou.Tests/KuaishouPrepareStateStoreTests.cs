using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouPrepareStateStoreTests
{
    [Fact]
    public void Legacy_completed_project_images_stay_completed_without_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "ks-prepare-state-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "原剧");
        var workflow = Path.Combine(root, "workflow", "_新剧");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        File.WriteAllText(Path.Combine(source, "shortdrama-project.json"), $$"""
            { "workflowProjectDir": {{Json(workflow)}}, "sourceProjectDir": {{Json(source)}} }
            """);
        File.WriteAllText(Path.Combine(workflow, ".kuaishou-personal-prepare-state.json"), """
            { "completed_prepare_steps": ["generate_project_images"] }
            """);
        try
        {
            var status = KuaishouArtifactColumnStatusReader.Read(source, enterprise: false);
            Assert.Equal("已完成", status.ProjectImageStatus);
            Assert.True(File.Exists(Path.Combine(workflow, KuaishouPrepareStateStore.FileName)));

            KuaishouPrepareStateStore.Mark(workflow, enterprise: false, "download");
            var steps = KuaishouPrepareStateStore.ReadSteps(workflow, enterprise: false);
            Assert.Contains("generate_project_images", steps);
            Assert.Contains("download", steps);
            Assert.Equal(2, steps.Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string Json(string path) => "\"" + path.Replace("\\", "\\\\") + "\"";
}
