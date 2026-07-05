using Microsoft.Playwright;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation.Weixin;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;
using ShortDrama.Infrastructure.Notifications;

namespace ShortDrama.Infrastructure.Automation;

public sealed class WeixinChannelUploader : IWeixinChannelUploader
{
    private const bool AutomaticSeriesFlowOnly = true;

    private enum SeriesStageResolution
    {
        Completed,
        Retry,
        ContinueAfterManual,
        SkipProject,
        Stop
    }

    private static readonly string[] DefaultConfigNames =
    [
        "weixin-channel-autogen.json",
        "weixin-channel-submit.json",
        "weixin-channel-config.json",
        "weixin-channel-publish-test.json",
        "weixin-channel-material.json",
        "weixin-channel-test-no-final-click.json"
    ];

    private readonly IWeixinAutomationConfigLoader _configLoader;
    private readonly IWeixinAuthStateService _authStateService;
    private readonly IProjectInfoParser _projectInfoParser;
    private readonly IWorkflowInteractionService _interactionService;
    private readonly WeixinBrowserRuntimeService _browserRuntimeService;
    private readonly WeixinHomePage _homePage;
    private readonly WeixinSeriesSubmissionPage _seriesSubmissionPage;
    private readonly WeixinMaterialPublishPage _materialPublishPage;
    private readonly WeixinSystemHighlightPublishPage _systemHighlightPublishPage;
    private readonly WeixinNewDramaMountService _newDramaMountService;
    private readonly WeixinMaterialPublishDescriptionService _materialPublishDescriptionService;
    private readonly WeixinPublishOriginalityService _publishOriginalityService;
    private readonly IWeixinLoginNotificationService _loginNotificationService;

    public WeixinChannelUploader(
        IWeixinAutomationConfigLoader configLoader,
        IWeixinAuthStateService authStateService,
        IProjectInfoParser projectInfoParser,
        IWorkflowInteractionService interactionService,
        WeixinBrowserRuntimeService browserRuntimeService,
        WeixinHomePage homePage,
        WeixinSeriesSubmissionPage seriesSubmissionPage,
        WeixinMaterialPublishPage materialPublishPage,
        WeixinSystemHighlightPublishPage systemHighlightPublishPage,
        WeixinNewDramaMountService newDramaMountService,
        WeixinMaterialPublishDescriptionService materialPublishDescriptionService,
        WeixinPublishOriginalityService publishOriginalityService,
        IWeixinLoginNotificationService? loginNotificationService = null)
    {
        _configLoader = configLoader;
        _authStateService = authStateService;
        _projectInfoParser = projectInfoParser;
        _interactionService = interactionService;
        _browserRuntimeService = browserRuntimeService;
        _homePage = homePage;
        _seriesSubmissionPage = seriesSubmissionPage;
        _materialPublishPage = materialPublishPage;
        _systemHighlightPublishPage = systemHighlightPublishPage;
        _newDramaMountService = newDramaMountService;
        _materialPublishDescriptionService = materialPublishDescriptionService;
        _publishOriginalityService = publishOriginalityService;
        _loginNotificationService = loginNotificationService ?? NoopWeixinLoginNotificationService.Instance;
    }

    public async Task<WeixinUploadResult> UploadAsync(
        WeixinUploadRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(request.ProjectDir))
        {
            return new WeixinUploadResult(false, request.ProjectDir, request.ConfigPath, "上传项目目录不存在。");
        }

        var resolvedConfigPath = ResolveConfigPath(request);
        var config = await _configLoader.LoadAsync(
            resolvedConfigPath,
            request.ProjectDir,
            cancellationToken);

        if (TryBuildMaterialPublishPreflightResult(request, config, resolvedConfigPath, out var preflightResult))
        {
            return preflightResult;
        }

        progress?.Report("微信上传：检查浏览器运行时...");
        var runtimeStatus = await _browserRuntimeService.InspectAsync(cancellationToken);
        if (!runtimeStatus.IsReady)
        {
            return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, runtimeStatus.Message);
        }

        _browserRuntimeService.ConfigureEnvironment(runtimeStatus);
        progress?.Report(runtimeStatus.Message);

        var authState = await _authStateService.ResolveAsync(config, cancellationToken);
        progress?.Report(authState.Message);

        Directory.CreateDirectory(config.OutputDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(config.AuthFilePath) ?? config.ConfigDirectory);

        using var playwright = await _browserRuntimeService.CreatePlaywrightAsync(cancellationToken);
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions
            {
                ExecutablePath = runtimeStatus.BrowserExecutablePath,
                Headless = config.Browser.Headless,
                SlowMo = config.Browser.SlowMoMs,
                Args =
                [
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--start-maximized"
                ]
            });
        await using var context = await CreateBrowserContextAsync(browser, config, progress);

        var page = await context.NewPageAsync();
        progress?.Report($"微信上传：正在打开后台 {config.BaseUrl}");

        await _homePage.OpenAsync(page, config.BaseUrl, cancellationToken);
        var isLoggedIn = await _homePage.IsLoggedInAsync(page, cancellationToken);
        string? homeScreenshotPath = null;
        if (!isLoggedIn || config.Debug.SaveHtml || config.Debug.SaveText)
        {
            homeScreenshotPath = await _homePage.SaveScreenshotAsync(
                page,
                config.OutputDirectory,
                "weixin-home.png",
                cancellationToken);
            await _homePage.SaveDebugArtifactsAsync(page, config, "weixin-home", cancellationToken);
            progress?.Report($"微信上传：后台首页已打开，截图已保存到 {homeScreenshotPath}");
        }
        else
        {
            progress?.Report("微信上传：后台首页已打开。");
        }

        if (!isLoggedIn)
        {
            var loginQrScreenshotPath = await _homePage.SaveLoginQrScreenshotAsync(
                page,
                config.OutputDirectory,
                "weixin-login-qr.png",
                cancellationToken);
            await TryNotifyLoginQrRequiredAsync(
                request,
                config,
                loginQrScreenshotPath,
                progress,
                cancellationToken);
            progress?.Report("微信上传：未检测到有效登录态，请在浏览器中扫码登录。");
            var loginDecision = await WaitForLoginCompletionAsync(
                request,
                page,
                progress,
                cancellationToken);
            if (string.Equals(loginDecision, "stop", StringComparison.Ordinal))
            {
                return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "微信上传已停止，可继续运行。");
            }

            await context.StorageStateAsync(new BrowserContextStorageStateOptions
            {
                Path = config.AuthFilePath
            });
            progress?.Report($"微信上传：登录态已更新到 {config.AuthFilePath}");
        }
        else
        {
            progress?.Report("微信上传：已复用有效登录态。");
        }

        if (string.Equals(config.TaskType, "publish_videos", StringComparison.OrdinalIgnoreCase))
        {
            var publishResult = await RunMaterialPublishAsync(
                request,
                config,
                context,
                page,
                resolvedConfigPath,
                progress,
                cancellationToken);
            await context.StorageStateAsync(new BrowserContextStorageStateOptions
            {
                Path = config.AuthFilePath
            });
            progress?.Report($"微信上传：已保存登录态 {config.AuthFilePath}");
            return publishResult;
        }

        progress?.Report($"微信剧集上传：正在导航到 {config.Navigation.Section} -> {config.Navigation.Item} -> {config.Navigation.EntryButton}");
        var resolution = await ExecuteSeriesStageAsync(
            request,
            config,
            "series-navigate",
            "导航到剧集上传页面",
            () => _seriesSubmissionPage.NavigateAsync(page, config.Navigation, cancellationToken),
            progress,
            cancellationToken);
        if (TryBuildSeriesInterruptionResult(resolution, request, resolvedConfigPath, out var interruptionResult))
        {
            return interruptionResult;
        }

        resolution = await ExecuteSeriesStageAsync(
            request,
            config,
            "first-page-ready",
            "等待第一页就绪",
            () => _seriesSubmissionPage.WaitForReadyAsync(page, config.FirstPage, cancellationToken),
            progress,
            cancellationToken);
        if (TryBuildSeriesInterruptionResult(resolution, request, resolvedConfigPath, out interruptionResult))
        {
            return interruptionResult;
        }

        progress?.Report("微信剧集上传：开始自动填写第一页表单...");
        resolution = await ExecuteSeriesStageAsync(
            request,
            config,
            "first-page-actions",
            "填写第一页表单",
            () => _seriesSubmissionPage.ExecuteFirstPageActionsAsync(page, config, progress, cancellationToken),
            progress,
            cancellationToken);
        if (TryBuildSeriesInterruptionResult(resolution, request, resolvedConfigPath, out interruptionResult))
        {
            return interruptionResult;
        }
        var firstPageScreenshotPath = await _homePage.SaveScreenshotAsync(
            page,
            config.OutputDirectory,
            "weixin-series-first-page.png",
            cancellationToken);
        await _homePage.SaveDebugArtifactsAsync(page, config, "weixin-series-first-page", cancellationToken);
        progress?.Report($"微信剧集上传：第一页已填写完成，截图已保存到 {firstPageScreenshotPath}");

        resolution = await ExecuteSeriesStageAsync(
            request,
            config,
            "second-page-entry",
            "进入第二页",
            async () =>
            {
                await _seriesSubmissionPage.MoveToSecondPageAsync(page, config.FirstPage, config.SecondPage, config.OutputDirectory, progress, cancellationToken);
                await _seriesSubmissionPage.WaitForSecondPageReadyAsync(page, config.SecondPage, cancellationToken);
            },
            progress,
            cancellationToken);
        if (TryBuildSeriesInterruptionResult(resolution, request, resolvedConfigPath, out interruptionResult))
        {
            return interruptionResult;
        }

        resolution = await ExecuteSeriesStageAsync(
            request,
            config,
            "second-page-before-upload",
            "执行第二页上传前动作",
            () => _seriesSubmissionPage.ExecuteSecondPageActionsBeforeUploadAsync(page, config, progress, cancellationToken),
            progress,
            cancellationToken);
        if (TryBuildSeriesInterruptionResult(resolution, request, resolvedConfigPath, out interruptionResult))
        {
            return interruptionResult;
        }

        resolution = await ExecuteSeriesStageAsync(
            request,
            config,
            "second-page-upload",
            "上传第二页视频",
            () => _seriesSubmissionPage.UploadSecondPageVideosAsync(page, config, progress, cancellationToken),
            progress,
            cancellationToken);
        if (TryBuildSeriesInterruptionResult(resolution, request, resolvedConfigPath, out interruptionResult))
        {
            return interruptionResult;
        }

        resolution = await ExecuteSeriesStageAsync(
            request,
            config,
            "second-page-after-upload",
            "执行第二页上传后动作",
            () => _seriesSubmissionPage.ExecuteSecondPageActionsAfterUploadAsync(page, config, progress, cancellationToken),
            progress,
            cancellationToken);
        if (TryBuildSeriesInterruptionResult(resolution, request, resolvedConfigPath, out interruptionResult))
        {
            return interruptionResult;
        }
        var secondPageScreenshotPath = await _homePage.SaveScreenshotAsync(
            page,
            config.OutputDirectory,
            "weixin-series-second-page.png",
            cancellationToken);
        await _homePage.SaveDebugArtifactsAsync(page, config, "weixin-series-second-page", cancellationToken);
        progress?.Report($"微信剧集上传：第二页视频上传已完成，截图已保存到 {secondPageScreenshotPath}");

        resolution = await ExecuteSeriesStageAsync(
            request,
            config,
            "submit-page-entry",
            "进入提审页",
            async () =>
            {
                await _seriesSubmissionPage.EnterSubmitPageAsync(page, config.SecondPage, progress, cancellationToken);
                await _seriesSubmissionPage.WaitForSubmitPageReadyAsync(page, config.Submit, cancellationToken);
            },
            progress,
            cancellationToken);
        if (TryBuildSeriesInterruptionResult(resolution, request, resolvedConfigPath, out interruptionResult))
        {
            return interruptionResult;
        }
        var submitPageScreenshotPath = await _homePage.SaveScreenshotAsync(
            page,
            config.OutputDirectory,
            "weixin-series-submit-page.png",
            cancellationToken);
        await _homePage.SaveDebugArtifactsAsync(page, config, "weixin-series-submit-page", cancellationToken);
        progress?.Report($"微信剧集上传：提审页已就绪，截图已保存到 {submitPageScreenshotPath}");

        var decision = await WaitForSeriesOperatorAsync(
            request,
            config,
            progress,
            cancellationToken);

        if (string.Equals(decision, "stop", StringComparison.Ordinal))
        {
            return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "微信上传已停止，可继续运行。");
        }

        resolution = await ExecuteSeriesStageAsync(
            request,
            config,
            "submit-final",
            "执行最终提审",
            () => _seriesSubmissionPage.ExecuteFinalSubmitAsync(page, config.Submit, progress, cancellationToken),
            progress,
            cancellationToken);
        if (TryBuildSeriesInterruptionResult(resolution, request, resolvedConfigPath, out interruptionResult))
        {
            return interruptionResult;
        }
        if (config.Submit.Enabled)
        {
            progress?.Report("微信剧集上传：已执行最终提交。");
        }

        await context.StorageStateAsync(new BrowserContextStorageStateOptions
        {
            Path = config.AuthFilePath
        });
        progress?.Report($"微信剧集上传：已保存登录态 {config.AuthFilePath}");

        if (config.Browser.KeepOpenSeconds > 0)
        {
            progress?.Report($"微信上传：按配置保留浏览器 {config.Browser.KeepOpenSeconds} 秒。");
            await Task.Delay(TimeSpan.FromSeconds(config.Browser.KeepOpenSeconds), cancellationToken);
        }

        return new WeixinUploadResult(
            Ok: true,
            ProjectDir: request.ProjectDir,
            ConfigPath: resolvedConfigPath,
            Message: config.Submit.Enabled
                ? "C# 微信剧集上传已执行到最终提交。"
                : "C# 微信剧集上传已执行到提审页，等待人工最终确认。");
    }

    private static bool TryBuildMaterialPublishPreflightResult(
        WeixinUploadRequest request,
        WeixinAutomationConfig config,
        string? resolvedConfigPath,
        out WeixinUploadResult result)
    {
        result = default!;
        if (!string.Equals(config.TaskType, "publish_videos", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!config.VideoPublish.Enabled)
        {
            result = new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "当前项目已禁用微信素材上传。");
            return true;
        }

        var sourceMode = WeixinMaterialPublishPage.NormalizeVideoSourceMode(config.VideoPublish.VideoSourceMode);
        if (string.Equals(sourceMode, "system_highlight", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(sourceMode, "new_drama_mount", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var publishItems = WeixinMaterialPublishPage.ResolvePublishVideoItems(request.ProjectDir, config.VideoPublish);
            if (publishItems.Count > 0)
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            result = new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, $"素材视频解析失败：{ex.Message}");
            return true;
        }

        result = new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, BuildNoMaterialVideoMessage(sourceMode));
        return true;
    }

    private static string BuildNoMaterialVideoMessage(string sourceMode)
    {
        return sourceMode == "new_drama_mount"
            ? "新剧挂载模式未找到可发表的视频；请先下载素材视频，或把视频放入当前 workflow 项目后再点击发表素材。"
            : "当前项目未找到可发表的素材视频。";
    }

    private async Task<WeixinUploadResult> RunMaterialPublishAsync(
        WeixinUploadRequest request,
        WeixinAutomationConfig config,
        IBrowserContext context,
        IPage page,
        string? resolvedConfigPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!config.VideoPublish.Enabled)
        {
            return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "当前项目已禁用微信素材上传。");
        }

        if (string.Equals(
                WeixinMaterialPublishPage.NormalizeVideoSourceMode(config.VideoPublish.VideoSourceMode),
                "system_highlight",
                StringComparison.Ordinal))
        {
            return await RunSystemHighlightMaterialPublishAsync(
                request,
                config,
                page,
                resolvedConfigPath,
                progress,
                cancellationToken);
        }

        var materialSourceProjectDir = request.ProjectDir;
        if (string.Equals(
                WeixinMaterialPublishPage.NormalizeVideoSourceMode(config.VideoPublish.VideoSourceMode),
                "new_drama_mount",
                StringComparison.Ordinal))
        {
            var mount = await _newDramaMountService.EnsureAsync(
                request.ProjectDir,
                config,
                resolvedConfigPath,
                progress,
                cancellationToken);
            materialSourceProjectDir = mount.SourceProjectDir;
            config = config with { VideoPublish = mount.Options };
        }

        var allPublishItems = WeixinMaterialPublishPage.ResolvePublishVideoItems(materialSourceProjectDir, config.VideoPublish);
        if (allPublishItems.Count == 0)
        {
            return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "当前项目未找到可发表的素材视频。");
        }

        var projectInfo = await ResolveMaterialPublishProjectInfoAsync(
            request.ProjectDir,
            config,
            allPublishItems.Count,
            cancellationToken);

        var statePath = WeixinMaterialPublishStateService.ResolveStatePath(request.ProjectDir, config.VideoPublish.StateFile);
        var publishState = WeixinMaterialPublishStateService.Load(statePath);
        var runStrategy = WeixinMaterialPublishStateService.ResolveEffectiveRunStrategy(config.VideoPublish);
        if (config.VideoPublish.AllowDuplicatePublish)
        {
            var duplicateAction = WeixinMaterialPublishStateService.PrepareDuplicatePublishSession(
                publishState,
                allPublishItems,
                enabled: true);
            if (!string.IsNullOrWhiteSpace(duplicateAction))
            {
                WeixinMaterialPublishStateService.Save(statePath, publishState);
                progress?.Report(
                    duplicateAction == "started"
                        ? "微信素材上传：已开启新一轮重复发布，本轮会重新发布目标素材；中断后会继续跑剩余视频。"
                        : "微信素材上传：继续上一轮未完成的重复发布，跳过本轮已成功视频。");
            }
        }

        var selectedVideos = WeixinMaterialPublishStateService.SelectPublishItemsByStrategy(
            allPublishItems,
            runStrategy,
            publishState);
        if (selectedVideos.Count == 0)
        {
            if (WeixinMaterialPublishStateService.CompleteDuplicatePublishSessionIfDone(publishState, allPublishItems))
            {
                WeixinMaterialPublishStateService.Save(statePath, publishState);
            }

            progress?.Report($"微信素材上传：当前策略 {runStrategy} 下没有可执行的视频。");
            return new WeixinUploadResult(true, request.ProjectDir, resolvedConfigPath, "当前策略下没有可执行的素材视频。");
        }

        selectedVideos = await _publishOriginalityService.ApplyAsync(
            request.ProjectDir,
            selectedVideos,
            config.VideoPublish,
            progress,
            cancellationToken);

        var shortTitle = WeixinMaterialPublishPage.BuildShortTitle(projectInfo, config.VideoPublish);
        progress?.Report($"微信素材上传：准备发表 {selectedVideos.Count} 条视频。策略：{runStrategy}。");

        for (var index = 0; index < selectedVideos.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var publishItem = selectedVideos[index];
            var videoPath = publishItem.VideoPath;
            var baseDescription = WeixinMaterialPublishPage.BuildPublishDescription(projectInfo, config.VideoPublish, publishItem);
            var description = await _materialPublishDescriptionService.ResolveAsync(
                request.ProjectDir,
                projectInfo,
                config.VideoPublish,
                publishItem,
                baseDescription,
                progress,
                cancellationToken);
            progress?.Report($"微信素材上传：开始处理 {index + 1}/{selectedVideos.Count} -> 第{publishItem.EpisodeIndex}集 {Path.GetFileName(videoPath)}");
            SaveMaterialPublishState(statePath, publishState with
            {
                Entries = UpsertMaterialPublishEntry(
                    publishState.Entries,
                    publishItem.EpisodeIndex.ToString(),
                    new MaterialPublishStateEntry("running", videoPath, DateTimeOffset.Now, null))
            });

            try
            {
                await _materialPublishPage.NavigateAsync(page, config.VideoPublish.Navigation, cancellationToken);
                await _materialPublishPage.WaitForReadyAsync(page, config.VideoPublish, cancellationToken);
                await _materialPublishPage.UploadVideosAsync(page, [videoPath], config.VideoPublish, progress, cancellationToken);
                if (config.VideoPublish.FillDescription)
                {
                    await _materialPublishPage.FillDescriptionAsync(page, description, progress, cancellationToken);
                }
                if (config.VideoPublish.FillShortTitle)
                {
                    await _materialPublishPage.FillShortTitleAsync(page, shortTitle, progress, cancellationToken);
                }
                var coverPath = WeixinMaterialPublishPage.ResolvePublishCoverPath(
                    request.ProjectDir,
                    config.VideoPublish,
                    videoPath);
                if (!string.IsNullOrWhiteSpace(coverPath))
                {
                    await _materialPublishPage.ReplaceCoverAsync(page, coverPath, progress, cancellationToken);
                }
                else if (config.VideoPublish.ReplaceCoverWithLocalImage)
                {
                    progress?.Report("微信素材上传：已启用封面替换，但未找到可用封面图片，继续后续流程。");
                }

                await _materialPublishPage.ChooseOptionsAsync(page, config.VideoPublish, projectInfo.Title, progress, cancellationToken);

                var decision = await RequestDecisionAsync(
                    request,
                    stage: "material-publish-ready",
                    message: $"素材视频 {Path.GetFileName(videoPath)} 已填充完成。点击继续执行后将自动{config.VideoPublish.FinalActionText}；也可以手动接管或停止。",
                    options: null,
                    progress,
                    cancellationToken);
                if (string.Equals(decision, "stop", StringComparison.Ordinal))
                {
                    SaveMaterialPublishState(statePath, publishState with
                    {
                        Entries = UpsertMaterialPublishEntry(
                            publishState.Entries,
                            publishItem.EpisodeIndex.ToString(),
                            new MaterialPublishStateEntry("interrupted", videoPath, DateTimeOffset.Now, "用户停止"))
                    });
                    return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "微信素材上传已停止，可继续运行。");
                }

                await _materialPublishPage.FinalizeAsync(page, config.VideoPublish, progress, cancellationToken);
                await _materialPublishPage.SaveArtifactsAsync(
                    page,
                    config,
                    config.OutputDirectory,
                    $"weixin-material-{publishItem.EpisodeIndex:D2}",
                    cancellationToken);

                SaveMaterialPublishState(statePath, publishState with
                {
                    Entries = UpsertMaterialPublishEntry(
                        publishState.Entries,
                        publishItem.EpisodeIndex.ToString(),
                        new MaterialPublishStateEntry("success", videoPath, DateTimeOffset.Now, null))
                });
            }
            catch (OperationCanceledException)
            {
                SaveMaterialPublishState(statePath, publishState with
                {
                    Entries = UpsertMaterialPublishEntry(
                        publishState.Entries,
                        publishItem.EpisodeIndex.ToString(),
                        new MaterialPublishStateEntry("interrupted", videoPath, DateTimeOffset.Now, "已取消"))
                });
                throw;
            }
            catch (Exception ex)
            {
                SaveMaterialPublishState(statePath, publishState with
                {
                    Entries = UpsertMaterialPublishEntry(
                        publishState.Entries,
                        publishItem.EpisodeIndex.ToString(),
                        new MaterialPublishStateEntry("failed", videoPath, DateTimeOffset.Now, ex.Message))
                });

                if (!config.VideoPublish.PauseOnError)
                {
                    throw;
                }

                return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, $"微信素材上传失败：第{publishItem.EpisodeIndex}集，{ex.Message}");
            }
        }

        if (config.Browser.KeepOpenSeconds > 0)
        {
            progress?.Report($"微信上传：按配置保留浏览器 {config.Browser.KeepOpenSeconds} 秒。");
            await Task.Delay(TimeSpan.FromSeconds(config.Browser.KeepOpenSeconds), cancellationToken);
        }

        if (WeixinMaterialPublishStateService.CompleteDuplicatePublishSessionIfDone(publishState, allPublishItems))
        {
            WeixinMaterialPublishStateService.Save(statePath, publishState);
        }

        return new WeixinUploadResult(
            Ok: true,
            ProjectDir: request.ProjectDir,
            ConfigPath: resolvedConfigPath,
            Message: $"C# 微信素材上传已完成，共处理 {selectedVideos.Count} 条视频。");
    }

    private async Task<WeixinUploadResult> RunSystemHighlightMaterialPublishAsync(
        WeixinUploadRequest request,
        WeixinAutomationConfig config,
        IPage page,
        string? resolvedConfigPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var projectInfo = await ResolveMaterialPublishProjectInfoAsync(
            request.ProjectDir,
            config,
            Math.Max(1, config.VideoPublish.PublishCount),
            cancellationToken);
        var statePath = WeixinMaterialPublishStateService.ResolveStatePath(request.ProjectDir, config.VideoPublish.StateFile);
        var publishState = WeixinMaterialPublishStateService.Load(statePath);
        var shortTitle = WeixinMaterialPublishPage.BuildShortTitle(projectInfo, config.VideoPublish);

        var plan = await _systemHighlightPublishPage.ResolvePublishTargetsAsync(
            page,
            config.BaseUrl,
            config.VideoPublish.Navigation,
            config.VideoPublish,
            projectInfo.Title,
            progress,
            cancellationToken);

        if (plan.GenerationInProgress)
        {
            return new WeixinUploadResult(true, request.ProjectDir, resolvedConfigPath, "系统高光视频仍在生成中，已跳过当前项目。");
        }

        if (plan.SelectedCandidates.Count == 0)
        {
            progress?.Report("系统高光发布：没有可执行的高光卡片。");
            return new WeixinUploadResult(true, request.ProjectDir, resolvedConfigPath, "系统高光发布没有可执行的高光卡片。");
        }

        progress?.Report($"系统高光发布：准备发表 {plan.SelectedCandidates.Count} 个高光视频。");
        foreach (var candidate in plan.SelectedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var videoPath = WeixinSystemHighlightPublishPage.BuildVirtualVideoPath(request.ProjectDir, candidate.SlotIndex);
            var publishItem = new WeixinMaterialPublishPage.PublishVideoItem(candidate.SlotIndex, videoPath);
            var baseDescription = WeixinMaterialPublishPage.BuildPublishDescription(projectInfo, config.VideoPublish, publishItem);
            var description = await _materialPublishDescriptionService.ResolveAsync(
                request.ProjectDir,
                projectInfo,
                config.VideoPublish,
                publishItem,
                baseDescription,
                progress,
                cancellationToken);
            progress?.Report($"系统高光发布：开始处理第 {candidate.SlotIndex} 个高光视频 {candidate.TypeText} {candidate.DurationText}");
            publishState = publishState with
            {
                Entries = UpsertMaterialPublishEntry(
                    publishState.Entries,
                    candidate.SlotIndex.ToString(),
                    new MaterialPublishStateEntry("running", videoPath, DateTimeOffset.Now, null))
            };
            SaveMaterialPublishState(statePath, publishState);

            IPage? publishPage = null;
            try
            {
                publishPage = await _systemHighlightPublishPage.OpenPublishPageFromDetailPageAsync(
                    page,
                    candidate.SlotIndex,
                    config.VideoPublish,
                    progress,
                    cancellationToken);

                if (config.VideoPublish.FillDescription)
                {
                    await _materialPublishPage.EnsureDescriptionAsync(publishPage, description, progress, cancellationToken);
                }

                await _systemHighlightPublishPage.WaitForCoverPreviewReadyAsync(publishPage, progress, cancellationToken);
                if (config.VideoPublish.FillShortTitle)
                {
                    await _materialPublishPage.FillShortTitleAsync(publishPage, shortTitle, progress, cancellationToken);
                }
                var coverPath = WeixinMaterialPublishPage.ResolvePublishCoverPath(
                    request.ProjectDir,
                    config.VideoPublish,
                    videoPath);
                if (!string.IsNullOrWhiteSpace(coverPath))
                {
                    await _materialPublishPage.ReplaceCoverAsync(publishPage, coverPath, progress, cancellationToken);
                }
                else if (config.VideoPublish.ReplaceCoverWithLocalImage)
                {
                    progress?.Report("系统高光发布：已启用封面替换，但未找到可用封面图片，继续后续流程。");
                }

                await _materialPublishPage.ChooseOptionsAsync(publishPage, config.VideoPublish, projectInfo.Title, progress, cancellationToken);

                var decision = await RequestDecisionAsync(
                    request,
                    stage: "system-highlight-material-publish-ready",
                    message: $"系统高光第 {candidate.SlotIndex} 个视频已填充完成。点击继续执行后将自动{config.VideoPublish.FinalActionText}；也可以手动接管或停止。",
                    options: null,
                    progress,
                    cancellationToken);
                if (string.Equals(decision, "stop", StringComparison.Ordinal))
                {
                    publishState = publishState with
                    {
                        Entries = UpsertMaterialPublishEntry(
                            publishState.Entries,
                            candidate.SlotIndex.ToString(),
                            new MaterialPublishStateEntry("interrupted", videoPath, DateTimeOffset.Now, "用户停止"))
                    };
                    SaveMaterialPublishState(statePath, publishState);
                    return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "系统高光发布已停止，可继续运行。");
                }

                await _materialPublishPage.FinalizeAsync(publishPage, config.VideoPublish, progress, cancellationToken);
                await _materialPublishPage.SaveArtifactsAsync(
                    publishPage,
                    config,
                    config.OutputDirectory,
                    $"weixin-system-highlight-{candidate.SlotIndex:D2}",
                    cancellationToken);

                publishState = publishState with
                {
                    Entries = UpsertMaterialPublishEntry(
                        publishState.Entries,
                        candidate.SlotIndex.ToString(),
                        new MaterialPublishStateEntry("success", videoPath, DateTimeOffset.Now, null))
                };
                SaveMaterialPublishState(statePath, publishState);
                page = await _systemHighlightPublishPage.RestoreDetailPageAsync(
                    page,
                    publishPage,
                    plan.DetailUrl,
                    plan.DramaTitle,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                publishState = publishState with
                {
                    Entries = UpsertMaterialPublishEntry(
                        publishState.Entries,
                        candidate.SlotIndex.ToString(),
                        new MaterialPublishStateEntry("interrupted", videoPath, DateTimeOffset.Now, "已取消"))
                };
                SaveMaterialPublishState(statePath, publishState);
                throw;
            }
            catch (Exception ex)
            {
                publishState = publishState with
                {
                    Entries = UpsertMaterialPublishEntry(
                        publishState.Entries,
                        candidate.SlotIndex.ToString(),
                        new MaterialPublishStateEntry("failed", videoPath, DateTimeOffset.Now, ex.Message))
                };
                SaveMaterialPublishState(statePath, publishState);
                if (!config.VideoPublish.PauseOnError)
                {
                    throw;
                }

                return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, $"系统高光发布失败：第 {candidate.SlotIndex} 个，{ex.Message}");
            }
        }

        await _systemHighlightPublishPage.TryRegenerateSystemHighlightsAsync(page, config.VideoPublish, progress, cancellationToken);

        if (config.Browser.KeepOpenSeconds > 0)
        {
            progress?.Report($"微信上传：按配置保留浏览器 {config.Browser.KeepOpenSeconds} 秒。");
            await Task.Delay(TimeSpan.FromSeconds(config.Browser.KeepOpenSeconds), cancellationToken);
        }

        return new WeixinUploadResult(
            Ok: true,
            ProjectDir: request.ProjectDir,
            ConfigPath: resolvedConfigPath,
            Message: $"系统高光发布完成，共处理 {plan.SelectedCandidates.Count} 个高光视频。");
    }

    private async Task<ProjectInfo> ResolveMaterialPublishProjectInfoAsync(
        string projectDir,
        WeixinAutomationConfig config,
        int publishItemCount,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _projectInfoParser.ParseAsync(projectDir, cancellationToken);
        }
        catch when (string.Equals(
                        WeixinMaterialPublishPage.NormalizeVideoSourceMode(config.VideoPublish.VideoSourceMode),
                        "directory_publish",
                        StringComparison.Ordinal))
        {
            var title = Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "目录批量发表";
            }

            var count = Math.Max(1, publishItemCount);
            return new ProjectInfo(
                OriginalTitle: title,
                Title: title,
                Tagline: string.Empty,
                Synopsis: string.Empty,
                ShortTitle: string.Empty,
                Tags: string.Empty,
                EpisodeCount: count,
                TotalMinutes: count,
                CostAmountWan: 1m,
                CompanyName: "未填写公司",
                ProjectDir: projectDir,
                SourceFilePath: config.ConfigPath ?? projectDir);
        }
    }

    private static string ResolveMaterialPublishStatePath(string projectDir, string stateFile)
    {
        return WeixinMaterialPublishStateService.ResolveStatePath(projectDir, stateFile);
    }

    private static MaterialPublishState LoadMaterialPublishState(string path)
    {
        return WeixinMaterialPublishStateService.Load(path);
    }

    private static void SaveMaterialPublishState(string path, MaterialPublishState state)
    {
        WeixinMaterialPublishStateService.Save(path, state);
    }

    private static IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> SelectPublishItemsByStrategy(
        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> items,
        string runStrategy,
        MaterialPublishState state)
    {
        return WeixinMaterialPublishStateService.SelectPublishItemsByStrategy(items, runStrategy, state);
    }

    private static IReadOnlyDictionary<string, MaterialPublishStateEntry> UpsertMaterialPublishEntry(
        IReadOnlyDictionary<string, MaterialPublishStateEntry> source,
        string key,
        MaterialPublishStateEntry value)
    {
        return WeixinMaterialPublishStateService.UpsertEntry(source, key, value);
    }

    private async Task<string> WaitForLoginCompletionAsync(
        WeixinUploadRequest request,
        IPage page,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (AutomaticSeriesFlowOnly)
        {
            var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(300);
            while (DateTimeOffset.UtcNow < timeoutAt)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await _homePage.IsLoggedInAsync(page, cancellationToken))
                {
                    progress?.Report("微信上传：已确认登录成功。");
                    return "resume";
                }

                await Task.Delay(1000, cancellationToken);
            }

            throw new TimeoutException("微信上传：等待扫码登录超时。");
        }

        while (!await _homePage.IsLoggedInAsync(page, cancellationToken))
        {
            var decision = await RequestDecisionAsync(
                request,
                stage: "login-required",
                message: "未检测到可复用登录态。请在打开的浏览器中扫码登录视频号后台，完成后点击继续执行；也可以先切到人工处理模式。",
                options: null,
                progress,
                cancellationToken);
            if (string.Equals(decision, "stop", StringComparison.Ordinal))
            {
                progress?.Report("微信上传：登录阶段已停止。");
                return decision;
            }
        }

        progress?.Report("微信上传：已确认登录成功。");
        return "resume";
    }

    private static async Task<IBrowserContext> CreateBrowserContextAsync(
        IBrowser browser,
        WeixinAutomationConfig config,
        IProgress<string>? progress)
    {
        var contextOptions = new BrowserNewContextOptions
        {
            UserAgent = config.Browser.UserAgent,
            ViewportSize = config.Browser.Headless
                ? new ViewportSize
                {
                    Width = config.Browser.Viewport.Width,
                    Height = config.Browser.Viewport.Height
                }
                : ViewportSize.NoViewport
        };
        if (!string.IsNullOrWhiteSpace(config.AuthFilePath) && File.Exists(config.AuthFilePath))
        {
            contextOptions.StorageStatePath = config.AuthFilePath;
            progress?.Report($"微信上传：已复用登录态文件 {config.AuthFilePath}");
        }

        try
        {
            return await browser.NewContextAsync(contextOptions);
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(contextOptions.StorageStatePath))
        {
            progress?.Report($"微信上传：登录态文件读取失败，改用全新会话：{ex.Message}");
            contextOptions.StorageStatePath = null;
            return await browser.NewContextAsync(contextOptions);
        }
    }

    private async Task TryNotifyLoginQrRequiredAsync(
        WeixinUploadRequest request,
        WeixinAutomationConfig config,
        string? screenshotPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await _loginNotificationService.NotifyLoginRequiredAsync(
                new WeixinLoginNotificationRequest(
                    ProjectKey: request.ProjectKey,
                    DisplayName: request.DisplayName,
                    ProjectDirectory: request.ProjectDir,
                    BaseUrl: config.BaseUrl,
                    AuthFilePath: config.AuthFilePath,
                    ScreenshotPath: screenshotPath,
                    Message: "检测到当前登录态不可复用，请在浏览器中扫码登录视频号后台后继续执行。"),
                cancellationToken);
            progress?.Report("微信上传：已发送登录二维码提醒。");
        }
        catch (Exception ex)
        {
            progress?.Report($"微信上传：登录二维码提醒发送失败：{ex.Message}");
        }
    }

    private async Task<string> WaitForSeriesOperatorAsync(
        WeixinUploadRequest request,
        WeixinAutomationConfig config,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (AutomaticSeriesFlowOnly)
        {
            progress?.Report("微信剧集上传：提审页已就绪，自动继续后续流程。");
            return "resume";
        }

        return await RequestDecisionAsync(
            request,
            stage: "submit-ready",
            message: config.Submit.Enabled
                ? "提审页已就绪。确认页面内容无误后点击继续执行，系统将自动点击最终提审按钮；也可以停止当前上传步骤。"
                : "提审页已就绪。请手动检查并决定是否最终提审；处理完成后点击继续执行，或停止当前上传步骤。",
            options: null,
            progress,
            cancellationToken);
    }

    private async Task<string> RequestDecisionAsync(
        WeixinUploadRequest request,
        string stage,
        string message,
        IReadOnlyList<string>? options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var decision = await _interactionService.RequestDecisionAsync(
            new WorkflowInteractionRequest(
                RequestId: Guid.NewGuid().ToString("N"),
                ProjectKey: request.ProjectKey,
                DisplayName: request.DisplayName,
                StepType: "weixin-upload",
                Scope: "project",
                Stage: stage,
                Message: message,
                Options: options ?? ["manual", "resume", "stop"]),
            cancellationToken);

        while (string.Equals(decision, "manual", StringComparison.Ordinal))
        {
            progress?.Report("微信上传：已切换到人工处理模式，等待继续或停止。");
            var manualOptions = (options ?? ["manual", "resume", "stop"])
                .Where(option => !string.Equals(option, "manual", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            decision = await _interactionService.RequestDecisionAsync(
                new WorkflowInteractionRequest(
                    RequestId: Guid.NewGuid().ToString("N"),
                    ProjectKey: request.ProjectKey,
                    DisplayName: request.DisplayName,
                    StepType: "weixin-upload",
                    Scope: "project",
                    Stage: "manual",
                    Message: "浏览器已交由你手动处理。完成当前页面操作后点击继续执行，或停止当前上传步骤。",
                    Options: manualOptions),
                cancellationToken);
        }

        progress?.Report($"微信上传：收到操作决定 {decision}");
        return decision;
    }

    private async Task<SeriesStageResolution> ExecuteSeriesStageAsync(
        WeixinUploadRequest request,
        WeixinAutomationConfig config,
        string stage,
        string stageLabel,
        Func<Task> action,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await action();
                return SeriesStageResolution.Completed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (AutomaticSeriesFlowOnly)
                {
                    throw new InvalidOperationException($"{stageLabel}失败：{ex.Message}", ex);
                }

                if (!config.VideoPublish.PauseOnError)
                {
                    throw new InvalidOperationException($"{stageLabel}失败：{ex.Message}", ex);
                }

                progress?.Report($"微信剧集上传：{stageLabel}失败，等待人工处理。{ex.Message}");
                var decision = await RequestDecisionAsync(
                    request,
                    stage,
                    $"{stageLabel}失败：{ex.Message}。你可以先人工处理页面，再继续；也可以跳过当前项目或停止当前步骤。",
                    ["manual", "resume", "skip_project", "stop"],
                    progress,
                    cancellationToken);

                switch (decision)
                {
                    case "resume":
                        progress?.Report($"微信剧集上传：阶段 {stageLabel} 将按当前页面状态重试。");
                        continue;
                    case "skip_project":
                        progress?.Report($"微信剧集上传：已跳过当前项目，阶段 {stageLabel}。");
                        return SeriesStageResolution.SkipProject;
                    case "stop":
                        progress?.Report($"微信剧集上传：已停止当前步骤，阶段 {stageLabel}。");
                        return SeriesStageResolution.Stop;
                    default:
                        continue;
                }
            }
        }
    }

    private static bool TryBuildSeriesInterruptionResult(
        SeriesStageResolution resolution,
        WeixinUploadRequest request,
        string? resolvedConfigPath,
        out WeixinUploadResult result)
    {
        switch (resolution)
        {
            case SeriesStageResolution.SkipProject:
                result = new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "微信剧集上传已跳过当前项目。");
                return true;
            case SeriesStageResolution.Stop:
                result = new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "微信剧集上传已停止，可继续运行。");
                return true;
            default:
                result = default!;
                return false;
        }
    }

    private static string? ResolveConfigPath(WeixinUploadRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ConfigPath) && File.Exists(request.ConfigPath))
        {
            return request.ConfigPath;
        }

        var names = string.IsNullOrWhiteSpace(request.ConfigName)
            ? DefaultConfigNames
            : [request.ConfigName];

        foreach (var name in names)
        {
            var candidate = Path.Combine(request.ProjectDir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
