using Microsoft.Playwright;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Ui.Services;

/// <summary>
/// 内置 WebView2 剧集上传自动化：在可见的内置浏览器中导航，经 CDP 驱动 <see cref="TikTokBrowserActions"/> 填表上传。
/// </summary>
public sealed class EmbeddedBrowserPublishAutomation : IPublishAutomation, IAsyncDisposable
{
    public async Task<PublishResult> PublishAsync(
        TikTokAccountProfile account,
        PublishItem item,
        IEmbeddedBrowser browser,
        FinalAction finalAction,
        Action<string>? log,
        CancellationToken ct)
    {
        void L(string m) => log?.Invoke(m);

        if (!File.Exists(item.VideoPath))
            return PublishResult.Fail($"视频不存在：{item.VideoPath}");

        try
        {
            TikTokUploadPrerequisiteService.EnsureUploadPrerequisites(account, L);
        }
        catch (InvalidOperationException ex)
        {
            return PublishResult.Fail(ex.Message);
        }

        var targetUrl = string.IsNullOrWhiteSpace(account.TiktokSeriesUrl)
            ? TikTokUrls.DefaultSeriesDraftUrl
            : account.TiktokSeriesUrl.Trim();

        var workflowDir = TikTokUploadStateStore.ResolveWorkflowProjectDir(item.ProjectDir);
        var options = TikTokPublishOptionsBuilder.FromAccount(account, workflowDir, L);
        var projectPayload = TikTokProjectPayloadFactory.BuildFromPublishItem(item);
        var payload = TikTokPublishPayload.FromPublishItem(item);
        var settings = ClientSettingsStore.Load();
        var recommendation = await TikTokPublishRecommendationService.BuildRecommendationAsync(
            projectPayload,
            settings,
            options,
            L,
            ct).ConfigureAwait(false);
        L($"TikTok 发布参数：标题={payload.Title}，总集数={payload.EpisodeCount}，" +
          $"目标观众={TikTokPublishRecommendationService.TargetAudienceDisplayText(recommendation.TargetAudience)}，" +
          $"题材={string.Join("、", recommendation.Genres)}");
        var coverPath = ResolveCoverPath(item, L);
        var hasWorkflow = !string.IsNullOrWhiteSpace(workflowDir);

        IPlaywright? pw = null;
        IBrowser? chromium = null;
        try
        {
            (pw, chromium, var page) = await EmbeddedBrowserAutomationBridge
                .ConnectPageAsync(browser, targetUrl, L, ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (IsLoginPage(page.Url))
            {
                var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
                if (await EmbeddedStorageStateImporter.TryImportAsync(page.Context, page, authPath, L, ct)
                    .ConfigureAwait(false))
                {
                    await page.GotoAsync(targetUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 90000,
                    }).ConfigureAwait(false);
                    try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }).ConfigureAwait(false); }
                    catch { /* SPA */ }
                }
            }

            if (IsLoginPage(page.Url))
                return PublishResult.Fail("账号未登录（请先在内置浏览器完成 TikTok 登录）");

            var dailyLimit = await TikTokBrowserActions.DetectDailyEpisodeLimitAsync(page).ConfigureAwait(false);
            if (dailyLimit is not null)
                return PublishResult.FailAndStopQueue($"检测到 TikTok 单日创建剧集上限：{dailyLimit}");

            var enteredEditFlow = false;
            if (item.ForceEditUpload && hasWorkflow)
            {
                L("已选择编辑剧集模式，直接查找平台已有草稿…");
                enteredEditFlow = await TikTokEditFlowService.TryEnterExistingDraftFlowAsync(
                    page,
                    workflowDir,
                    payload,
                    options,
                    recommendation,
                    coverPath,
                    L,
                    ct,
                    allowPlatformSearch: true)
                    .ConfigureAwait(false);
                if (!enteredEditFlow)
                    return PublishResult.Fail("未找到可编辑草稿，无法进入编辑剧集模式");
            }
            else if (hasWorkflow)
            {
                enteredEditFlow = await TikTokEditFlowService.TryEnterExistingDraftFlowAsync(
                    page,
                    workflowDir,
                    payload,
                    options,
                    recommendation,
                    coverPath,
                    L,
                    ct,
                    allowPlatformSearch: TikTokUploadStateStore.HasUploadStepAttempted(workflowDir))
                    .ConfigureAwait(false);
            }

            if (!enteredEditFlow)
            {
                await TikTokBrowserActions.FillCreateInitialFieldsAsync(page, payload, options, L, ct)
                    .ConfigureAwait(false);
                ThrowIfLoginRedirect(page);

                if (hasWorkflow)
                {
                    enteredEditFlow = await TikTokEditFlowService.MaybeRouteDuplicateToEditFlowAsync(
                            page, workflowDir, payload, options, recommendation, coverPath, L, ct)
                        .ConfigureAwait(false);
                    ThrowIfLoginRedirect(page);
                }

                if (!enteredEditFlow)
                {
                    await TikTokBrowserActions.UploadCoverAsync(page, coverPath, L, ct).ConfigureAwait(false);
                    ThrowIfLoginRedirect(page);

                    if (hasWorkflow)
                    {
                        enteredEditFlow = await TikTokEditFlowService.MaybeRouteDuplicateToEditFlowAsync(
                                page, workflowDir, payload, options, recommendation, coverPath, L, ct)
                            .ConfigureAwait(false);
                        ThrowIfLoginRedirect(page);
                    }
                }

                if (!enteredEditFlow)
                {
                    await TikTokBrowserActions.FillCreateRemainingFieldsAsync(
                            page, payload, options, recommendation, coverPath, coverAlreadyUploaded: true, L, ct)
                        .ConfigureAwait(false);
                }
            }

            dailyLimit = await TikTokBrowserActions.DetectDailyEpisodeLimitAsync(page).ConfigureAwait(false);
            if (dailyLimit is not null)
            {
                var limitMsg = $"检测到 TikTok 单日创建剧集上限：{dailyLimit}";
                if (hasWorkflow)
                    TikTokUploadStateStore.MarkUploadStepFailed(workflowDir, limitMsg, payload.Title);
                return PublishResult.FailAndStopQueue(limitMsg);
            }

            if (hasWorkflow)
                TikTokUploadStateStore.MarkUploadStepStarted(workflowDir, payload.Title);

            PublishResult result;
            if (finalAction == FinalAction.Publish)
            {
                await TikTokBrowserActions.SubmitAsync(page, L, ct).ConfigureAwait(false);
                result = PublishResult.Success("已提交 TikTok 表单");
            }
            else if (finalAction == FinalAction.Draft)
            {
                await TikTokBrowserActions.SaveAsync(page, L, ct).ConfigureAwait(false);
                result = PublishResult.Success("已保存 TikTok 草稿");
            }
            else
            {
                L("表单已填写完成（未点保存/提交）");
                result = PublishResult.Success("已填表（只填不发）");
            }

            if (hasWorkflow)
                TikTokUploadStateStore.MarkUploadStepCompleted(workflowDir, payload.Title);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (hasWorkflow)
                TikTokUploadStateStore.MarkUploadStepFailed(
                    workflowDir, $"{ex.GetType().Name}: {ex.Message}", payload.Title);
            return PublishResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { await (chromium?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false); }
            catch { /* disconnect CDP only */ }
            pw?.Dispose();
        }
    }

    private static void ThrowIfLoginRedirect(IPage page)
    {
        if (IsLoginPage(page.Url))
            throw new InvalidOperationException("账号未登录（TikTok 跳转到登录页）");
    }

    private static string ResolveCoverPath(PublishItem item, Action<string>? log = null)
    {
        if (!string.IsNullOrWhiteSpace(item.CoverPath) && File.Exists(item.CoverPath))
        {
            var workflow = !string.IsNullOrWhiteSpace(item.ProjectDir)
                ? TikTokUploadStateStore.ResolveWorkflowProjectDir(item.ProjectDir)
                : "";
            if (!string.IsNullOrWhiteSpace(workflow))
                return TikTokCoverService.EnsureTikTok3x4Cover(item.CoverPath, workflow, log);
            return Path.GetFullPath(item.CoverPath);
        }

        var workflowDir = !string.IsNullOrWhiteSpace(item.ProjectDir)
            ? TikTokUploadStateStore.ResolveWorkflowProjectDir(item.ProjectDir)
            : "";
        var poster = TikTokCoverService.ResolvePosterPath(workflowDir, item.ProjectDir);
        if (!string.IsNullOrWhiteSpace(poster) && !string.IsNullOrWhiteSpace(workflowDir))
            return TikTokCoverService.EnsureTikTok3x4Cover(poster, workflowDir, log);

        var stem = Path.Combine(Path.GetDirectoryName(item.VideoPath) ?? "", Path.GetFileNameWithoutExtension(item.VideoPath));
        foreach (var candidate in new[]
                 {
                     stem + ".cover.jpg",
                     stem + ".cover.png",
                     stem + ".jpg",
                     stem + ".png",
                 })
        {
            if (!File.Exists(candidate)) continue;
            if (!string.IsNullOrWhiteSpace(workflowDir))
                return TikTokCoverService.EnsureTikTok3x4Cover(candidate, workflowDir, log);
            return Path.GetFullPath(candidate);
        }

        if (!string.IsNullOrWhiteSpace(item.ProjectDir))
        {
            foreach (var name in new[] { "海报图片.png", "海报图片.jpg" })
            {
                foreach (var root in new[] { workflowDir, item.ProjectDir })
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    var path = Path.Combine(root, name);
                    if (!File.Exists(path)) continue;
                    var wf = string.IsNullOrWhiteSpace(workflowDir) ? root : workflowDir;
                    return TikTokCoverService.EnsureTikTok3x4Cover(path, wf, log);
                }
            }
        }

        throw new InvalidOperationException(
            "未找到封面文件。请在发布项中指定 CoverPath，或在项目目录放置 海报图片.png。");
    }

    private static bool IsLoginPage(string url)
    {
        var lowered = (url ?? "").ToLowerInvariant();
        return lowered.Contains("tiktokdramacenter.com") && lowered.Contains("/login");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
