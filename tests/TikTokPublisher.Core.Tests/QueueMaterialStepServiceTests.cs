using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueMaterialStepServiceTests
{
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
}
