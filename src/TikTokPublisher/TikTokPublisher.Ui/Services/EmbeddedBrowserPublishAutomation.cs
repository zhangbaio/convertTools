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
        var uploadStepStartedRecorded = false;
        void RecordUploadStepStarted()
        {
            if (!hasWorkflow || uploadStepStartedRecorded)
                return;
            TikTokUploadStateStore.MarkUploadStepStarted(workflowDir, payload.Title);
            uploadStepStartedRecorded = true;
        }

        var useLaunch = string.Equals(
            (account.TiktokUploadBrowserMode ?? "").Trim(), "playwright", StringComparison.OrdinalIgnoreCase);

        IPlaywright? pw = null;
        IBrowser? chromium = null;
        CancellationTokenSource? limitCts = null;
        var outerCt = ct;
        string? dailyLimitHit = null;
        IPage? activePage = null;
        try
        {
            IPage page;
            if (useLaunch)
            {
                var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
                (pw, chromium, page) = await EmbeddedBrowserAutomationBridge
                    .LaunchPageAsync(account, targetUrl, authPath, account.TiktokPlaywrightUploadHeadless, L, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                (pw, chromium, page) = await EmbeddedBrowserAutomationBridge
                    .ConnectPageAsync(browser, targetUrl, L, ct)
                    .ConfigureAwait(false);
            }
            activePage = page;
            ct.ThrowIfCancellationRequested();

            void StartDailyLimitWatch()
            {
                if (limitCts is not null)
                    return;

                // 对齐 Python _watch_daily_episode_limit：上限提示是出现时机不固定的短暂 toast，
                // 但必须在进入本次新建页后再监听，避免上一轮残留页面把当前任务误判为上限。
                limitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                ct = limitCts.Token;
                _ = WatchDailyEpisodeLimitAsync(page, limitCts, text =>
                {
                    dailyLimitHit = text;
                    L($"TikTok 检测到单日创建剧集上限提示：{text}");
                });
            }

            // 清理上一轮失败遗留的「是否离开网站」弹窗/半填表单，确保从干净页面开始。
            await TikTokBrowserActions.ResetLeftoverPageStateAsync(page, L, ct).ConfigureAwait(false);

            if (IsLoginPage(page.Url) && !useLaunch)
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
                return PublishResult.Fail(useLaunch
                    ? "独立浏览器登录态失效，请在「浏览器」页用内置浏览器重新登录以刷新授权文件"
                    : "账号未登录（请先在内置浏览器完成 TikTok 登录）");

            // 注意：此处不检测单日上限。刚连接时页面可能停留在上一个项目的残留页
            // （含旧的“已达上限”提示），会误判停队列。上限检测仅在导航到新建/编辑页后进行。

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
                    allowPlatformSearch: TikTokUploadStateStore.ShouldSearchPlatformForExistingDraft(workflowDir))
                    .ConfigureAwait(false);
            }

            if (!enteredEditFlow)
            {
                await NavigateToCreateDraftPageAsync(page, targetUrl, L, ct).ConfigureAwait(false);
                ThrowIfLoginRedirect(page);
                StartDailyLimitWatch();
                RecordUploadStepStarted();

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

            var dailyLimit = await TikTokBrowserActions.DetectDailyEpisodeLimitAsync(page).ConfigureAwait(false);
            if (dailyLimit is not null)
            {
                var limitMsg = $"检测到 TikTok 单日创建剧集上限：{dailyLimit}";
                if (hasWorkflow)
                    TikTokUploadStateStore.MarkUploadStepFailed(workflowDir, limitMsg, payload.Title);
                return PublishResult.FailAndStopQueue(limitMsg);
            }

            if (hasWorkflow)
                RecordUploadStepStarted();

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
        catch (Exception ex) when (dailyLimitHit is not null && !outerCt.IsCancellationRequested)
        {
            // 后台监视命中上限后取消主流程会抛出取消/操作异常；据 dailyLimitHit 还原为上限结果。
            _ = ex;
            var limitMsg = $"检测到 TikTok 单日创建剧集上限：{dailyLimitHit}（任务队列已停止，请明天再试）";
            if (hasWorkflow)
                TikTokUploadStateStore.MarkUploadStepFailed(workflowDir, limitMsg, payload.Title);
            return PublishResult.FailAndStopQueue(limitMsg);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failureText = $"{ex.GetType().Name}: {ex.Message}";
            if (IsTikTokCrashFailure(failureText) ||
                (activePage is not null && await TikTokBrowserActions.LooksLikeTikTokCrashPageAsync(activePage).ConfigureAwait(false)))
            {
                var message = $"内置浏览器页面崩溃，已自动跳过当前项目：{failureText}";
                L(message);
                if (hasWorkflow)
                    TikTokUploadStateStore.MarkUploadStepFailed(workflowDir, message, payload.Title);
                return PublishResult.FailAndSkipManualIntervention(message);
            }

            if (hasWorkflow)
                TikTokUploadStateStore.MarkUploadStepFailed(
                    workflowDir, failureText, payload.Title);
            return PublishResult.Fail(failureText);
        }
        finally
        {
            try { limitCts?.Cancel(); } catch { /* ignore */ }
            limitCts?.Dispose();
            try { await (chromium?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false); }
            catch { /* disconnect CDP only */ }
            pw?.Dispose();
        }
    }

    /// <summary>
    /// 后台高频轮询「当前创建剧集已达上限」toast（对齐 Python <c>_watch_daily_episode_limit</c>）。
    /// 该 toast 出现时机不固定且短暂，故需高频探测、命中即停。为适配共享 WebView2 会话，
    /// 先消化「上一个项目提交后残留的 toast」作为基线，之后针对当前项目命中即取消主流程。
    /// </summary>
    private static async Task WatchDailyEpisodeLimitAsync(
        IPage page,
        CancellationTokenSource limitCts,
        Action<string> onHit)
    {
        try
        {
            await WaitLeftoverLimitToastClearedAsync(page, limitCts.Token).ConfigureAwait(false);

            while (!limitCts.IsCancellationRequested)
            {
                string? text = null;
                try { text = await TikTokBrowserActions.DetectDailyEpisodeLimitAsync(page).ConfigureAwait(false); }
                catch { /* 页面繁忙/已释放时忽略本轮 */ }

                if (text is not null)
                {
                    onHit(text);
                    limitCts.Cancel();
                    return;
                }

                await Task.Delay(400, limitCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 主流程结束时正常退出
        }
        catch (ObjectDisposedException)
        {
            // limitCts 已随主流程释放
        }
    }

    /// <summary>
    /// 连接后页面可能仍是上一个项目残留的上限 toast；等它消失（或首次导航清除）后再进入正式监视，
    /// 避免把上一个项目的残留提示误判到当前项目头上。最多等待约 20 秒，超时也进入监视。
    /// </summary>
    private static async Task WaitLeftoverLimitToastClearedAsync(IPage page, CancellationToken ct)
    {
        string? initial = null;
        try { initial = await TikTokBrowserActions.DetectDailyEpisodeLimitAsync(page).ConfigureAwait(false); }
        catch { /* ignore */ }

        // 连接时无残留 toast：直接进入监视。
        if (initial is null)
            return;

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            string? current = null;
            try { current = await TikTokBrowserActions.DetectDailyEpisodeLimitAsync(page).ConfigureAwait(false); }
            catch { /* ignore */ }
            if (current is null)
                return;
        }
    }

    private static void ThrowIfLoginRedirect(IPage page)
    {
        if (IsLoginPage(page.Url))
            throw new InvalidOperationException("账号未登录（TikTok 跳转到登录页）");
    }

    private static bool IsTikTokCrashFailure(string? message)
    {
        var text = message ?? "";
        return TikTokBrowserActions.ContainsTikTokCrashMarker(text) ||
               text.Contains("TikTok 页面崩溃", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("TikTok 页面刷新后仍显示异常", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("页面异常", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("React 崩溃", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task NavigateToCreateDraftPageAsync(
        IPage page,
        string targetUrl,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        log?.Invoke("正在打开 TikTok 新建剧集页…");
        await page.GotoAsync(targetUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 90000,
        }).ConfigureAwait(false);
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }).ConfigureAwait(false); }
        catch { /* SPA */ }
        await TikTokBrowserActions.DismissFloatingAssistantAsync(page, log).ConfigureAwait(false);
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
