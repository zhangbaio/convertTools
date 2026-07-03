using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;
using Microsoft.Playwright;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Ui.Services;

/// <summary>TikTok 短剧中心 Playwright 自动化。经 WebView2 CDP 连接，调用 <see cref="TikTokBrowserActions"/>。</summary>
public sealed class TikTokPlaywrightAutomation : IPublishAutomation, IAsyncDisposable
{
    private IPlaywright? _pw;

    private async Task<IPlaywright> PwAsync() => _pw ??= await Playwright.CreateAsync();

    public async Task<PublishResult> PublishAsync(
        TikTokAccountProfile account,
        PublishItem item,
        string cdpEndpoint,
        FinalAction finalAction,
        Action<string>? log,
        CancellationToken ct)
    {
        void L(string m) => log?.Invoke(m);

        if (!File.Exists(item.VideoPath))
            return PublishResult.Fail($"视频不存在：{item.VideoPath}");

        var targetUrl = string.IsNullOrWhiteSpace(account.TiktokSeriesUrl)
            ? TikTokUrls.DefaultSeriesDraftUrl
            : account.TiktokSeriesUrl.Trim();

        var options = TikTokPublishOptions.FromAccount(account);
        var payload = TikTokPublishPayload.FromPublishItem(item);
        var recommendation = options.BuildRecommendation(item);
        var coverPath = ResolveCoverPath(item);
        var workflowDir = TikTokUploadStateStore.ResolveWorkflowProjectDir(item.ProjectDir);
        var hasWorkflow = !string.IsNullOrWhiteSpace(workflowDir);

        var pw = await PwAsync();
        await using var browser = await pw.Chromium.ConnectOverCDPAsync(cdpEndpoint);
        var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

        L("进入 TikTok 短剧草稿页…");
        await page.GotoAsync(targetUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60000,
        });
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }); }
        catch { /* SPA */ }
        await page.WaitForTimeoutAsync(2000);
        ct.ThrowIfCancellationRequested();

        if (IsLoginPage(page.Url))
            return PublishResult.Fail("账号未登录（请先在右侧浏览器完成 TikTok 登录）");

        var dailyLimit = await TikTokBrowserActions.DetectDailyEpisodeLimitAsync(page);
        if (dailyLimit is not null)
            return PublishResult.FailAndStopQueue($"检测到 TikTok 单日创建剧集上限：{dailyLimit}");

        try
        {
            var enteredEditFlow = false;
            if (hasWorkflow)
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
                    allowPlatformSearch: TikTokUploadStateStore.HasUploadStepAttempted(workflowDir));
            }

            if (!enteredEditFlow)
            {
                await TikTokBrowserActions.FillCreateInitialFieldsAsync(page, payload, options, L, ct);
                ThrowIfLoginRedirect(page);

                if (hasWorkflow)
                {
                    enteredEditFlow = await TikTokEditFlowService.MaybeRouteDuplicateToEditFlowAsync(
                        page, workflowDir, payload, options, recommendation, coverPath, L, ct);
                    ThrowIfLoginRedirect(page);
                }

                if (!enteredEditFlow)
                {
                    await TikTokBrowserActions.UploadCoverAsync(page, coverPath, L, ct);
                    ThrowIfLoginRedirect(page);

                    if (hasWorkflow)
                    {
                        enteredEditFlow = await TikTokEditFlowService.MaybeRouteDuplicateToEditFlowAsync(
                            page, workflowDir, payload, options, recommendation, coverPath, L, ct);
                        ThrowIfLoginRedirect(page);
                    }
                }

                if (!enteredEditFlow)
                {
                    await TikTokBrowserActions.FillCreateRemainingFieldsAsync(
                        page, payload, options, recommendation, coverPath, coverAlreadyUploaded: true, L, ct);
                }
            }

            dailyLimit = await TikTokBrowserActions.DetectDailyEpisodeLimitAsync(page);
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
                await TikTokBrowserActions.SubmitAsync(page, L, ct);
                result = PublishResult.Success("已提交 TikTok 表单");
            }
            else if (finalAction == FinalAction.Draft)
            {
                await TikTokBrowserActions.SaveAsync(page, L, ct);
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
                TikTokUploadStateStore.MarkUploadStepFailed(workflowDir, $"{ex.GetType().Name}: {ex.Message}", payload.Title);
            return PublishResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ThrowIfLoginRedirect(IPage page)
    {
        if (IsLoginPage(page.Url))
            throw new InvalidOperationException("账号未登录（TikTok 跳转到登录页）");
    }

    private static string ResolveCoverPath(PublishItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.CoverPath) && File.Exists(item.CoverPath))
            return Path.GetFullPath(item.CoverPath);

        var stem = Path.Combine(Path.GetDirectoryName(item.VideoPath) ?? "", Path.GetFileNameWithoutExtension(item.VideoPath));
        foreach (var candidate in new[]
                 {
                     stem + ".cover.jpg",
                     stem + ".cover.png",
                     stem + ".jpg",
                     stem + ".png",
                 })
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        if (!string.IsNullOrWhiteSpace(item.ProjectDir))
        {
            var workflow = TikTokUploadStateStore.ResolveWorkflowProjectDir(item.ProjectDir);
            foreach (var name in new[] { "海报图片.png", "海报图片.jpg" })
            {
                foreach (var root in new[] { workflow, item.ProjectDir })
                {
                    var poster = Path.Combine(root, name);
                    if (File.Exists(poster))
                        return Path.GetFullPath(poster);
                }
            }
        }

        throw new InvalidOperationException(
            "未找到封面文件。请在发布项中指定 CoverPath，或在视频同目录放置 <视频名>.cover.jpg。");
    }

    private static bool IsLoginPage(string url)
    {
        var lowered = (url ?? "").ToLowerInvariant();
        return lowered.Contains("tiktokdramacenter.com") && lowered.Contains("/login");
    }

    public ValueTask DisposeAsync()
    {
        _pw?.Dispose();
        return ValueTask.CompletedTask;
    }
}
