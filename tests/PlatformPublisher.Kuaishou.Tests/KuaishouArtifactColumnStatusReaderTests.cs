using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouArtifactColumnStatusReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ks-artifact-status-" + Guid.NewGuid().ToString("N"));

    public KuaishouArtifactColumnStatusReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void PersonalArtifacts_MatchAssistantColumnStatus()
    {
        var source = Path.Combine(_root, "再赴朱筵换此生");
        var workflow = Path.Combine(_root, "workflow", "_重回订婚寻新生");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(workflow, "videos"));
        Write(Path.Combine(source, "第1集.mp4"), 1);
        Write(Path.Combine(source, "shortdrama-project.json"), """
            {
              "originalTitle": "再赴朱筵换此生",
              "title": "再赴朱筵换此生",
              "episodeCount": 1,
              "workflowDirName": "_重回订婚寻新生",
              "workflowProjectDir": "PLACEHOLDER",
              "sourceProjectDir": "SOURCE"
            }
            """.Replace("PLACEHOLDER", workflow.Replace("\\", "\\\\")).Replace("SOURCE", source.Replace("\\", "\\\\")));
        Write(Path.Combine(source, ".weixin-channel-download-state.json"), """
            { "ok": true, "stopped": false, "episode_mappings": [ {"episode": 1} ], "message": "下载完成" }
            """);
        Write(Path.Combine(workflow, "短剧信息.txt"), """
            原剧名：再赴朱筵换此生
            集数：1
            新剧名：重回订婚寻新生
            """);
        Write(Path.Combine(workflow, "快手横屏封面.jpg"), 1);
        Write(Path.Combine(workflow, "快手竖屏海报.jpg"), 1);
        for (var index = 1; index <= 4; index++) Write(Path.Combine(workflow, $"工程图_{index}.png"), 1);
        Write(Path.Combine(workflow, "kuaishou-auto-fill.json"), """
            { "description": "简介", "audience_gender": 2, "plot_list": [1], "tag_list": [2] }
            """);
        Write(Path.Combine(workflow, ".kuaishou-personal-prepare-state.json"), """
            {
              "status": "materials_ready",
              "episode_count": 1,
              "source_video_dir": "SOURCE",
              "completed_prepare_steps": ["download", "rewrite_info", "generate_poster", "generate_project_images", "auto_fill_info"]
            }
            """.Replace("SOURCE", source.Replace("\\", "\\\\")));

        var status = KuaishouArtifactColumnStatusReader.Read(source, enterprise: false);

        Assert.Equal("再赴朱筵换此生", status.OriginalTitle);
        Assert.Equal("重回订婚寻新生", status.NewTitle);
        Assert.Equal(1, status.EpisodeCount);
        Assert.Equal("已完成", status.DownloadStatus);
        Assert.Equal("已完成", status.RewriteStatus);
        Assert.Equal("已完成", status.PosterStatus);
        Assert.Equal("已完成 4图", status.ProjectImageStatus);
        Assert.Equal("已完成", status.AutoFillStatus);
        Assert.Equal("未执行", status.UploadStatus);
        Assert.Equal("待执行", status.OverallStatus);
    }

    [Fact]
    public void UploadStateRunning_ShowsProgressAndCountsWorkflowVideos()
    {
        var source = Path.Combine(_root, "名为女友的猎人");
        var workflow = Path.Combine(_root, "workflow", "_假面女友寻真相");
        var videos = Path.Combine(workflow, "videos");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(videos);
        Write(Path.Combine(videos, "第1集.mp4"), 1);
        Write(Path.Combine(videos, "第2集.mp4"), 1);
        Write(Path.Combine(source, "shortdrama-project.json"), """
            {
              "originalTitle": "名为女友的猎人",
              "workflowProjectDir": "PLACEHOLDER",
              "sourceProjectDir": "SOURCE"
            }
            """.Replace("PLACEHOLDER", workflow.Replace("\\", "\\\\")).Replace("SOURCE", source.Replace("\\", "\\\\")));
        Write(Path.Combine(workflow, "短剧信息.txt"), "原剧名：名为女友的猎人\n新剧名：假面女友寻真相\n");
        Write(Path.Combine(workflow, ".kuaishou-personal-prepare-state.json"), """
            { "status": "materials_ready", "source_video_dir": "SOURCE", "completed_prepare_steps": ["download", "rewrite_info"] }
            """.Replace("SOURCE", source.Replace("\\", "\\\\")));
        Write(Path.Combine(workflow, ".kuaishou-personal-upload-state.json"), """
            {
              "status": "running",
              "episode_count": 55,
              "uploaded_episode_count": 0,
              "current_stage": "upload_series_videos"
            }
            """);

        var status = KuaishouArtifactColumnStatusReader.Read(source, enterprise: false);

        Assert.Equal("假面女友寻真相", status.NewTitle);
        Assert.Equal(55, status.EpisodeCount);
        Assert.Equal("已完成", status.DownloadStatus);
        Assert.Equal("已完成", status.RewriteStatus);
        Assert.Equal("未执行", status.PosterStatus);
        Assert.Equal("进行中 0/55", status.UploadStatus);
        Assert.Equal("进行中", status.OverallStatus);
    }

    [Fact]
    public void MissingArtifacts_StayUnstarted()
    {
        var source = Path.Combine(_root, "早餐店的温暖时光");
        Directory.CreateDirectory(source);

        var status = KuaishouArtifactColumnStatusReader.Read(source, enterprise: false);

        Assert.Equal("早餐店的温暖时光", status.OriginalTitle);
        Assert.Equal("早餐店的温暖时光", status.NewTitle);
        Assert.Equal(0, status.EpisodeCount);
        Assert.Equal("未执行", status.DownloadStatus);
        Assert.Equal("未执行", status.RewriteStatus);
        Assert.Equal("未执行", status.PosterStatus);
        Assert.Equal("未执行", status.ProjectImageStatus);
        Assert.Equal("未执行", status.AutoFillStatus);
        Assert.Equal("未执行", status.UploadStatus);
        Assert.Equal("待执行", status.OverallStatus);
    }

    [Fact]
    public void PartialProjectImages_ReportCompletedCount()
    {
        var source = Path.Combine(_root, "九号当铺");
        var workflow = Path.Combine(_root, "workflow", "_九号当铺");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        Write(Path.Combine(source, "shortdrama-project.json"), $$"""
            { "workflowProjectDir": "{{workflow.Replace("\\", "\\\\")}}" }
            """);
        Write(Path.Combine(workflow, "工程图_1.jpg"), 1);
        Write(Path.Combine(workflow, "工程图_2.png"), 1);

        var status = KuaishouArtifactColumnStatusReader.Read(source, enterprise: false);

        Assert.Equal("已完成 2图", status.ProjectImageStatus);
    }

    [Fact]
    public void DownloadFailure_WinsOverCompletedMarker()
    {
        var source = Path.Combine(_root, "失败剧");
        Directory.CreateDirectory(source);
        Write(Path.Combine(source, ".weixin-channel-download-state.json"), """{ "ok": false, "message": "中断" }""");
        Write(Path.Combine(source, ".kuaishou-personal-prepare-state.json"), """
            { "status": "failed", "failed_step": "poster", "completed_prepare_steps": ["download"] }
            """);

        var status = KuaishouArtifactColumnStatusReader.Read(source, enterprise: false);

        Assert.Equal("失败", status.DownloadStatus);
        Assert.Equal("失败", status.OverallStatus);
    }

    [Fact]
    public void EnterpriseArtifacts_DoNotReadPersonalFiles()
    {
        var source = Path.Combine(_root, "企业剧");
        var workflow = Path.Combine(_root, "workflow", "_企业新名");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        Write(Path.Combine(source, "shortdrama-project.json"), $$"""
            { "originalTitle": "企业剧", "workflowProjectDir": "{{workflow.Replace("\\", "\\\\")}}" }
            """);
        Write(Path.Combine(workflow, "短剧信息.txt"), "原剧名：企业剧\n新剧名：企业新剧名\n");
        Write(Path.Combine(workflow, "kuaishou-auto-fill.json"), """
            { "description": "个人", "audience_gender": 1, "plot_list": [1], "tag_list": [1] }
            """);
        Write(Path.Combine(workflow, "kuaishou-enterprise-auto-fill.json"), """
            { "description": "", "audience_gender": 2, "plot_list": [], "tag_list": [1] }
            """);
        Write(Path.Combine(workflow, ".kuaishou-enterprise-prepare-state.json"), """
            { "status": "local_steps_done", "completed_prepare_steps": { "rewrite_info": true, "auto_fill_info": false } }
            """);
        Write(Path.Combine(workflow, ".kuaishou-enterprise-upload-state.json"), """
            { "status": "running", "uploaded_episode_count": 1, "episode_count": 3, "response_summary": { "status": "success", "uploaded_episode_count": 3, "episode_count": 3 } }
            """);

        var status = KuaishouArtifactColumnStatusReader.Read(source, enterprise: true);

        Assert.Equal("企业新剧名", status.NewTitle);
        Assert.Equal("已完成", status.RewriteStatus);
        Assert.Equal("未执行", status.AutoFillStatus);
        Assert.Equal("已完成", status.UploadStatus);
        Assert.Equal("已完成", status.OverallStatus);
    }

    [Fact]
    public void PosterFilesWithoutCompletedStep_AreReady()
    {
        var source = Path.Combine(_root, "海报剧");
        var workflow = Path.Combine(_root, "workflow", "_海报新名");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workflow);
        Write(Path.Combine(source, "shortdrama-project.json"), $$"""
            { "workflowProjectDir": "{{workflow.Replace("\\", "\\\\")}}" }
            """);
        Write(Path.Combine(workflow, "短剧信息.txt"), "原剧名：海报剧\n新剧名：海报新名\n");
        Write(Path.Combine(workflow, "快手横屏封面.jpg"), 1);
        Write(Path.Combine(workflow, "快手竖屏海报.jpg"), 1);

        var status = KuaishouArtifactColumnStatusReader.Read(source, enterprise: false);

        Assert.Equal("已完成", status.PosterStatus);
        Assert.Equal("已完成", status.RewriteStatus);
    }

    private static void Write(string path, string content) => File.WriteAllText(path, content);

    private static void Write(string path, int bytes) => File.WriteAllBytes(path, new byte[bytes]);
}

public sealed class KuaishouFormChoicesTests
{
    [Fact]
    public void StoredCodes_DisplayAssistantLabels()
    {
        Assert.Equal("3", KuaishouFormChoices.CanonicalValue(KuaishouFormChoices.ComicMethods, "3", "1"));
        Assert.Equal("AIGC剧", KuaishouFormChoices.LabelOf(KuaishouFormChoices.ComicMethods, "3", "1"));
        Assert.Equal("AIGC剧", KuaishouFormChoices.LabelOf(KuaishouFormChoices.ComicMethods, "AIGC剧", "1"));
        Assert.Equal("auto", KuaishouFormChoices.CanonicalValue(KuaishouFormChoices.ProductionForms, "auto", "竖屏"));
        Assert.Equal("自动识别", KuaishouFormChoices.LabelOf(KuaishouFormChoices.ProductionForms, "auto", "竖屏"));
        Assert.Equal(3, KuaishouFormChoices.SaleTypeCode("观看广告解锁"));
        Assert.Equal(3, KuaishouFormChoices.SaleTypeCode("3"));
        Assert.Equal("含AI生成内容", KuaishouFormChoices.LabelOf(KuaishouFormChoices.AuthorDeclarations, "1", "0"));
        Assert.Equal("自有版权", KuaishouFormChoices.LabelOf(KuaishouFormChoices.CopyrightProofTypes, "1", "2"));
    }

    [Fact]
    public void Resolver_UsesAssistantRulesForEditedConfig()
    {
        var config = new KuaishouPersonalConfig
        {
            ProductionMethod = "3",
            ProductionForm = "auto",
            AuthorDeclaration = "1",
            SaleType = "3",
            BroadcastTimeMode = "today",
            RevenueShareFullSceneDisplay = true,
            FullSceneDisplay = false,
            KuaishouNickname = "星燃漫剧",
            AdvertiserId = "5162494128",
            Username = "张彪",
            Gender = "男",
            FreeEpisodeCount = 0,
            UnlockEpisodeCount = 0,
            EpisodePrice = "0.11",
            SpecialTheme = "false",
            ActorInfoJson = """[{"name":"顾承宇","gender":"男","role":"男主"}]""",
        };

        var form = KuaishouPersonalFormResolver.Resolve(config, videoWidth: 1080, videoHeight: 1920, today: new DateTime(2026, 10, 19));

        Assert.Equal("AIGC剧", form.ComicMethodLabel);
        Assert.Equal("竖屏", form.ProductionForm);
        Assert.Equal("含AI生成内容", form.AuthorDeclarationLabel);
        Assert.Equal("观看广告解锁", form.SaleTypeLabel);
        Assert.Equal("2026-10-19", form.BroadcastDate);
        Assert.True(form.FullSceneDisplay);
        Assert.Equal("星燃漫剧+5162494128", form.ProductionOrg);
        Assert.Equal("张彪", form.CreditName);
        Assert.Equal(1, form.FreeEpisodeCount);
        Assert.Equal("顾承宇", form.Actors[0].Name);
        Assert.Equal("横屏", KuaishouPersonalFormResolver.ResolveProductionForm("auto", 1920, 1080));
        Assert.Equal("横屏", KuaishouPersonalFormResolver.ResolveProductionForm("横屏", 1080, 1920));
    }
}
