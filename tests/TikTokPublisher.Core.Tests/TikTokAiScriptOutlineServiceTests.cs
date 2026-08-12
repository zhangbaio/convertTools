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
    public void OutlineCharacterProps_AcceptsObjectAndArrayValues()
    {
        const string json = """
            {
              "characters": [
                { "name": "甲", "props": { "item": "玉佩", "meaning": "身份" } },
                { "name": "乙", "props": ["药箱", "银针"] }
              ]
            }
            """;

        var outline = JsonSerializer.Deserialize<AiScriptOutline>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;

        Assert.Equal("item：玉佩；meaning：身份", outline.Characters[0].Props);
        Assert.Equal("药箱、银针", outline.Characters[1].Props);
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
        Assert.Contains("episodes 必须从 1 连续到 47", prompt);
    }

    [Fact]
    public void QueueStepRegistry_PlacesAiOutlineAfterRewriteAndBeforePoster()
    {
        var keys = QueueStepRegistry.All.Select(step => step.Key).ToArray();

        var rewrite = Array.IndexOf(keys, QueueStepKeys.RewriteInfo);
        var outline = Array.IndexOf(keys, QueueStepKeys.GenerateAiScriptOutline);
        var poster = Array.IndexOf(keys, QueueStepKeys.GeneratePoster);

        Assert.True(rewrite < outline);
        Assert.True(outline < poster);
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
    public void CreateDocument_UsesEmbeddedTemplateAndWritesOutlineTables()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "tiktok-outline-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var output = Path.Combine(tempDir, "大纲.docx");
        try
        {
            var outline = new AiScriptOutline
            {
                Genre = "都市短剧",
                Style = "现实主义",
                Tone = "强冲突",
                Synopsis = "这是完整剧情梗概。",
                Characters = [new AiOutlineCharacter { Name = "主角", Positioning = "核心人物" }],
                Scenes = [new AiOutlineScene { Number = "S01", Name = "办公室" }],
                Episodes =
                [
                    new AiOutlineEpisode { Number = 1, Title = "开端", Event = "事件一" },
                    new AiOutlineEpisode { Number = 2, Title = "反转", Event = "事件二" },
                ],
            };

            TikTokAiScriptOutlineService.CreateDocument(output, "新剧名", 2, outline);

            using var document = WordprocessingDocument.Open(output, false);
            var body = document.MainDocumentPart!.Document.Body!;
            Assert.Contains("项目名称：新剧名", body.InnerText);
            Assert.Contains("产物四：分集大纲", body.InnerText);
            Assert.DoesNotContain("总集数", body.InnerText);
            Assert.Equal(2, body.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>().Count());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
