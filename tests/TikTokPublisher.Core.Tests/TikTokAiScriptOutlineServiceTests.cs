using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using DocumentFormat.OpenXml.Packaging;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using System.Text.Json;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokAiScriptOutlineServiceTests
{
    [Theory]
    [InlineData(1, "画面比例：9:16（竖屏短剧）")]
    [InlineData(0, "画面比例：16:9（横屏短剧）")]
    [InlineData(-1, "")]
    public void FormatAspectRatio_UsesResolvedVideoOrientation(int videoVertical, string expected)
    {
        Assert.Equal(expected, TikTokAiScriptOutlineService.FormatAspectRatio(videoVertical));
    }

    [Fact]
    public void ContainsAiContentDisclosure_RejectsDisclosureVariantsButAllowsDocumentTitle()
    {
        Assert.True(TikTokAiScriptOutlineService.ContainsAiContentDisclosure("（注：部分内容可能由 AI 生成）"));
        Assert.True(TikTokAiScriptOutlineService.ContainsAiContentDisclosure("本内容由人工智能辅助生成"));
        Assert.False(TikTokAiScriptOutlineService.ContainsAiContentDisclosure("《新剧名》AI剧本大纲"));
    }

    [Fact]
    public void ParseOutline_RejectsMissingEpisodeCoverage()
    {
        var outline = CreateValidOutline();
        outline.StoryArcs[0].EndEpisode = 2;

        var error = Assert.Throws<InvalidOperationException>(() =>
            TikTokAiScriptOutlineService.ParseOutline(JsonSerializer.Serialize(outline), 2));

        Assert.Contains("完整覆盖", error.Message);
    }

    [Fact]
    public void ParseOutline_RejectsAiContentDisclosure()
    {
        var outline = CreateValidOutline();
        outline.Theme = "（注：部分内容可能由 AI 生成）";

        var error = Assert.Throws<InvalidOperationException>(() =>
            TikTokAiScriptOutlineService.ParseOutline(JsonSerializer.Serialize(outline), 1));

        Assert.Contains("AI 内容标注", error.Message);
    }

    [Fact]
    public void AccountProfile_DefaultOutlineEpisodeCountIsFifteen()
    {
        var profile = new TikTokAccountProfile();

        Assert.Equal(15, profile.TiktokAiScriptOutlineEpisodeCount);
    }

    [Fact]
    public void PublishOptions_BindsOptionalOutlinePdfToCurrentWorkflow()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-outline-upload-{Guid.NewGuid():N}");
        var account = new TikTokAccountProfile
        {
            TiktokCopyrightMaterialTypes =
            [
                TikTokPublishConstants.ProductionAgreementMaterialType,
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
            ],
            TiktokUploadAiScriptOutlineWithScreenshots = true,
        };

        var options = TikTokPublishOptionsBuilder.FromAccount(account, workflow);

        Assert.True(options.UploadAiScriptOutlineWithScreenshots);
        Assert.Equal(
            Path.Combine(workflow, TikTokAiScriptOutlineService.OutputFileName),
            options.AiScriptOutlineFilePath);
    }

    [Fact]
    public void BuildPrompt_UsesNewTitleOriginalSynopsisAndExactEpisodeCount()
    {
        var prompt = TikTokAiScriptOutlineService.BuildPrompt("新剧名", "这是改写前的旧简介", 47);

        Assert.Contains("新剧名：新剧名", prompt);
        Assert.Contains("这是改写前的旧简介", prompt);
        Assert.Contains("仅生成前 47 集的分集内容", prompt);
        Assert.Contains("不得输出或声明项目总集数", prompt);
        Assert.Contains("六部分结构", prompt);
        Assert.Contains("无遗漏、无重复地覆盖第 1 集至第 47 集", prompt);
        Assert.Contains("禁止输出任何 AI 内容声明", prompt);
        Assert.Contains("storyArcs", prompt);
    }

    [Fact]
    public void QueueStepRegistry_FollowsVisibleGenerationOrder()
    {
        var keys = QueueStepRegistry.All.Select(step => step.Key).ToArray();

        var rewrite = Array.IndexOf(keys, QueueStepKeys.RewriteInfo);
        var poster = Array.IndexOf(keys, QueueStepKeys.GeneratePoster);
        var script = Array.IndexOf(keys, QueueStepKeys.GenerateEpisodeScript);
        var aiMaterials = Array.IndexOf(keys, QueueStepKeys.GenerateAiDramaMaterials);
        var outline = Array.IndexOf(keys, QueueStepKeys.GenerateAiScriptOutline);
        var proof = Array.IndexOf(keys, QueueStepKeys.GenerateProofMaterial);

        Assert.True(rewrite < poster);
        Assert.True(poster < script);
        Assert.True(script < aiMaterials);
        Assert.True(aiMaterials < outline);
        Assert.True(outline < proof);
        Assert.Contains(QueueStepRegistry.UserSelectable, step => step.Key == QueueStepKeys.GenerateAiScriptOutline);
    }

    [Fact]
    public void NormalizeStepStates_AddsAiOutlineState()
    {
        var item = new QueueProjectItem();

        item.NormalizeStepStates();

        Assert.Equal(QueueStepStatus.Pending, item.StepStates[QueueStepKeys.GenerateAiScriptOutline]);
    }

    [Fact]
    public void CreateDocument_UsesEmbeddedTemplateAndWritesSixSectionReadingLayout()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "tiktok-outline-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var output = Path.Combine(tempDir, "大纲.docx");
        try
        {
            var outline = new AiScriptOutline
            {
                Genre = "都市短剧",
                CoreSellingPoint = "身份反差与连续反转。",
                Logline = "落魄律师重回法庭揭开旧案真相。",
                WorldOverview = "故事发生在竞争激烈的律所与法庭体系中。",
                WorldRules =
                [
                    new AiOutlineWorldRule { Title = "证据规则", Description = "关键事实必须形成证据闭环。" },
                    new AiOutlineWorldRule { Title = "利益规则", Description = "人物选择受到职业利益牵引。" },
                ],
                Characters =
                [
                    new AiOutlineCharacter
                    {
                        Name = "沈砚",
                        Role = "男主",
                        Identity = "落魄律师",
                        Personality = "冷静克制",
                        Ability = "证据推理",
                        Motivation = "查清旧案",
                        Arc = "从自我怀疑走向坚守正义",
                    },
                ],
                StoryArcs =
                [
                    new AiOutlineStoryArc
                    {
                        Title = "第一阶段：旧案重启",
                        StartEpisode = 1,
                        EndEpisode = 2,
                        Mainline = "男主取得关键证据。",
                        EpisodeGroups =
                        [
                            new AiOutlineEpisodeGroup { StartEpisode = 1, EndEpisode = 1, Plot = "男主收到匿名卷宗。" },
                            new AiOutlineEpisodeGroup { StartEpisode = 2, EndEpisode = 2, Plot = "证人突然翻供。" },
                        ],
                        EndingHook = "幕后人现身。",
                    },
                ],
                Highlights =
                [
                    new AiOutlineHighlight { Title = "法庭反转", Description = "男主用时间线击穿伪证。" },
                    new AiOutlineHighlight { Title = "身份揭晓", Description = "匿名委托人身份反转。" },
                ],
                Theme = "在利益与真相之间，选择决定一个人最终成为谁。",
            };

            TikTokAiScriptOutlineService.CreateDocument(output, "新剧名", 2, outline);

            using var document = WordprocessingDocument.Open(output, false);
            var body = document.MainDocumentPart!.Document.Body!;
            Assert.Contains("《新剧名》AI剧本大纲", body.InnerText);
            Assert.Contains("一、剧本核心定位", body.InnerText);
            Assert.Contains("二、世界观核心设定（剧本核心规则）", body.InnerText);
            Assert.Contains("三、核心人物设定", body.InnerText);
            Assert.Contains("四、分集剧情大纲", body.InnerText);
            Assert.Contains("五、核心爽点与名场面设计", body.InnerText);
            Assert.Contains("六、剧本主题内核", body.InnerText);
            Assert.DoesNotContain("总集数", body.InnerText);
            Assert.False(TikTokAiScriptOutlineService.ContainsAiContentDisclosure(body.InnerText));
            Assert.Empty(body.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private static AiScriptOutline CreateValidOutline() => new()
    {
        Genre = "都市短剧",
        CoreSellingPoint = "身份反差",
        Logline = "主角查清旧案。",
        WorldOverview = "故事发生在城市律所。",
        WorldRules = [new AiOutlineWorldRule { Title = "证据", Description = "证据必须闭环。" }],
        Characters = [new AiOutlineCharacter { Name = "沈砚", Role = "男主", Identity = "律师" }],
        StoryArcs =
        [
            new AiOutlineStoryArc
            {
                Title = "旧案重启",
                StartEpisode = 1,
                EndEpisode = 1,
                Mainline = "男主收到线索。",
                EpisodeGroups = [new AiOutlineEpisodeGroup { StartEpisode = 1, EndEpisode = 1, Plot = "调查开启。" }],
                EndingHook = "证人出现。",
            },
        ],
        Highlights = [new AiOutlineHighlight { Title = "法庭反转", Description = "击穿伪证。" }],
        Theme = "坚持真相。",
    };
}
