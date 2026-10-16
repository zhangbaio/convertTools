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

    [Fact]
    public void Read_parses_hongguo_bracket_intro_and_stops_intro_at_separator()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "简介.txt"),
                string.Join('\n',
                    "══════════════════════════════════",
                    "          短 剧 简 介",
                    "══════════════════════════════════",
                    "【剧\u3000\u3000名】珩珩漫漫寻恩路",
                    "【评\u3000\u3000分】9.8 分",
                    "【总 集 数】共 56 集",
                    "【分\u3000\u3000类】都市日常",
                    "【短 剧 ID】7693110299905231896",
                    "──────────────────────────────────",
                    "【剧情简介】",
                    "她循着旧约回到故乡。",
                    "终于找到当年的少年。",
                    "──────────────────────────────────",
                    "【封面链接】http://p6-novelfm-sign.byteimg.com/cover.heic?x-expires=1",
                    "【版权信息】",
                    "版权信息：本书的数字版权由测试公司提供。"),
                new UTF8Encoding(false));

            var result = ExternalDramaInfoReader.Read(root);

            result.Title.Should().Be("珩珩漫漫寻恩路");
            result.Category.Should().Be("都市日常");
            result.DeclaredEpisodeCount.Should().Be(56);
            result.BookId.Should().Be("7693110299905231896");
            result.PosterUrl.Should().StartWith("http://p6-novelfm-sign.byteimg.com/cover.heic");
            result.Intro.Should().Be("她循着旧约回到故乡。\n终于找到当年的少年。");
            Path.GetFileName(result.IntroPath).Should().Be("简介.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_uses_downloader_metadata_json_and_prefers_non_heic_cover()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "metadata.json"),
                """
                {"categories": ["都市", "日常"], "cover_url": "https://p3-novel.byteimg.com/cover.jpeg",
                 "drama_id": "7693110299905231896", "episode_count": 56,
                 "intro": "她循着旧约回到故乡。", "title": "珩珩漫漫寻恩路"}
                """,
                new UTF8Encoding(false));

            var result = ExternalDramaInfoReader.Read(root);

            result.Title.Should().Be("珩珩漫漫寻恩路");
            result.Intro.Should().Be("她循着旧约回到故乡。");
            result.IntroSource.Should().Be("metadata.json");
            result.IntroPath.Should().BeNull();
            result.Category.Should().Be("都市·日常");
            result.DeclaredEpisodeCount.Should().Be(56);
            result.BookId.Should().Be("7693110299905231896");
            result.PosterUrl.Should().Be("https://p3-novel.byteimg.com/cover.jpeg");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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
