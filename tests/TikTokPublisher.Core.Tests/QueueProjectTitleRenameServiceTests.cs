using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueProjectTitleRenameServiceTests
{
    [Fact]
    public void RenameNewTitle_SyncsWorkflowFilesQueueStateAndUploadCaches()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"queue-title-rename-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "source-project");
        var oldWorkflowDir = Path.Combine(workspace, "workflow", "_旧剧名");
        var newWorkflowDir = Path.Combine(workspace, "workflow", "_新剧名");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(oldWorkflowDir);

        try
        {
            WriteMetadata(sourceDir, sourceDir, oldWorkflowDir, "_旧剧名", "旧剧名");
            WriteMetadata(oldWorkflowDir, sourceDir, oldWorkflowDir, "_旧剧名", "旧剧名");
            File.WriteAllText(
                Path.Combine(sourceDir, "短剧信息.txt"),
                "原剧名: 原剧名\n短标题: 旧剧名\n");
            File.WriteAllText(
                Path.Combine(oldWorkflowDir, "短剧信息.txt"),
                "新剧名: 旧剧名\n原剧名: 原剧名\n剧名: 旧剧名\n短标题: 旧剧名\n集数: 12\n");

            WorkspaceQueueDatabase.EnsureDatabase(WorkspaceQueuePaths.QueueDatabasePath(workspace));
            var item = new QueueProjectItem
            {
                ProjectDir = sourceDir,
                DisplayName = "source-project",
                OriginalTitle = "原剧名",
                NewTitle = "旧剧名",
                EpisodeCount = 12,
                Enabled = true,
                StatusText = QueueStepStatus.Failed,
                CurrentStep = QueueStepKeys.UploadSeries,
                LastError = "旧标题上传失败",
                StepStates = new Dictionary<string, string>
                {
                    [QueueStepKeys.Download] = QueueStepStatus.Completed,
                    [QueueStepKeys.RewriteInfo] = QueueStepStatus.Failed,
                    [QueueStepKeys.GeneratePoster] = QueueStepStatus.Completed,
                    [QueueStepKeys.MaterialValidate] = QueueStepStatus.Completed,
                    [QueueStepKeys.UploadSeries] = QueueStepStatus.Failed,
                },
            };
            item.NormalizeStepStates();
            WorkspaceQueueDatabase.Save(workspace, [item], new Dictionary<string, object?>
            {
                ["prefer_upload_when_ready"] = true,
            });

            TikTokUploadStateStore.SaveState(oldWorkflowDir, new Dictionary<string, object?>
            {
                ["last_upload_title"] = "旧剧名",
                ["platform_series_lookup"] = new Dictionary<string, object?>
                {
                    ["status"] = "not_found",
                    ["searched_titles"] = new List<string> { "旧剧名" },
                },
            });
            ProjectStateDocumentStore.SaveDocument(
                workspace,
                sourceDir,
                TikTokUploadManifestService.DocumentType,
                new Dictionary<string, object?>
                {
                    ["display_title"] = "旧剧名",
                    ["workflow_project_dir"] = oldWorkflowDir,
                },
                oldWorkflowDir);
            File.WriteAllText(
                Path.Combine(oldWorkflowDir, "tiktok-upload-manifest.json"),
                JsonSerializer.Serialize(new Dictionary<string, object?> { ["display_title"] = "旧剧名" }));

            var result = QueueProjectTitleRenameService.RenameNewTitle(workspace, sourceDir, "新剧名");

            result.OldTitle.Should().Be("旧剧名");
            result.NewTitle.Should().Be("新剧名");
            result.WorkflowDirectoryRenamed.Should().BeTrue();
            result.ResetUpload.Should().BeTrue();
            result.ResetPoster.Should().BeFalse();
            result.ResetMaterialValidate.Should().BeFalse();
            Directory.Exists(oldWorkflowDir).Should().BeFalse();
            Directory.Exists(newWorkflowDir).Should().BeTrue();

            var workflowInfo = ProjectInfoTextHelper.ParseInfoFile(Path.Combine(newWorkflowDir, "短剧信息.txt"));
            workflowInfo["新剧名"].Should().Be("新剧名");
            workflowInfo["剧名"].Should().Be("新剧名");
            workflowInfo["短标题"].Should().Be("新剧名");
            var sourceInfo = ProjectInfoTextHelper.ParseInfoFile(Path.Combine(sourceDir, "短剧信息.txt"));
            sourceInfo["短标题"].Should().Be("新剧名");

            var sourceMetadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(Path.Combine(sourceDir, "shortdrama-project.json")))!;
            sourceMetadata["newTitle"].GetString().Should().Be("新剧名");
            sourceMetadata["workflowDirName"].GetString().Should().Be("_新剧名");
            sourceMetadata["workflowProjectDir"].GetString().Should().Be(newWorkflowDir);

            var renamed = WorkspaceQueueService.ScanProjects(workspace).Single();
            renamed.NewTitle.Should().Be("新剧名");
            renamed.StatusText.Should().Be(QueueStepStatus.Pending);
            renamed.LastError.Should().BeEmpty();
            renamed.StepStates[QueueStepKeys.RewriteInfo].Should().Be(QueueStepStatus.Completed);
            renamed.StepStates[QueueStepKeys.GeneratePoster].Should().Be(QueueStepStatus.Completed);
            renamed.StepStates[QueueStepKeys.MaterialValidate].Should().Be(QueueStepStatus.Completed);
            renamed.StepStates[QueueStepKeys.UploadSeries].Should().Be(QueueStepStatus.Pending);

            var state = TikTokUploadStateStore.LoadState(newWorkflowDir);
            state["last_upload_title"].GetString().Should().Be("新剧名");
            state.Should().NotContainKey("platform_series_lookup");

            var manifest = ProjectStateDocumentStore.LoadDocument(
                workspace,
                sourceDir,
                TikTokUploadManifestService.DocumentType);
            manifest["display_title"].GetString().Should().Be("新剧名");
            var legacyManifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(Path.Combine(newWorkflowDir, "tiktok-upload-manifest.json")))!;
            legacyManifest["display_title"].GetString().Should().Be("新剧名");
        }
        finally
        {
            DeleteDirectoryWithRetry(workspace);
        }
    }

    private static void WriteMetadata(
        string dir,
        string sourceDir,
        string workflowDir,
        string workflowDirName,
        string newTitle)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "shortdrama-project.json"),
            JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["sourceProjectDir"] = sourceDir,
                    ["workflowProjectDir"] = workflowDir,
                    ["workflowDirName"] = workflowDirName,
                    ["title"] = "原剧名",
                    ["originalTitle"] = "原剧名",
                    ["newTitle"] = newTitle,
                    ["new_title"] = newTitle,
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        if (!Directory.Exists(path)) return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }

        try { Directory.Delete(path, recursive: true); }
        catch { /* SQLite may keep WAL files locked briefly on Windows test runs. */ }
    }
}
