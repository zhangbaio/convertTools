using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class LocalManualDramaImportServiceTests
{
    [Fact]
    public void Import_Creates_Project_Metadata_And_Queues_External_Local_Drama()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-import-workspace-{Guid.NewGuid():N}");
        var downloadRoot = Path.Combine(Path.GetTempPath(), $"manual-import-download-{Guid.NewGuid():N}");
        var source = Path.Combine(downloadRoot, "重回 95 订婚宴，我靠创业逆风翻盘");

        try
        {
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "第1集.mp4"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(source, "第2集.mp4"), [4, 5, 6]);
            File.WriteAllBytes(Path.Combine(source, "原始封面.jpg"), [7, 8, 9]);
            File.WriteAllText(Path.Combine(source, "简介.txt"), "本地手动下载剧集简介");

            var result = LocalManualDramaImportService.Import(workspace, source);

            result.SourceProjectDir.Should().Be(Path.GetFullPath(source));
            result.EpisodeCount.Should().Be(2);
            result.WorkflowProjectDir.Should().StartWith(Path.Combine(Path.GetFullPath(workspace), "workflow"));
            Directory.Exists(result.WorkflowProjectDir).Should().BeTrue();

            var metadataPath = Path.Combine(source, "shortdrama-project.json");
            File.Exists(metadataPath).Should().BeTrue();
            using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
            metadata.RootElement.GetProperty("sourceProjectDir").GetString().Should().Be(Path.GetFullPath(source));
            metadata.RootElement.GetProperty("workflowProjectDir").GetString().Should().Be(result.WorkflowProjectDir);
            metadata.RootElement.GetProperty("episodeCount").GetInt32().Should().Be(2);
            metadata.RootElement.GetProperty("intro").GetString().Should().Be("本地手动下载剧集简介");
            File.Exists(Path.Combine(source, "海报原图.jpg")).Should().BeTrue();
            File.Exists(Path.Combine(result.WorkflowProjectDir, "海报原图.jpg")).Should().BeTrue();

            WorkspaceQueueService.AddProjectsToQueue(
                workspace,
                [source],
                "acct-current",
                "当前账号").Should().ContainSingle();

            var item = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;
            item.ProjectDir.Should().Be(Path.GetFullPath(source));
            item.AccountProfileId.Should().Be("acct-current");
            item.AccountProfileName.Should().Be("当前账号");
            item.EpisodeCount.Should().Be(2);
            Path.GetFileName(item.PrimaryVideoPath).Should().Be("第1集.mp4");
            item.StepStates[QueueStepKeys.GeneratePoster].Should().Be(
                QueueStepStatus.Pending,
                "本地原始封面只是输入素材，必须按改写后的新剧名执行生成海报步骤");
        }
        finally
        {
            DeleteBestEffort(workspace);
            DeleteBestEffort(downloadRoot);
        }
    }

    [Fact]
    public void ScanProjects_Local_Manual_Poster_Requires_Current_Title_Generation_State()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-poster-state-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "本地原剧名");

        try
        {
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "第1集.mp4"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(source, "原始封面.png"), [4, 5, 6]);

            var import = LocalManualDramaImportService.Import(workspace, source);
            var item = WorkspaceQueueService.AddProjectsToQueue(workspace, [source]).Should().ContainSingle().Subject;
            item.StepStates[QueueStepKeys.GeneratePoster] = QueueStepStatus.Completed;
            WorkspaceQueueService.SaveProjects(workspace, [item]);

            var repaired = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;
            repaired.StepStates[QueueStepKeys.GeneratePoster].Should().Be(
                QueueStepStatus.Pending,
                "历史版本误标完成但没有真实生成状态时必须自动修复");

            var inputPath = Path.Combine(source, "海报原图.png");
            var outputPath = Path.Combine(import.WorkflowProjectDir, TikTokPosterGenerationStateService.OutputFileName);
            File.WriteAllBytes(outputPath, [7, 8, 9]);
            TikTokPosterGenerationStateService.SaveGeneratedState(
                repaired,
                new ClientSettings(),
                inputPath,
                outputPath);
            repaired.StepStates[QueueStepKeys.GeneratePoster] = QueueStepStatus.Completed;
            WorkspaceQueueService.SaveProjects(workspace, [repaired]);

            var current = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;
            current.StepStates[QueueStepKeys.GeneratePoster].Should().Be(QueueStepStatus.Completed);

            ProjectInfoTextHelper.UpdateFields(
                Path.Combine(import.WorkflowProjectDir, "短剧信息.txt"),
                new Dictionary<string, string> { ["新剧名"] = "全新剧名" });

            var renamed = WorkspaceQueueService.ScanProjects(workspace).Should().ContainSingle().Subject;
            renamed.NewTitle.Should().Be("全新剧名");
            renamed.StepStates[QueueStepKeys.GeneratePoster].Should().Be(
                QueueStepStatus.Pending,
                "海报生成状态中的剧名与当前新剧名不一致时必须重新生成");
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    [Fact]
    public void Poster_State_Video_Frame_Fallback_Does_Not_Repeat_For_Unchanged_Request()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-poster-fallback-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "本地原剧名");

        try
        {
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "第1集.mp4"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(source, "原始封面.png"), [4, 5, 6]);

            var import = LocalManualDramaImportService.Import(workspace, source);
            var item = WorkspaceQueueService.AddProjectsToQueue(workspace, [source]).Should().ContainSingle().Subject;
            var inputPath = Path.Combine(source, "海报原图.png");
            var outputPath = Path.Combine(import.WorkflowProjectDir, TikTokPosterGenerationStateService.OutputFileName);
            File.WriteAllBytes(outputPath, [7, 8, 9]);
            var requestedSettings = new ClientSettings { PosterMode = "video_frame" };

            TikTokPosterGenerationStateService.SaveGeneratedState(
                item,
                requestedSettings,
                inputPath,
                outputPath,
                effectivePosterMode: "original");

            TikTokPosterGenerationStateService.NeedsGeneratePoster(item, requestedSettings).Should().BeFalse(
                "视频抽帧失败并回退成功后，相同请求不应在每次队列执行时重复生成");

            var switchedProviderSettings = requestedSettings.Clone();
            switchedProviderSettings.ImageProvider = "ofox_image2";
            TikTokPosterGenerationStateService.NeedsGeneratePoster(item, switchedProviderSettings).Should().BeFalse(
                "切换图片提供商只影响后续生成，不应让已经完成的海报失效");

            var changedSettings = requestedSettings.Clone();
            changedSettings.FrameExtractTime += 1.0;
            TikTokPosterGenerationStateService.NeedsGeneratePoster(item, changedSettings).Should().BeFalse(
                "已有生成记录和有效海报时，配置变化只影响显式强制重跑后的新海报");
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    [Fact]
    public void Import_Truncates_Local_Videos_To_Configured_Keep_Count_Without_Deleting_Files()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-import-truncate-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "超长本地剧");

        try
        {
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "第1集.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(source, "第2集.mp4"), [2]);
            File.WriteAllBytes(Path.Combine(source, "第3集.mp4"), [3]);
            File.WriteAllText(Path.Combine(source, "短剧信息.txt"), "剧名: 超长本地剧\n集数: 3\n");
            var settings = new ClientSettings
            {
                TiktokAllowOverLimitUploadImport = true,
                TiktokOverLimitDownloadEpisodeCount = 2,
            };

            var result = LocalManualDramaImportService.Import(workspace, source, settings: settings);

            result.EpisodeCount.Should().Be(2);
            Directory.EnumerateFiles(source, "*.mp4").Should().HaveCount(3);
            ProjectVideoResolver.ResolveUploadVideos(source).Should().HaveCount(2);
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "shortdrama-project.json")));
            metadata.RootElement.GetProperty("originalEpisodeCount").GetInt32().Should().Be(3);
            metadata.RootElement.GetProperty("effectiveEpisodeCount").GetInt32().Should().Be(2);
            metadata.RootElement.GetProperty("truncatedForTikTokUpload").GetBoolean().Should().BeTrue();
            File.ReadAllText(Path.Combine(source, "短剧信息.txt")).Should().Contain("集数: 2");
            File.ReadAllText(Path.Combine(result.WorkflowProjectDir, "短剧信息.txt")).Should().Contain("集数: 2");
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    [Fact]
    public void ListCandidates_Sorts_Mixed_Numeric_And_Text_Video_Names()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-import-mixed-names-{Guid.NewGuid():N}");
        var source = Path.Combine(workspace, "混合命名短剧");

        try
        {
            Directory.CreateDirectory(source);
            foreach (var name in new[] { "1.mp4", "10.mp4", "2.mp4", "第1集.mp4", "3000000000.mp4" })
                File.WriteAllBytes(Path.Combine(source, name), [1, 2, 3]);

            var candidate = LocalManualDramaImportService.ListCandidates(workspace).Should().ContainSingle().Subject;
            candidate.EpisodeCount.Should().Be(5);
            LocalManualDramaImportService.Import(workspace, source).EpisodeCount.Should().Be(5);
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    private const string HongguoTitle = "珩珩漫漫寻恩路";
    private const string HongguoBookId = "7693110299905231896";
    private const string HongguoJpegUrl = "https://p3-novel.byteimg.com/novel-pic/cover.jpeg";
    private const string HongguoIntro =
        "十二岁那年，许清禾在陌生乡村遇见少年陈舟。两人相依走过风雨，多年后她带着恩情回到故乡，终于找到当年那盏为她亮着的灯。";

    [Fact]
    public void Import_Hongguo_Download_Folder_Uses_Real_Title_Intro_And_Book_Id()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-import-hongguo-{Guid.NewGuid():N}");
        try
        {
            var source = WriteHongguoDownloadFolder(workspace);

            var candidate = LocalManualDramaImportService.ListCandidates(workspace).Should().ContainSingle().Subject;
            candidate.DisplayName.Should().Be(HongguoTitle);

            var result = LocalManualDramaImportService.Import(workspace, source);

            result.DisplayName.Should().Be(HongguoTitle);
            Path.GetFileName(result.WorkflowProjectDir).Should().Be("_" + HongguoTitle);
            using (var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "shortdrama-project.json"))))
            {
                var root = metadata.RootElement;
                foreach (var key in new[] { "displayName", "sourceName", "title", "originalTitle" })
                    root.GetProperty(key).GetString().Should().Be(HongguoTitle, key);
                root.GetProperty("bookId").GetString().Should().Be(HongguoBookId);
                root.GetProperty("posterUrl").GetString().Should().Be(HongguoJpegUrl);
                root.GetProperty("intro").GetString().Should().Be(HongguoIntro);
                root.GetProperty("category").GetString().Should().Be("都市日常");
                root.GetProperty("declaredEpisodeCount").GetInt32().Should().Be(56);
            }

            var info = ProjectInfoTextHelper.ParseInfoFile(Path.Combine(result.WorkflowProjectDir, "短剧信息.txt"));
            info["原剧名"].Should().Be(HongguoTitle);
            info["新剧名"].Should().Be(HongguoTitle);
            info["简介"].Should().Be(HongguoIntro);
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    [Fact]
    public void Reimport_Hongguo_Download_Folder_Keeps_Rewritten_New_Title()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-import-hongguo-rewrite-{Guid.NewGuid():N}");
        try
        {
            var source = WriteHongguoDownloadFolder(workspace);
            LocalManualDramaImportService.Import(workspace, source);
            var renamedWorkflowDir = ProjectWorkspaceService.SyncWorkflowProjectDirName(source, "寻恩之路");
            var infoPath = Path.Combine(renamedWorkflowDir, "短剧信息.txt");
            ProjectInfoTextHelper.UpdateFields(infoPath, new Dictionary<string, string> { ["新剧名"] = "寻恩之路" });

            var result = LocalManualDramaImportService.Import(workspace, source);

            result.WorkflowProjectDir.Should().Be(renamedWorkflowDir);
            var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
            info["原剧名"].Should().Be(HongguoTitle);
            info["新剧名"].Should().Be("寻恩之路");
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    [Fact]
    public void Reimport_Hongguo_Download_Folder_Repairs_Folder_Name_Placeholders_And_Local_Book_Id()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-import-hongguo-legacy-{Guid.NewGuid():N}");
        try
        {
            var source = WriteHongguoDownloadFolder(workspace);
            var folderName = Path.GetFileName(source);
            var legacyWorkflowDir = Path.Combine(workspace, "workflow", "_" + folderName);
            Directory.CreateDirectory(legacyWorkflowDir);
            File.WriteAllText(
                Path.Combine(source, "shortdrama-project.json"),
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["displayName"] = folderName,
                    ["sourceName"] = folderName,
                    ["title"] = folderName,
                    ["originalTitle"] = folderName,
                    ["bookId"] = "local:0123456789abcdef",
                    ["workflowDirName"] = "_" + folderName,
                    ["workflowProjectDir"] = legacyWorkflowDir,
                    ["sourceProjectDir"] = source,
                    ["localManualImport"] = true,
                }));
            var legacyInfoPath = Path.Combine(legacyWorkflowDir, "短剧信息.txt");
            File.WriteAllText(legacyInfoPath, $"新剧名: {folderName}\n原剧名: {folderName}\n集数: 3\n");

            var result = LocalManualDramaImportService.Import(workspace, source);

            result.DisplayName.Should().Be(HongguoTitle);
            result.WorkflowProjectDir.Should().Be(legacyWorkflowDir);
            using (var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "shortdrama-project.json"))))
            {
                var root = metadata.RootElement;
                foreach (var key in new[] { "displayName", "sourceName", "title", "originalTitle" })
                    root.GetProperty(key).GetString().Should().Be(HongguoTitle, key);
                root.GetProperty("bookId").GetString().Should().Be(HongguoBookId);
            }

            var info = ProjectInfoTextHelper.ParseInfoFile(legacyInfoPath);
            info["原剧名"].Should().Be(HongguoTitle);
            info["新剧名"].Should().Be(HongguoTitle);
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    [Fact]
    public void Import_Hongguo_Folder_Without_Info_Files_Strips_Prefix()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"manual-import-hongguo-bare-{Guid.NewGuid():N}");
        try
        {
            var source = WriteHongguoDownloadFolder(workspace, introText: false, downloaderMetadata: false);

            var result = LocalManualDramaImportService.Import(workspace, source);

            result.DisplayName.Should().Be(HongguoTitle);
            Path.GetFileName(result.WorkflowProjectDir).Should().Be("_" + HongguoTitle);
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "shortdrama-project.json")));
            metadata.RootElement.GetProperty("bookId").GetString().Should().StartWith("local:");
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    private static string WriteHongguoDownloadFolder(
        string workspace,
        bool introText = true,
        bool downloaderMetadata = true)
    {
        var source = Path.Combine(workspace, "红果_" + HongguoTitle);
        Directory.CreateDirectory(source);
        foreach (var index in new[] { 1, 2, 3 })
            File.WriteAllBytes(Path.Combine(source, $"{index}.mp4"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(source, "封面.jpg"), [7, 8, 9]);
        if (introText)
        {
            File.WriteAllText(
                Path.Combine(source, "简介.txt"),
                string.Join('\n',
                    "══════════════════════════════════",
                    "          短 剧 简 介",
                    "══════════════════════════════════",
                    "",
                    $"【剧\u3000\u3000名】{HongguoTitle}",
                    "【评\u3000\u3000分】9.8 分",
                    "【总 集 数】共 56 集",
                    "【分\u3000\u3000类】都市日常",
                    $"【短 剧 ID】{HongguoBookId}",
                    "──────────────────────────────────",
                    "【剧情简介】",
                    HongguoIntro,
                    "──────────────────────────────────",
                    "",
                    "【封面链接】http://p6-novelfm-sign.byteimg.com/cover.heic?x-expires=1",
                    "【版权信息】",
                    "版权信息：本书的数字版权由测试公司提供并授权发行。"));
        }

        if (downloaderMetadata)
        {
            File.WriteAllText(
                Path.Combine(source, "metadata.json"),
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["categories"] = Array.Empty<string>(),
                    ["cover_url"] = HongguoJpegUrl,
                    ["description"] = HongguoIntro,
                    ["drama_id"] = HongguoBookId,
                    ["episode_count"] = 56,
                    ["intro"] = HongguoIntro,
                    ["title"] = HongguoTitle,
                }));
        }

        return source;
    }

    private static void DeleteBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // SQLite on Windows may still hold the queue db briefly after a save.
        }
    }
}
