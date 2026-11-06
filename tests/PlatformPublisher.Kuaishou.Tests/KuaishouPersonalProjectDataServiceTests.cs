using PlatformPublisher.Kuaishou.Publishing;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouPersonalProjectDataServiceTests
{
    [Fact]
    public async Task Source_directory_with_info_file_still_uses_workflow_materials()
    {
        var root = Directory.CreateTempSubdirectory();
        var source = Directory.CreateDirectory(Path.Combine(root.FullName, "红果_赶山"));
        var workflow = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "_赶山"));
        File.WriteAllText(Path.Combine(source.FullName, "短剧信息.txt"), "原剧名：赶山\n新剧名：赶山\n简介：测试简介\n");
        File.WriteAllText(Path.Combine(source.FullName, "1.mp4"), "video");
        File.WriteAllText(Path.Combine(workflow.FullName, "短剧信息.txt"), "原剧名：赶山\n新剧名：赶山\n简介：测试简介\n");
        File.WriteAllText(Path.Combine(workflow.FullName, "工程图_1.png"), "image");
        File.WriteAllText(
            Path.Combine(workflow.FullName, "kuaishou-auto-fill.json"),
            """{"tag_label_list":["逆袭","乡村","年代"]}""");
        var metadata = $$"""
        {
          "sourceProjectDir": {{Json(source.FullName)}},
          "workflowProjectDir": {{Json(workflow.FullName)}},
          "workflowDirName": "_赶山"
        }
        """;
        File.WriteAllText(Path.Combine(source.FullName, "shortdrama-project.json"), metadata);

        var data = await new KuaishouPersonalProjectDataService(new UnusedScanner()).ResolveAsync(
            source.FullName,
            new KuaishouPersonalConfig(),
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(workflow.FullName), data.WorkflowDirectory);
        Assert.Equal(Path.GetFullPath(source.FullName), data.SourceDirectory);
        Assert.Single(data.ProjectImagePaths);
        Assert.Equal(Path.Combine(workflow.FullName, "videos", "赶山-第1集.mp4"), Assert.Single(data.VideoPaths));
        Assert.False(File.Exists(Path.Combine(source.FullName, "1.mp4")));
        Assert.True(File.Exists(Path.Combine(data.WorkflowDirectory, "kuaishou-auto-fill.json")));
    }

    [Fact]
    public void Episode_videos_keep_real_episode_numbers_inside_the_new_title_folder()
    {
        var root = Directory.CreateTempSubdirectory();
        var source = Directory.CreateDirectory(Path.Combine(root.FullName, "红果_原剧"));
        var workflow = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "_临危受命守大乾"));
        File.WriteAllText(Path.Combine(source.FullName, "剧-第2集.mp4"), "second");
        File.WriteAllText(Path.Combine(source.FullName, "剧-第1集.mp4"), "first");

        var paths = KuaishouSettlementEpisodeVideoService.Prepare(source.FullName, workflow.FullName, "临危受命守大乾");

        Assert.Equal(
            Path.Combine(workflow.FullName, "videos", "临危受命守大乾-第1集.mp4"),
            paths[0]);
        Assert.Equal(
            Path.Combine(workflow.FullName, "videos", "临危受命守大乾-第2集.mp4"),
            paths[1]);
        Assert.False(File.Exists(Path.Combine(source.FullName, "剧-第1集.mp4")));
    }

    [Fact]
    public void Missing_episode_number_stops_before_moving_videos()
    {
        var root = Directory.CreateTempSubdirectory();
        var source = Directory.CreateDirectory(Path.Combine(root.FullName, "source"));
        var workflow = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow", "_新剧"));
        File.WriteAllText(Path.Combine(source.FullName, "剧-第1集.mp4"), "one");
        File.WriteAllText(Path.Combine(source.FullName, "剧-第3集.mp4"), "three");

        var error = Assert.Throws<InvalidOperationException>(() =>
            KuaishouSettlementEpisodeVideoService.Prepare(source.FullName, workflow.FullName, "新剧"));

        Assert.Contains("第2集", error.Message);
        Assert.True(File.Exists(Path.Combine(source.FullName, "剧-第1集.mp4")));
        Assert.False(Directory.Exists(Path.Combine(workflow.FullName, "videos")));
    }

    [Fact]
    public void Upload_actors_prefer_prepared_names_and_character_roles()
    {
        var root = Directory.CreateTempSubdirectory();
        var workflow = Directory.CreateDirectory(Path.Combine(root.FullName, "workflow"));
        File.WriteAllText(
            Path.Combine(workflow.FullName, "kuaishou-auto-fill.json"),
            """
            {
              "actor_info_list": [
                {"name": "顾承宇", "gender": "男", "role": "赵大川"},
                {"name": "江念初", "gender": "女", "role": "沈染"}
              ]
            }
            """);

        var actors = KuaishouPersonalFormResolver.ResolveUploadActors(workflow.FullName, new KuaishouPersonalConfig());

        Assert.Equal("顾承宇", actors[0].Name);
        Assert.Equal("江念初", actors[1].Name);
        Assert.Equal("赵大川", actors[0].Role);
        Assert.Equal("沈染", actors[1].Role);
    }

    [Fact]
    public void Upload_actors_match_library_to_story_characters_when_project_has_no_names()
    {
        var actors = KuaishouPersonalFormResolver.ResolveUploadActors(null, new KuaishouPersonalConfig
        {
            ActorInfoJson = """[{"name":"","gender":"男","role":""}]""",
            ActorLibraryText = "顾承宇|男|陆沉\n江念初|女|林晚乔",
        });

        Assert.Equal("顾承宇", actors[0].Name);
        Assert.Equal("男", actors[0].Gender);
        Assert.Equal("江念初", actors[1].Name);
        Assert.Equal("女", actors[1].Gender);
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private sealed class UnusedScanner : IProjectScanner
    {
        public Task<ProjectScanResult> ScanAsync(string rootDir, string? backupRootDir, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("已有短剧信息的源目录不应再扫描。");
    }
}
