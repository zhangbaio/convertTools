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
    public Task<PublishResult> PublishAsync(
        TikTokAccountProfile account,
        PublishItem item,
        IEmbeddedBrowser browser,
        FinalAction finalAction,
        Action<string>? log,
        CancellationToken ct) =>
        PublishCoreAsync(account, item, browser, finalAction, log, uploadFilesPreflighted: false, ct);

    // QueueWorkerRunner always completes the upload-file preflight before invoking the UI host.
    // Keep this entry point internal so direct/scheduled callers cannot accidentally bypass it.
    internal Task<PublishResult> PublishPreflightedAsync(
        TikTokAccountProfile account,
        PublishItem item,
        IEmbeddedBrowser browser,
        FinalAction finalAction,
        Action<string>? log,
        CancellationToken ct) =>
        PublishCoreAsync(account, item, browser, finalAction, log, uploadFilesPreflighted: true, ct);

    private async Task<PublishResult> PublishCoreAsync(
        TikTokAccountProfile account,
        PublishItem item,
        IEmbeddedBrowser browser,
        FinalAction finalAction,
        Action<string>? log,
        bool uploadFilesPreflighted,
        CancellationToken ct)
    {
        void L(string m) => log?.Invoke(m);

        var consistency = TikTokUploadEpisodeConsistencyService.ValidateBeforeUpload(item);
        if (!consistency.Ok)
            return PublishResult.FailAndSkipManualIntervention(consistency.Message);

        if (!uploadFilesPreflighted)
        {
            var preflight = await TikTokUploadFilePreflightService
                .ValidateAsync(item, L, ct)
                .ConfigureAwait(false);
            if (!preflight.Ok)
                return PublishResult.FailAndSkipManualIntervention(preflight.Message);
        }

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

        string workflowDir;
        TikTokPublishOptions options;
        try
        {
            // Queue uploads normally prepare this dependency before acquiring a browser slot.
            // Keep the guard here as well so scheduled/manual publish entry points cannot bypass it.
            await TikTokProofMaterialService
                .EnsureCurrentForUploadAsync(item, account, L, ct)
                .ConfigureAwait(false);
            workflowDir = TikTokUploadStateStore.ResolveWorkflowProjectDir(item.ProjectDir);
            options = TikTokPublishOptionsBuilder.FromAccount(account, workflowDir, L);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return PublishResult.FailAndSkipManualIntervention(ex.Message);
        }
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
        if (useLaunch && account.TiktokPlaywrightUploadHeadless && finalAction == FinalAction.Publish)
        {
            L("提示：当前使用外部浏览器无头模式提交，TikTok 可能在最终提交阶段触发风控；提交后会校验原创管理状态。");
        }

        IPlaywright? pw = null;
        IBrowser? chromium = null;
        IPage? activePage = null;
        var outerCt = ct;
        CancellationTokenSource? dailyLimitCts = null;
        Task? dailyLimitWatcher = null;
        string? dailyLimitHit = null;
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
                    ? "外部浏览器登录态失效，请在「浏览器」页用内置浏览器重新登录以刷新授权文件"
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
                    allowPlatformSearch: true,
                    allowCreateFallback: false)
                    .ConfigureAwait(false);
                if (!enteredEditFlow)
                    return PublishResult.Fail("未找到可编辑草稿，编辑剧集模式不会新建上传；如需新建请使用执行勾选队列");
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
                    allowPlatformSearch: TikTokUploadStateStore.ShouldSearchPlatformForExistingDraft(workflowDir),
                    allowCreateFallback: true)
                    .ConfigureAwait(false);
            }

            if (!enteredEditFlow)
            {
                await NavigateToCreateDraftPageAsync(page, targetUrl, L, ct).ConfigureAwait(false);
                ThrowIfLoginRedirect(page);

                // The platform shows this limit as a short-lived toast immediately after the
                // local-upload action. Point-in-time checks can miss it while Playwright/CDP is
                // selecting files, so watch continuously after this run has entered a fresh
                // create page. Starting here (rather than on the leftover page) avoids treating
                // a previous project's stale toast as a limit hit for the current queue item.
                dailyLimitCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                ct = dailyLimitCts.Token;
                dailyLimitWatcher = WatchDailyEpisodeLimitAsync(page, dailyLimitCts, text =>
                {
                    dailyLimitHit = text;
                    L($"检测到 TikTok 创建剧集上限提示：{text}");
                });
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
                var limitMsg = $"TikTok 单日创建剧集上限：{dailyLimit}";
                if (hasWorkflow)
                    TikTokUploadStateStore.MarkUploadStepFailed(workflowDir, limitMsg, payload.Title);
                return PublishResult.FailAndStopQueue(limitMsg);
            }

            if (hasWorkflow)
                RecordUploadStepStarted();

            PublishResult result;
            if (finalAction == FinalAction.Publish)
            {
                await TikTokBrowserActions.SubmitAsync(
                        page,
                        L,
                        ct,
                        TikTokBrowserActions.PayloadTitleCandidates(payload))
                    .ConfigureAwait(false);
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
        catch (OperationCanceledException) when (dailyLimitHit is not null && !outerCt.IsCancellationRequested)
        {
            var message = $"TikTok 单日创建剧集上限：{dailyLimitHit}";
            if (hasWorkflow)
                TikTokUploadStateStore.MarkUploadStepFailed(workflowDir, message, payload.Title);
            return PublishResult.FailAndStopQueue(message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TikTokDailyLimitException ex)
        {
            var message = ex.Message;
            L($"检测到 TikTok 发布限制提示，任务队列将停止：{ex.LimitText}");
            if (hasWorkflow)
                TikTokUploadStateStore.MarkUploadStepFailed(workflowDir, message, payload.Title);
            return PublishResult.FailAndStopQueue(message);
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
            try { dailyLimitCts?.Cancel(); } catch { /* watcher is already stopping */ }
            if (dailyLimitWatcher is not null)
            {
                try { await dailyLimitWatcher.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* normal watcher shutdown */ }
                catch (ObjectDisposedException) { /* linked CTS already released */ }
            }
            dailyLimitCts?.Dispose();
            try { await (chromium?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false); }
            catch { /* disconnect CDP only */ }
            pw?.Dispose();
        }
    }

    /// <summary>
    /// Continuously observes the visible TikTok toast/dialog containers while the current create
    /// flow is active. The limit toast can disappear before a file chooser/CDP upload call returns.
    /// </summary>
    private static async Task WatchDailyEpisodeLimitAsync(
        IPage page,
        CancellationTokenSource watcherCts,
        Action<string> onHit)
    {
        try
        {
            while (!watcherCts.IsCancellationRequested)
            {
                string? text = null;
                try
                {
                    text = await TikTokBrowserActions.DetectDailyEpisodeLimitAsync(page).ConfigureAwait(false);
                }
                catch
                {
                    // Navigation/CDP activity can make a single DOM read fail; keep observing.
                }

                if (text is not null)
                {
                    onHit(text);
                    watcherCts.Cancel();
                    return;
                }

                await Task.Delay(250, watcherCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal completion when the upload finishes or the queue is stopped.
        }
        catch (ObjectDisposedException)
        {
            // The owning publish flow has already completed.
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
        try
        {
            await page.GotoAsync(targetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 90000,
            }).ConfigureAwait(false);
        }
        catch (PlaywrightException ex) when (IsHttpResponseCodeFailure(ex.Message))
        {
            throw new InvalidOperationException(BuildAccessDeniedMessage(targetUrl), ex);
        }

        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }).ConfigureAwait(false); }
        catch { /* SPA */ }
        await TikTokBrowserActions.DismissFloatingAssistantAsync(page, log).ConfigureAwait(false);
    }

    private static bool IsHttpResponseCodeFailure(string? message)
    {
        var value = message ?? "";
        return value.Contains("ERR_HTTP_RESPONSE_CODE_FAILURE", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("HTTP ERROR 403", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("403", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAccessDeniedMessage(string url) =>
        $"TikTok Drama Center 拒绝访问：{url}。请先在「浏览器」页确认当前账号能打开 TikTok Drama Center；如果页面显示 403，请为该账号启用可用代理/静态 IP，或在新电脑上完成一次人工验证后再上传。";

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
