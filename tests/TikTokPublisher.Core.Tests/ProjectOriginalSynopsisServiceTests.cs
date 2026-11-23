using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

[Collection("Account AI data root")]
public sealed class ProjectOriginalSynopsisServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Backfill_reads_plain_intro_and_preserves_existing_text_and_encoding(bool emitBom)
    {
        var workspace = Directory.CreateTempSubdirectory("original-synopsis-");
        var source = workspace.CreateSubdirectory("Case of the Harbor").FullName;
        try
        {
            var workflow = ProjectWorkspaceService.PrepareWorkflowProject(source);
            var infoPath = Path.Combine(workflow, "短剧信息.txt");
            var originalPath = Path.Combine(source, "简介.txt");
            const string before = "原剧名: Case of the Harbor\r\n新剧名: Harbor Case Renewed\r\n简介: Existing publish text.\r\n自定义字段：保留\r\n";
            var encoding = new UTF8Encoding(emitBom);
            File.WriteAllText(infoPath, before, encoding);
            File.WriteAllText(originalPath, "Mira finds the old ledger.\r\nHer brother follows the clue.");

            ProjectOriginalSynopsisService.Backfill(workspace.FullName, source, null).Should().Be(OriginalSynopsisBackfillResult.Added);
            var expected = before.Replace("简介: Existing", "原简介: Mira finds the old ledger. Her brother follows the clue.\r\n简介: Existing");
            File.ReadAllBytes(infoPath).Should().Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)));

            File.WriteAllText(originalPath, "A later import contains a different story.");
            ProjectOriginalSynopsisService.Backfill(workspace.FullName, workflow, null).Should().Be(OriginalSynopsisBackfillResult.AlreadyPresent);
            ProjectOriginalSynopsisService.Resolve(
                ProjectWorkspaceService.LoadContext(source), "Case of the Harbor", ProjectInfoTextHelper.ParseInfoFile(infoPath))
                .Should().Be("Mira finds the old ledger. Her brother follows the clue.");
            File.ReadAllText(infoPath).Should().Be(expected);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public void Backfill_leaves_a_workflow_only_project_unchanged_without_a_reliable_source()
    {
        var workspace = Directory.CreateTempSubdirectory("original-synopsis-");
        var previousDataRoot = AppPaths.DataRootOverride;
        AppPaths.DataRootOverride = Path.Combine(workspace.FullName, "test-data");
        try
        {
            var workflow = workspace.CreateSubdirectory("workflow").CreateSubdirectory("Only workflow").FullName;
            var infoPath = Path.Combine(workflow, "短剧信息.txt");
            const string saved = "原剧名: Only workflow\n新剧名: A new title\n简介: Already rewritten.\n";
            File.WriteAllText(infoPath, saved);

            ProjectOriginalSynopsisService.Backfill(workspace.FullName, workflow, null).Should()
                .Be(OriginalSynopsisBackfillResult.SourceMissing);
            File.ReadAllText(infoPath).Should().Be(saved);
            File.Exists(AppPaths.AppDatabaseFile).Should().BeFalse();
        }
        finally
        {
            AppPaths.DataRootOverride = previousDataRoot;
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public void Backfill_uses_the_earliest_trusted_history_for_the_same_project()
    {
        var workspace = Directory.CreateTempSubdirectory("original-synopsis-");
        var previousDataRoot = AppPaths.DataRootOverride;
        AppPaths.DataRootOverride = Path.Combine(workspace.FullName, "test-data");
        try
        {
            var source = workspace.CreateSubdirectory("Original Show").FullName;
            var workflow = ProjectWorkspaceService.PrepareWorkflowProject(source);
            var infoPath = Path.Combine(workflow, "短剧信息.txt");
            File.WriteAllText(infoPath, "原剧名: Original Show\n原简介: 暂无简介\n简介: Published rewrite.\n");
            AiRewriteHistoryRecord History(string dir, string original, string newTitle, string mode, string createdAt) =>
                new("Original Show", original, newTitle, "Published rewrite.", "Original Show", dir,
                    workspace.FullName, "account", "Test", $"{dir}#account#synopsis={mode}", "test", createdAt);
            AiRewriteHistoryService.Append(History(Path.Combine(workspace.FullName, "Other project"), "Other original", "Other title", "1", "2026-01-01"));
            AiRewriteHistoryService.Append(History(source, "A title-only run cannot establish this source", "Title change", "0", "2026-01-02"));
            AiRewriteHistoryService.Append(History(source, "The original from this project's history.", "First title", "1", "2026-01-03"));
            AiRewriteHistoryService.Append(History(source, "A later original", "Later title", "1", "2026-01-04"));

            ProjectOriginalSynopsisService.Backfill(workspace.FullName, source, null).Should().Be(OriginalSynopsisBackfillResult.Added);
            File.ReadAllText(infoPath).Should().Be(
                "原剧名: Original Show\n原简介: The original from this project's history.\n简介: Published rewrite.\n");
        }
        finally
        {
            AppPaths.DataRootOverride = previousDataRoot;
            SqliteConnection.ClearAllPools();
            workspace.Delete(recursive: true);
        }
    }
}
