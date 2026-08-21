using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokQueueDocumentStepTests
{
    [Theory]
    [InlineData(5, 40, 40, 5)]
    [InlineData(10, 40, 40, 10)]
    [InlineData(10, 3, 40, 3)]
    [InlineData(8, 0, 6, 6)]
    [InlineData(8, 0, 0, 8)]
    public void Episode_script_count_respects_account_configuration_and_available_episodes(
        int configured,
        int availableVideos,
        int declaredEpisodes,
        int expected)
    {
        var account = new TikTokPublisher.Core.Models.TikTokAccountProfile
        {
            TiktokEpisodeScriptEpisodeCount = configured,
        };

        TikTokEpisodeScriptService.ResolveTargetEpisodeCount(
                account,
                availableVideos,
                declaredEpisodes)
            .Should().Be(expected);
    }

    [Fact]
    public void Synopsis_script_prompt_does_not_claim_video_or_total_episode_count()
    {
        var prompt = TikTokEpisodeScriptService.BuildSynopsisEpisodePrompt(
            "新剧名", 2, 5, "旧简介中的人物关系与主线冲突。");

        prompt.Should().Contain("新剧名");
        prompt.Should().Contain("旧简介中的人物关系与主线冲突");
        prompt.Should().Contain("第 2 集");
        prompt.Should().Contain("不能声称内容来自视频或字幕");
        prompt.Should().NotContain("总集数：");
    }

    [Fact]
    public void Document_steps_are_registered_in_expected_order()
    {
        var keys = QueueStepRegistry.All.Select(step => step.Key).ToArray();

        Array.IndexOf(keys, QueueStepKeys.GenerateEpisodeScript)
            .Should().BeLessThan(Array.IndexOf(keys, QueueStepKeys.GenerateProofMaterial));
        Array.IndexOf(keys, QueueStepKeys.GenerateTimestampCertificate)
            .Should().BeGreaterThan(Array.IndexOf(keys, QueueStepKeys.GenerateProofMaterial));
        QueueStepRegistry.UserSelectable.Select(step => step.Key)
            .Should().Contain([QueueStepKeys.GenerateEpisodeScript, QueueStepKeys.GenerateTimestampCertificate]);
    }

    [Fact]
    public void Normalize_step_states_adds_document_steps_to_existing_projects()
    {
        var item = new QueueProjectItem();

        item.NormalizeStepStates();

        item.StepStates[QueueStepKeys.GenerateEpisodeScript].Should().Be(QueueStepStatus.Pending);
        item.StepStates[QueueStepKeys.GenerateTimestampCertificate].Should().Be(QueueStepStatus.Pending);
    }

    [Fact]
    public void Episode_prompt_requires_reference_script_contract()
    {
        var prompt = TikTokEpisodeScriptService.BuildEpisodePrompt(
            "测试剧",
            2,
            "第2集.mp4",
            "[00:00:01.000-00:00:02.000] 我回来了");

        prompt.Should().Contain("2-1 场景名称 深夜/室外")
            .And.Contain("人物：角色甲，角色乙")
            .And.Contain("△画面、动作、表情或镜头描述。")
            .And.Contain("对白必须写成“角色名：台词”")
            .And.Contain("不要输出剧名、视频名、“第 2 集”、本集梗概")
            .And.Contain("不要使用 Markdown")
            .And.Contain("[00:00:01.000-00:00:02.000] 我回来了");
        TikTokEpisodeScriptService.OutputSuffix.Should().Be("前5集剧本.pdf");
    }

    [Fact]
    public void Fallback_character_table_merges_episode_appearances()
    {
        var table = TikTokQueueDocumentWriter.BuildFallbackCharacterTable(
        [
            new EpisodeScriptSection(1, "第1集.mp4", "1-1 屋内 白天/室内\n人物：白灵，许文远"),
            new EpisodeScriptSection(2, "第2集.mp4", "2-1 山林 深夜/室外\n人物：白灵，老道士"),
        ]);

        table.Should().Contain("白灵：出现在第 1、2 集")
            .And.Contain("许文远：出现在第 1 集")
            .And.Contain("老道士：出现在第 2 集");
    }

    [Fact]
    public void Character_table_prompt_retains_the_head_and_tail_of_every_episode()
    {
        var longBody = new string('甲', 20_000);
        var episodes = Enumerable.Range(1, 5)
            .Select(episode => new EpisodeScriptSection(
                episode,
                $"第{episode}集.mp4",
                episode == 5 ? longBody + "\n人物：末集新角色" : longBody))
            .ToArray();

        var prompt = TikTokEpisodeScriptService.BuildCharacterTablePrompt("测试剧", episodes);

        prompt.Should().Contain("第 1 集：")
            .And.Contain("第 5 集：")
            .And.Contain("人物：末集新角色")
            .And.Contain("中间内容已省略");
    }

    [Fact]
    public void Document_writer_emits_reference_hierarchy_normalized_text_and_styles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tiktok-document-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "artifact.docx");
            TikTokQueueDocumentWriter.WriteScriptDocument(
                path,
                "测试剧本",
                [new EpisodeScriptSection(1, "第1集.mp4", """
                    ```text
                    # 第1集
                    **1-1 农家土屋 深夜/室内**
                    人物: 白灵, 许文远
                    动作：白灵推开木门。
                    白灵: 我回来了。
                    1-9 农家小院 深夜/室外
                    人物：白灵
                    △白灵走出屋门。
                    角色表
                    这一行不应进入分集正文。
                    ```
                    """)],
                """
                **角色表**
                **白灵:** 女，许文远的妻子。
                """);

            using var document = WordprocessingDocument.Open(path, false);
            var body = document.MainDocumentPart!.Document.Body!;
            var paragraphs = body.Elements<Paragraph>().ToArray();
            paragraphs.Select(paragraph => paragraph.InnerText).Should().Equal(
                "测试剧本 前 1 集剧本",
                "第 1 集 · 第1集.mp4",
                "第 1 集",
                "1-1 农家土屋 深夜/室内",
                "人物：白灵，许文远",
                "△白灵推开木门。",
                "白灵：我回来了。",
                "1-2 农家小院 深夜/室外",
                "人物：白灵",
                "△白灵走出屋门。",
                "全集角色表",
                "角色表",
                "白灵：女，许文远的妻子。");

            var title = paragraphs[0];
            title.ParagraphProperties!.ParagraphBorders!.BottomBorder!.Color!.Value.Should().Be("4F81BD");
            title.Descendants<FontSize>().Select(size => size.Val!.Value).Distinct().Should().Equal("52");
            title.Descendants<Color>().Select(color => color.Val!.Value).Distinct().Should().Equal("17365D");
            title.ParagraphProperties.Justification!.Val!.Value.Should().Be(JustificationValues.Left);

            var episodeFileHeading = paragraphs[1];
            episodeFileHeading.Descendants<FontSize>().Select(size => size.Val!.Value).Distinct().Should().Equal("26");
            episodeFileHeading.Descendants<Color>().Select(color => color.Val!.Value).Distinct().Should().Equal("4F81BD");
            episodeFileHeading.Descendants<Bold>().Should().NotBeEmpty();

            var sceneHeading = paragraphs[3];
            sceneHeading.Descendants<Bold>().Should().ContainSingle();
            sceneHeading.Descendants<RunFonts>().Single().EastAsia!.Value.Should().Be("SimSun");
            paragraphs[5].Descendants<FontSize>().Single().Val!.Value.Should().Be("22");

            var section = body.GetFirstChild<SectionProperties>()!;
            section.GetFirstChild<PageSize>()!.Width!.Value.Should().Be(12240U);
            section.GetFirstChild<PageSize>()!.Height!.Value.Should().Be(15840U);
            section.GetFirstChild<PageMargin>()!.Left!.Value.Should().Be(1800);
            section.GetFirstChild<PageMargin>()!.Right!.Value.Should().Be(1800U);
            section.GetFirstChild<PageMargin>()!.Top!.Value.Should().Be(1440);
            section.GetFirstChild<PageMargin>()!.Bottom!.Value.Should().Be(1440);

            new OpenXmlValidator(FileFormatVersions.Office2013).Validate(document).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
