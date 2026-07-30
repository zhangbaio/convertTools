using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class DeletedCopyrightProofWorkflowRecoveryServiceTests
{
    [Fact]
    public void RepairExistingProject_merges_proof_only_target_and_repairs_partial_project()
    {
        using var fixture = RecoveryFixture.Create();
        var proofPath = Path.Combine(
            fixture.DesiredWorkflowDir,
            TikTokProofMaterialService.ProofPdfFileName);
        File.WriteAllText(proofPath, "existing proof");

        var repaired = DeletedCopyrightProofWorkflowRecoveryService.RepairExistingProject(
            fixture.SourceDir,
            fixture.NewTitle);

        repaired.Should().BeTrue();
        Directory.Exists(fixture.CurrentWorkflowDir).Should().BeFalse();
        Directory.Exists(fixture.DesiredWorkflowDir).Should().BeTrue();
        File.ReadAllText(Path.Combine(
                fixture.DesiredWorkflowDir,
                TikTokProofMaterialService.ProofPdfFileName))
            .Should()
            .Be("existing proof");
        File.Exists(Path.Combine(fixture.DesiredWorkflowDir, "短剧信息.txt"))
            .Should()
            .BeTrue();
        ReadMetadataPath(fixture.SourceDir, "workflowProjectDir")
            .Should()
            .Be(fixture.DesiredWorkflowDir);
    }

    [Fact]
    public void ValidateTarget_rejects_real_project_without_changing_either_directory()
    {
        using var fixture = RecoveryFixture.Create();
        var conflictingMetadata = Path.Combine(
            fixture.DesiredWorkflowDir,
            "shortdrama-project.json");
        File.WriteAllText(conflictingMetadata, """{"title":"另一个项目"}""");

        var action = () => DeletedCopyrightProofWorkflowRecoveryService.ValidateTarget(
            fixture.SourceDir,
            fixture.NewTitle);

        action.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*完整项目或未知文件*");
        Directory.Exists(fixture.CurrentWorkflowDir).Should().BeTrue();
        Directory.Exists(fixture.DesiredWorkflowDir).Should().BeTrue();
        File.Exists(conflictingMetadata).Should().BeTrue();
    }

    [Fact]
    public void RepairExistingProject_reuses_target_while_proof_pdf_is_open()
    {
        using var fixture = RecoveryFixture.Create();
        var proofPath = Path.Combine(
            fixture.DesiredWorkflowDir,
            TikTokProofMaterialService.ProofPdfFileName);
        File.WriteAllText(proofPath, "existing proof");
        using var openProof = new FileStream(
            proofPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var repaired = DeletedCopyrightProofWorkflowRecoveryService.RepairExistingProject(
            fixture.SourceDir,
            fixture.NewTitle);

        repaired.Should().BeTrue();
        Directory.Exists(fixture.CurrentWorkflowDir).Should().BeFalse();
        Directory.Exists(fixture.DesiredWorkflowDir).Should().BeTrue();
        openProof.Length.Should().BeGreaterThan(0);
        File.Exists(Path.Combine(fixture.DesiredWorkflowDir, "短剧信息.txt"))
            .Should()
            .BeTrue();
    }

    private static string ReadMetadataPath(string directory, string propertyName)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, "shortdrama-project.json")));
        return document.RootElement.GetProperty(propertyName).GetString() ?? "";
    }

    private sealed class RecoveryFixture : IDisposable
    {
        private RecoveryFixture(
            string workspace,
            string sourceDir,
            string currentWorkflowDir,
            string desiredWorkflowDir,
            string newTitle)
        {
            Workspace = workspace;
            SourceDir = sourceDir;
            CurrentWorkflowDir = currentWorkflowDir;
            DesiredWorkflowDir = desiredWorkflowDir;
            NewTitle = newTitle;
        }

        public string Workspace { get; }
        public string SourceDir { get; }
        public string CurrentWorkflowDir { get; }
        public string DesiredWorkflowDir { get; }
        public string NewTitle { get; }

        public static RecoveryFixture Create()
        {
            const string originalTitle = "怪谈玩家，但画风不对";
            const string newTitle = "诡异游戏里我反成大反派";
            var workspace = Path.Combine(
                Path.GetTempPath(),
                $"deleted-proof-workflow-{Guid.NewGuid():N}");
            var sourceDir = Path.Combine(workspace, originalTitle);
            var currentWorkflowDir = Path.Combine(
                workspace,
                "workflow",
                "_" + originalTitle);
            var desiredWorkflowDir = Path.Combine(
                workspace,
                "workflow",
                "_" + newTitle);
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(currentWorkflowDir);
            Directory.CreateDirectory(desiredWorkflowDir);
            WriteMetadata(sourceDir, sourceDir, currentWorkflowDir, originalTitle, newTitle);
            WriteMetadata(
                currentWorkflowDir,
                sourceDir,
                currentWorkflowDir,
                originalTitle,
                newTitle);
            File.WriteAllText(
                Path.Combine(currentWorkflowDir, "短剧信息.txt"),
                $"""
                 新剧名: {newTitle}
                 原剧名: {originalTitle}
                 集数: 51
                 """);
            return new RecoveryFixture(
                workspace,
                sourceDir,
                currentWorkflowDir,
                desiredWorkflowDir,
                newTitle);
        }

        public void Dispose()
        {
            if (Directory.Exists(Workspace))
                Directory.Delete(Workspace, recursive: true);
        }

        private static void WriteMetadata(
            string directory,
            string sourceProjectDir,
            string workflowProjectDir,
            string originalTitle,
            string newTitle)
        {
            File.WriteAllText(
                Path.Combine(directory, "shortdrama-project.json"),
                JsonSerializer.Serialize(new
                {
                    title = originalTitle,
                    originalTitle,
                    newTitle,
                    sourceProjectDir,
                    workflowProjectDir,
                    workflowDirName = Path.GetFileName(workflowProjectDir),
                }));
        }
    }
}
