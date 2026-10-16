using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ExternalDramaInfoReaderTests
{
    [Fact]
    public void Read_prefers_plain_intro_and_extracts_other_fields_from_detailed_file()
    {
        var root = CreateTempDirectory();
        const string intro = "苏澈回归云州，为母亲洗刷冤屈，并追查父亲遇害背后的真相。";
        try
        {
            File.WriteAllText(Path.Combine(root, "简介.txt"), intro, new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(root, "详细简介.txt"),
                $"剧名：归旌\n作者：测试公司\n类型：战神赘婿\n集数：40\n简介：{intro}\n发布时间：2026-08-21",
                new UTF8Encoding(false));
            var metadata = new JsonObject { ["intro"] = "归旌，待补充简介。" };

            var result = ExternalDramaInfoReader.Read(root, metadata);

            result.Title.Should().Be("归旌");
            result.Intro.Should().Be(intro);
            result.Category.Should().Be("战神赘婿");
            result.DeclaredEpisodeCount.Should().Be(40);
            Path.GetFileName(result.IntroPath).Should().Be("简介.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_extracts_multiline_intro_from_gb18030_detailed_file()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "详细简介.txt"),
                "标题:异乡归途\n题材:都市情感\n总集数:18集\n剧情简介:女孩来到陌生城市。\n她在朋友帮助下找到家人。\n发布日期:2026-08-20",
                Encoding.GetEncoding("gb18030"));

            var result = ExternalDramaInfoReader.Read(root);

            result.Title.Should().Be("异乡归途");
            result.Category.Should().Be("都市情感");
            result.DeclaredEpisodeCount.Should().Be(18);
            result.Intro.Should().Be("女孩来到陌生城市。\n她在朋友帮助下找到家人。");
            result.Intro.Should().NotContain("发布日期");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_uses_single_unknown_text_file_as_plain_intro()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "story-summary.txt"), "A complete external synopsis.");

            var result = ExternalDramaInfoReader.Read(root);

            result.Intro.Should().Be("A complete external synopsis.");
            Path.GetFileName(result.IntroPath).Should().Be("story-summary.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_parses_hongguo_bracket_intro_title_and_poster()
    {
        var root = Path.Combine(CreateTempDirectory(), "红果_灾年逆袭：我靠穿越养全家");
        Directory.CreateDirectory(root);
        const string intro =
            "灾年大旱，吃光全家口粮膘肥体胖的林瑶抛下丈夫和两个孩子跟别人跑了，" +
            "最后被渣男骗，下场凄惨。现代农业大学生林瑶熬夜猝死，一觉醒来，" +
            "自己穿进了这本年代小说里，成了即将抛夫弃子跟人跑路的胖女人。";
        try
        {
            File.WriteAllText(
                Path.Combine(root, "简介.txt"),
                string.Join(
                    "\n",
                    "══════════════════════════════════",
                    "          短 剧 简 介",
                    "══════════════════════════════════",
                    "",
                    "【剧　　名】灾年逆袭：我靠穿越养全家",
                    "【评　　分】9.8 分",
                    "【状　　态】第1季 / 已完结 / 共100集",
                    "【总 集 数】共 100 集",
                    "【分　　类】逆袭",
                    "",
                    "──────────────────────────────────",
                    "【剧情简介】",
                    intro,
                    "──────────────────────────────────",
                    "",
                    "【封面链接】http://example.com/cover.jpg",
                    "【版权信息】",
                    "版权信息：测试版权"),
                new UTF8Encoding(false));

            // 元数据里的文件夹占位剧名不应压过简介【剧名】。
            var metadata = new JsonObject
            {
                ["displayName"] = "红果_灾年逆袭：我靠穿越养全家",
                ["title"] = "红果_灾年逆袭：我靠穿越养全家",
                ["intro"] = "══════════════════════════════════",
            };

            var result = ExternalDramaInfoReader.Read(root, metadata);

            result.Title.Should().Be("灾年逆袭：我靠穿越养全家");
            result.Category.Should().Be("逆袭");
            result.DeclaredEpisodeCount.Should().Be(100);
            result.Intro.Should().Be(intro);
            result.Intro.Should().NotContain("版权信息");
            result.PosterUrl.Should().Be("http://example.com/cover.jpg");
        }
        finally
        {
            DeleteBestEffort(Path.GetDirectoryName(root)!);
        }
    }

    [Fact]
    public void Local_import_repairs_hongguo_folder_seed_titles_from_bracket_intro()
    {
        var workspace = CreateTempDirectory();
        var source = Path.Combine(workspace, "红果_订婚宴上我收回了一切");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllBytes(Path.Combine(source, "第1集.mp4"), [1]);
            File.WriteAllText(
                Path.Combine(source, "简介.txt"),
                string.Join(
                    "\n",
                    "【剧　　名】订婚宴上我收回了一切",
                    "【总 集 数】共 60 集",
                    "【剧情简介】",
                    "订婚宴当天，女主发现未婚夫出轨，当场收回所有嫁妆并离开这段关系。"),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(source, "shortdrama-project.json"),
                """
                {
                  "displayName": "红果_订婚宴上我收回了一切",
                  "title": "红果_订婚宴上我收回了一切",
                  "originalTitle": "红果_订婚宴上我收回了一切",
                  "intro": "红果_订婚宴上我收回了一切，待补充简介。",
                  "workflowDirName": "_红果_订婚宴上我收回了一切",
                  "localManualImport": true
                }
                """,
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(source, "短剧信息.txt"),
                "原剧名：红果_订婚宴上我收回了一切\n新剧名：红果_订婚宴上我收回了一切\n简介：红果_订婚宴上我收回了一切，待补充简介。\n",
                new UTF8Encoding(false));

            var result = LocalManualDramaImportService.Import(workspace, source);

            result.DisplayName.Should().Be("订婚宴上我收回了一切");
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "shortdrama-project.json")));
            metadata.RootElement.GetProperty("displayName").GetString().Should().Be("订婚宴上我收回了一切");
            metadata.RootElement.GetProperty("title").GetString().Should().Be("订婚宴上我收回了一切");
            metadata.RootElement.GetProperty("originalTitle").GetString().Should().Be("订婚宴上我收回了一切");

            var sourceInfo = ProjectInfoTextHelper.ParseInfoFile(Path.Combine(source, "短剧信息.txt"));
            sourceInfo["原剧名"].Should().Be("订婚宴上我收回了一切");
            sourceInfo["新剧名"].Should().Be("订婚宴上我收回了一切");
            sourceInfo["简介"].Should().Contain("订婚宴当天");

            var workflowInfo = ProjectInfoTextHelper.ParseInfoFile(
                Path.Combine(result.WorkflowProjectDir, "短剧信息.txt"));
            workflowInfo["原剧名"].Should().Be("订婚宴上我收回了一切");
            workflowInfo["新剧名"].Should().Be("订婚宴上我收回了一切");
            workflowInfo["简介"].Should().Contain("订婚宴当天");
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    [Fact]
    public void Local_import_does_not_overwrite_manual_new_title()
    {
        var workspace = CreateTempDirectory();
        var source = Path.Combine(workspace, "红果_订婚宴上我收回了一切");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllBytes(Path.Combine(source, "第1集.mp4"), [1]);
            File.WriteAllText(
                Path.Combine(source, "简介.txt"),
                "【剧　　名】订婚宴上我收回了一切\n【剧情简介】\n真实简介。\n",
                new UTF8Encoding(false));
            var workflowDir = Path.Combine(workspace, "workflow", "_红果_订婚宴上我收回了一切");
            Directory.CreateDirectory(workflowDir);
            File.WriteAllText(
                Path.Combine(source, "shortdrama-project.json"),
                $$"""
                {
                  "displayName": "红果_订婚宴上我收回了一切",
                  "title": "红果_订婚宴上我收回了一切",
                  "originalTitle": "红果_订婚宴上我收回了一切",
                  "workflowDirName": "_红果_订婚宴上我收回了一切",
                  "workflowProjectDir": {{JsonSerializer.Serialize(workflowDir)}},
                  "sourceProjectDir": {{JsonSerializer.Serialize(source)}},
                  "localManualImport": true
                }
                """,
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(workflowDir, "短剧信息.txt"),
                "原剧名: 订婚宴上我收回了一切\n新剧名: 人工改过的新剧名\n简介: 真实简介。\n",
                new UTF8Encoding(false));

            var result = LocalManualDramaImportService.Import(workspace, source);

            result.DisplayName.Should().Be("订婚宴上我收回了一切");
            var workflowInfo = ProjectInfoTextHelper.ParseInfoFile(
                Path.Combine(result.WorkflowProjectDir, "短剧信息.txt"));
            workflowInfo["原剧名"].Should().Be("订婚宴上我收回了一切");
            workflowInfo["新剧名"].Should().Be("人工改过的新剧名");
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    [Fact]
    public void Local_import_separates_declared_episode_count_from_available_videos()
    {
        var workspace = CreateTempDirectory();
        var sourceRoot = CreateTempDirectory();
        var source = Path.Combine(sourceRoot, "归旌");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllBytes(Path.Combine(source, "第1集.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(source, "第2集.mp4"), [2]);
            File.WriteAllBytes(Path.Combine(source, "第3集.mp4.aria2"), [3]);
            File.WriteAllText(
                Path.Combine(source, "详细简介.txt"),
                "剧名：归旌\n类型：战神赘婿\n集数：40\n简介：真实简介内容。",
                new UTF8Encoding(false));

            var result = LocalManualDramaImportService.Import(workspace, source);

            result.EpisodeCount.Should().Be(2);
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "shortdrama-project.json")));
            metadata.RootElement.GetProperty("episodeCount").GetInt32().Should().Be(40);
            metadata.RootElement.GetProperty("declaredEpisodeCount").GetInt32().Should().Be(40);
            metadata.RootElement.GetProperty("effectiveEpisodeCount").GetInt32().Should().Be(2);
            metadata.RootElement.GetProperty("intro").GetString().Should().Be("真实简介内容。");
            metadata.RootElement.GetProperty("category").GetString().Should().Be("战神赘婿");
        }
        finally
        {
            DeleteBestEffort(workspace);
            DeleteBestEffort(sourceRoot);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"external-drama-info-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteBestEffort(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
    }
}
