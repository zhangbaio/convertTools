using FluentAssertions;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueMaterialStepServiceTests
{
    [Fact]
    public void Download_completeness_uses_successful_download_count_instead_of_stale_declared_count()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"download-real-count-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var item = new QueueProjectItem { ProjectDir = sourceDir, EpisodeCount = 68 };

        try
        {
            File.WriteAllText(
                Path.Combine(sourceDir, "shortdrama-project.json"),
                JsonSerializer.Serialize(new { episodeCount = 68 }));
            File.WriteAllText(
                Path.Combine(sourceDir, ".weixin-channel-download-state.json"),
                JsonSerializer.Serialize(new
                {
                    ok = true,
                    video_count = 37,
                    episodes = "all",
                    failures = Array.Empty<string>(),
                    episode_number_mode = "source",
                    episode_mappings = Enumerable.Range(1, 37).Select(number => new
                    {
                        source_episode_number = number,
                        sequence_episode_number = number,
                    }),
                }));
            foreach (var number in Enumerable.Range(1, 37))
                File.WriteAllBytes(Path.Combine(sourceDir, $"第{number}集.mp4"), [1]);

            var expectedNumbers = QueueMaterialStepService.ResolveExpectedDownloadedEpisodeNumbers(sourceDir, item);
            expectedNumbers.Should().Equal(Enumerable.Range(1, 37));
            QueueMaterialStepService.InspectDownloadedEpisodes(sourceDir, item, expectedNumbers)
                .IsComplete.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public void Download_completeness_uses_source_episode_mapping_when_numbers_are_not_contiguous()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"download-mapping-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var sourceNumbers = Enumerable.Range(1, 7).Concat(Enumerable.Range(9, 33)).ToArray();
        var item = new QueueProjectItem
        {
            ProjectDir = sourceDir,
            EpisodeCount = 40,
        };

        try
        {
            File.WriteAllText(
                Path.Combine(sourceDir, "shortdrama-project.json"),
                JsonSerializer.Serialize(new { episodeCount = 40 }));
            File.WriteAllText(
                Path.Combine(sourceDir, ".weixin-channel-download-state.json"),
                JsonSerializer.Serialize(new
                {
                    episodes = "all",
                    episode_number_mode = "source",
                    episode_mappings = sourceNumbers.Select((number, index) => new
                    {
                        source_episode_number = number,
                        sequence_episode_number = index + 1,
                    }),
                }));
            foreach (var number in sourceNumbers)
                File.WriteAllBytes(Path.Combine(sourceDir, $"第{number}集.mp4"), [1, 2, 3]);

            var expectedNumbers = QueueMaterialStepService.ResolveExpectedDownloadedEpisodeNumbers(sourceDir, item);
            expectedNumbers.Should().Equal(sourceNumbers);

            var complete = QueueMaterialStepService.InspectDownloadedEpisodes(sourceDir, item, expectedNumbers);
            complete.IsComplete.Should().BeTrue();
            complete.Expected.Should().Be(40);
            complete.FoundCount.Should().Be(40);
            complete.Missing.Should().BeEmpty();

            File.Delete(Path.Combine(sourceDir, "第9集.mp4"));
            var missing = QueueMaterialStepService.InspectDownloadedEpisodes(sourceDir, item, expectedNumbers);
            missing.IsComplete.Should().BeFalse();
            missing.FoundCount.Should().Be(39);
            missing.Missing.Should().Equal(9);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public void NeedsAiRewrite_requires_matching_persisted_synopsis_mode_and_content()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"rewrite-state-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "原项目");
        var workflowDir = Path.Combine(workspace, "workflow", "原项目");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowDir);
        var infoPath = Path.Combine(workflowDir, "短剧信息.txt");
        File.WriteAllText(infoPath, """
            原剧名: 旧梦归途
            新剧名: 归途有光
            推荐语: 她跨越风雨，终于找回被夺走的人生。
            简介: 女主在家族变故后重整旗鼓，与伙伴携手揭开多年前的真相。
            """);

        var item = new QueueProjectItem
        {
            ProjectDir = sourceDir,
            OriginalTitle = "旧梦归途",
            NewTitle = "归途有光",
        };
        var disabled = new TikTokAccountProfile
        {
            Id = "account-a",
            TiktokAiRewriteSynopsis = false,
        };
        var enabled = new TikTokAccountProfile
        {
            Id = "account-a",
            TiktokAiRewriteSynopsis = true,
        };

        try
        {
            var context = ProjectWorkspaceService.LoadContext(sourceDir);

            QueueMaterialStepService.NeedsAiRewrite(item, disabled).Should().BeFalse();
            QueueMaterialStepService.PersistRewriteCompletionState(context, disabled, infoPath, rewriteSynopsis: false);

            QueueMaterialStepService.NeedsAiRewrite(item, enabled).Should().BeTrue(
                "enabling synopsis rewrite must not reuse a title-only completion");

            QueueMaterialStepService.PersistRewriteCompletionState(context, enabled, infoPath, rewriteSynopsis: true);
            QueueMaterialStepService.NeedsAiRewrite(item, enabled).Should().BeFalse();

            ProjectWorkspaceService.UpdateProjectInfoField(infoPath, "简介", "后来手工恢复的原始简介内容");
            QueueMaterialStepService.NeedsAiRewrite(item, enabled).Should().BeTrue(
                "the persisted synopsis fingerprint no longer matches the project info");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { /* SQLite can retain a pooled Windows handle briefly. */ }
        }
    }

    [Fact]
    public void Persisted_ai_result_with_colon_in_synopsis_can_recover_missing_completion_state()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"rewrite-recovery-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(workspace, "珍珠心软之第五年重逢，驰先生再度失控");
        var workflowDir = Path.Combine(workspace, "workflow", "珍珠心软之第五年重逢，驰先生再度失控");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(workflowDir);

        const string originalTitle = "珍珠心软之第五年重逢，驰先生再度失控";
        const string newTitle = "四年重逢贺先生情难自禁";
        const string synopsis = "贺行舟与许念因误会分开，直到他得知当年真相：许念是为了保护他才独自离开。";
        var infoPath = Path.Combine(workflowDir, "短剧信息.txt");
        File.WriteAllText(infoPath, $"""
            原剧名: {originalTitle}
            新剧名: {newTitle}
            推荐语: 误会分散四年，重逢爱恨翻涌
            简介: {synopsis}
            """);

        var item = new QueueProjectItem
        {
            ProjectDir = sourceDir,
            OriginalTitle = originalTitle,
            NewTitle = newTitle,
        };
        var account = new TikTokAccountProfile
        {
            Id = "account-a",
            TiktokAiRewriteSynopsis = true,
        };

        try
        {
            var context = ProjectWorkspaceService.LoadContext(sourceDir);
            var variantKey = $"{Path.GetFullPath(sourceDir)}#account-a#synopsis=1";
            var history = new[]
            {
                new AiRewriteHistoryRecord(
                    OriginalTitle: originalTitle,
                    OriginalSynopsis: "原始简介",
                    NewTitle: newTitle,
                    NewSynopsis: synopsis,
                    ProjectName: Path.GetFileName(sourceDir),
                    ProjectDir: sourceDir,
                    WorkspacePath: workspace,
                    AccountProfileId: account.Id,
                    AccountProfileName: "",
                    VariantKey: variantKey,
                    ModelName: "test-model",
                    CreatedAt: "2026-07-15T15:11:58"),
            };

            QueueMaterialStepService.NeedsAiRewrite(item, account).Should().BeTrue(
                "the generated file exists but its completion state was not written");
            QueueMaterialStepService.CurrentInfoMatchesRewriteHistory(
                    item,
                    context,
                    infoPath,
                    history,
                    rewriteSynopsis: true,
                    rewriteVariantKey: variantKey)
                .Should().BeTrue("the exact on-disk AI output is already recorded in history");
            QueueMaterialStepService.CurrentInfoMatchesRewriteHistory(
                    item,
                    context,
                    infoPath,
                    history,
                    rewriteSynopsis: true,
                    rewriteVariantKey: $"{Path.GetFullPath(sourceDir)}#account-b#synopsis=1")
                .Should().BeFalse("a different account variant must receive its own rewrite");
            QueueMaterialStepService.CurrentInfoMatchesRewriteHistory(
                    item,
                    context,
                    infoPath,
                    history.Select(record => record with { NewSynopsis = "另一个 AI 改写简介" }).ToArray(),
                    rewriteSynopsis: true,
                    rewriteVariantKey: variantKey)
                .Should().BeFalse("落盘简介必须与历史指纹完全一致");

            var persist = () => QueueMaterialStepService.PersistRewriteCompletionState(
                context,
                account,
                infoPath,
                rewriteSynopsis: true);
            persist.Should().NotThrow("简介正文中的全角冒号不应被当作字段分隔符");
            QueueMaterialStepService.NeedsAiRewrite(item, account).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { /* SQLite can retain a pooled Windows handle briefly. */ }
        }
    }
}
