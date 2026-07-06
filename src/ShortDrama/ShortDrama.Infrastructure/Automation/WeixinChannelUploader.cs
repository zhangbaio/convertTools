using Microsoft.Playwright;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation.Weixin;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;
using ShortDrama.Infrastructure.Notifications;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    private static readonly JsonSerializerOptions MergeJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };
    private const double MergePublishFallbackFps = 25.0;

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

        MaterialPublishPreparation? materialPublishPreparation = null;
        if (string.Equals(config.TaskType, "publish_videos", StringComparison.OrdinalIgnoreCase))
        {
            var preparationResult = await PrepareMaterialPublishBeforeBrowserAsync(
                request,
                config,
                resolvedConfigPath,
                progress,
                cancellationToken);
            if (preparationResult.CompletedResult is not null)
            {
                return preparationResult.CompletedResult;
            }

            materialPublishPreparation = preparationResult.Preparation;
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
                materialPublishPreparation,
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

    private sealed record MaterialPublishPreparation(
        WeixinAutomationConfig Config,
        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> AllPublishItems,
        ProjectInfo ProjectInfo,
        string StatePath,
        MaterialPublishState PublishState,
        string RunStrategy,
        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> SelectedVideos);

    private async Task<(WeixinUploadResult? CompletedResult, MaterialPublishPreparation? Preparation)> PrepareMaterialPublishBeforeBrowserAsync(
        WeixinUploadRequest request,
        WeixinAutomationConfig config,
        string? resolvedConfigPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(config.TaskType, "publish_videos", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        if (!config.VideoPublish.Enabled)
        {
            return (new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "当前项目已禁用微信素材上传。"), null);
        }

        var sourceMode = WeixinMaterialPublishPage.NormalizeVideoSourceMode(config.VideoPublish.VideoSourceMode);
        if (string.Equals(sourceMode, "system_highlight", StringComparison.Ordinal))
        {
            return (null, null);
        }

        var materialSourceProjectDir = request.ProjectDir;
        if (string.Equals(sourceMode, "new_drama_mount", StringComparison.Ordinal))
        {
            var mount = await _newDramaMountService.EnsureAsync(
                request.ProjectDir,
                config,
                resolvedConfigPath,
                progress,
                cancellationToken);
            materialSourceProjectDir = mount.SourceProjectDir;
            config = config with { VideoPublish = mount.Options };
            sourceMode = WeixinMaterialPublishPage.NormalizeVideoSourceMode(config.VideoPublish.VideoSourceMode);
        }

        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> allPublishItems;
        try
        {
            allPublishItems = WeixinMaterialPublishPage.ResolvePublishVideoItems(materialSourceProjectDir, config.VideoPublish);
        }
        catch (Exception ex)
        {
            return (new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, $"素材视频解析失败：{ex.Message}"), null);
        }

        if (allPublishItems.Count == 0)
        {
            return (new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, BuildNoMaterialVideoMessage(sourceMode)), null);
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
            return (new WeixinUploadResult(true, request.ProjectDir, resolvedConfigPath, "当前策略下没有可执行的素材视频。"), null);
        }

        selectedVideos = await PrepareMergePublishVideosAsync(
            request.ProjectDir,
            projectInfo,
            config.VideoPublish,
            selectedVideos,
            progress,
            cancellationToken);

        selectedVideos = await _publishOriginalityService.ApplyAsync(
            request.ProjectDir,
            selectedVideos,
            config.VideoPublish,
            progress,
            cancellationToken);

        return (null, new MaterialPublishPreparation(
            config,
            allPublishItems,
            projectInfo,
            statePath,
            publishState,
            runStrategy,
            selectedVideos));
    }

    private async Task<WeixinUploadResult> RunMaterialPublishAsync(
        WeixinUploadRequest request,
        WeixinAutomationConfig config,
        IBrowserContext context,
        IPage page,
        string? resolvedConfigPath,
        MaterialPublishPreparation? preparation,
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

        if (preparation is null)
        {
            return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "素材上传准备失败：未生成可发布任务。");
        }

        config = preparation.Config;
        var allPublishItems = preparation.AllPublishItems;
        var projectInfo = preparation.ProjectInfo;
        var statePath = preparation.StatePath;
        var publishState = preparation.PublishState;
        var runStrategy = preparation.RunStrategy;
        var selectedVideos = preparation.SelectedVideos;

        var shortTitle = WeixinMaterialPublishPage.BuildShortTitle(projectInfo, config.VideoPublish);
        progress?.Report($"微信素材上传：准备发表 {selectedVideos.Count} 条视频。策略：{runStrategy}。");

        for (var index = 0; index < selectedVideos.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var publishItem = selectedVideos[index];
            var videoPath = publishItem.VideoPath;
            var publishStateKeys = ResolveMaterialPublishStateKeys(publishItem);
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
            publishState = publishState with
            {
                Entries = UpsertMaterialPublishEntries(
                    publishState.Entries,
                    publishStateKeys,
                    new MaterialPublishStateEntry("running", videoPath, DateTimeOffset.Now, null))
            };
            SaveMaterialPublishState(statePath, publishState);

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
                    publishState = publishState with
                    {
                        Entries = UpsertMaterialPublishEntries(
                            publishState.Entries,
                            publishStateKeys,
                            new MaterialPublishStateEntry("interrupted", videoPath, DateTimeOffset.Now, "用户停止"))
                    };
                    SaveMaterialPublishState(statePath, publishState);
                    return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "微信素材上传已停止，可继续运行。");
                }

                await _materialPublishPage.FinalizeAsync(page, config.VideoPublish, progress, cancellationToken);
                await _materialPublishPage.SaveArtifactsAsync(
                    page,
                    config,
                    config.OutputDirectory,
                    $"weixin-material-{publishItem.EpisodeIndex:D2}",
                    cancellationToken);

                publishState = publishState with
                {
                    Entries = UpsertMaterialPublishEntries(
                        publishState.Entries,
                        publishStateKeys,
                        new MaterialPublishStateEntry("success", videoPath, DateTimeOffset.Now, null))
                };
                SaveMaterialPublishState(statePath, publishState);
            }
            catch (OperationCanceledException)
            {
                publishState = publishState with
                {
                    Entries = UpsertMaterialPublishEntries(
                        publishState.Entries,
                        publishStateKeys,
                        new MaterialPublishStateEntry("interrupted", videoPath, DateTimeOffset.Now, "已取消"))
                };
                SaveMaterialPublishState(statePath, publishState);
                throw;
            }
            catch (Exception ex)
            {
                publishState = publishState with
                {
                    Entries = UpsertMaterialPublishEntries(
                        publishState.Entries,
                        publishStateKeys,
                        new MaterialPublishStateEntry("failed", videoPath, DateTimeOffset.Now, ex.Message))
                };
                SaveMaterialPublishState(statePath, publishState);

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

    private static async Task<IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem>> PrepareMergePublishVideosAsync(
        string projectDir,
        ProjectInfo projectInfo,
        WeixinVideoPublishOptions options,
        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> selectedVideos,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!options.MergePublishEnabled || selectedVideos.Count == 0)
        {
            return selectedVideos;
        }

        if (selectedVideos.Count == 1)
        {
            progress?.Report("合并发布：已启用，但本轮只有 1 个素材，直接发布原视频。");
            return selectedVideos;
        }

        var groupSize = Math.Max(0, options.MergePublishGroupSize);
        if (groupSize == 1)
        {
            progress?.Report("合并发布：合并频率为 1，无需生成合并视频。");
            return selectedVideos;
        }

        var groups = SplitMergePublishGroups(selectedVideos, groupSize);
        progress?.Report(
            groupSize > 0
                ? $"合并发布：已启用，{selectedVideos.Count} 个素材每 {groupSize} 个合并，预计发布 {groups.Count} 次。"
                : $"合并发布：已启用，{selectedVideos.Count} 个素材全部合并为 1 次发布。");

        var mergedVideos = new List<WeixinMaterialPublishPage.PublishVideoItem>();
        for (var index = 0; index < groups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = groups[index];
            if (group.Count <= 1)
            {
                mergedVideos.Add(group[0]);
                continue;
            }

            var outputPath = BuildMergePublishOutputPath(projectDir, projectInfo, group, groupSize);
            progress?.Report($"合并发布：生成 {index + 1}/{groups.Count} -> {Path.GetFileName(outputPath)}");
            var mergedPath = await MergePublishVideosAsync(
                group,
                outputPath,
                options,
                progress,
                cancellationToken);
            var baseDescription = WeixinMaterialPublishPage.BuildPublishDescription(projectInfo, options, group[0]);
            WriteMergePublishSidecar(mergedPath, baseDescription, group);
            mergedVideos.Add(group[0] with { VideoPath = mergedPath });
        }

        return mergedVideos;
    }

    private static IReadOnlyList<IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem>> SplitMergePublishGroups(
        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> selectedVideos,
        int groupSize)
    {
        if (selectedVideos.Count == 0)
        {
            return [];
        }

        if (groupSize <= 0)
        {
            return [selectedVideos.ToArray()];
        }

        var groups = new List<IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem>>();
        for (var index = 0; index < selectedVideos.Count; index += groupSize)
        {
            groups.Add(selectedVideos.Skip(index).Take(groupSize).ToArray());
        }

        return groups;
    }

    private static string BuildMergePublishOutputPath(
        string projectDir,
        ProjectInfo projectInfo,
        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> group,
        int groupSize)
    {
        var title = !string.IsNullOrWhiteSpace(projectInfo.Title)
            ? projectInfo.Title
            : (!string.IsNullOrWhiteSpace(projectInfo.OriginalTitle)
                ? projectInfo.OriginalTitle
                : Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        var safeTitle = SanitizeFileName(title);
        if (string.IsNullOrWhiteSpace(safeTitle))
        {
            safeTitle = "material";
        }

        string suffix;
        if (groupSize <= 0 || group.Count == 0)
        {
            suffix = "合并发布";
        }
        else
        {
            var firstEpisode = group[0].EpisodeIndex;
            var lastEpisode = group[^1].EpisodeIndex;
            suffix = firstEpisode == lastEpisode
                ? $"合并发布-{firstEpisode}"
                : $"合并发布-{firstEpisode}-{lastEpisode}";
        }

        return Path.Combine(projectDir, "合并发布", $"{safeTitle}-{suffix}.mp4");
    }

    private static async Task<string> MergePublishVideosAsync(
        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> group,
        string outputPath,
        WeixinVideoPublishOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var inputPaths = group
            .Select(item => Path.GetFullPath(item.VideoPath))
            .Where(File.Exists)
            .ToArray();
        if (inputPaths.Length == 0)
        {
            throw new InvalidOperationException("合并发布失败：没有可合并的视频。");
        }

        if (inputPaths.Length == 1)
        {
            return inputPaths[0];
        }

        var normalizedOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(normalizedOutputPath) ?? ".");

        var ffmpeg = ResolveMergeFfmpegBinary(options);
        var ffprobe = ResolveMergeFfprobeBinary(options);
        var inputProbes = new List<MergePublishProbe>();
        var expectedTotalDuration = 0.0;
        foreach (var path in inputPaths)
        {
            var probe = await ProbeMergePublishMediaAsync(ffprobe, path, cancellationToken);
            if (probe is null)
            {
                continue;
            }

            inputProbes.Add(probe);
            expectedTotalDuration += Math.Max(0.0, probe.DurationSeconds);
        }

        var (targetWidth, targetHeight, targetFps) = ResolveMergePublishReencodeTargets(inputProbes);
        var copyMergeAllowed = CanCopyMergePublishInputs(inputProbes, inputPaths.Length);
        var inputs = BuildMergePublishInputs(group);
        if (await HasReusableMergedOutputAsync(
                normalizedOutputPath,
                inputs,
                expectedTotalDuration,
                copyMergeAllowed,
                targetWidth,
                targetHeight,
                ffprobe,
                cancellationToken))
        {
            progress?.Report($"合并发布：复用已生成视频 {Path.GetFileName(normalizedOutputPath)}");
            return normalizedOutputPath;
        }

        var tempPrefix = Path.Combine(
            Path.GetDirectoryName(normalizedOutputPath) ?? ".",
            $"{Path.GetFileNameWithoutExtension(normalizedOutputPath)}.tmp-{Guid.NewGuid():N}");
        var tempOutputPath = tempPrefix + Path.GetExtension(normalizedOutputPath);
        var fileListPath = tempPrefix + ".txt";

        await File.WriteAllTextAsync(
            fileListPath,
            string.Join(Environment.NewLine, inputPaths.Select(BuildConcatFileLine)) + Environment.NewLine,
            Encoding.UTF8,
            cancellationToken);

        try
        {
            progress?.Report($"合并发布：开始合并 {inputPaths.Length} 个片段 -> {Path.GetFileName(normalizedOutputPath)}");
            var completed = copyMergeAllowed
                ? await RunMergeProcessAsync(
                    ffmpeg,
                    BuildMergePublishCopyArguments(fileListPath, tempOutputPath),
                    cancellationToken)
                : new MergeProcessResult(1, string.Empty, "input parameters differ");

            if (completed.ExitCode == 0 &&
                !await HasValidMergedDurationAsync(
                    tempOutputPath,
                    expectedTotalDuration,
                    copyMergeAllowed,
                    targetWidth,
                    targetHeight,
                    ffprobe,
                    cancellationToken))
            {
                completed = new MergeProcessResult(1, completed.Stdout, "invalid merged duration");
            }

            if (completed.ExitCode != 0)
            {
                TryDeleteFile(tempOutputPath);
                progress?.Report(copyMergeAllowed
                    ? "合并发布：无损合并不可用，改用重新编码合并。"
                    : "合并发布：输入视频参数不一致，改用重新编码合并。");
                var includeAudio = inputProbes.Count == inputPaths.Length &&
                                   inputProbes.All(probe => !string.IsNullOrWhiteSpace(probe.AudioCodecName));
                completed = await RunMergeProcessAsync(
                    ffmpeg,
                    BuildMergePublishReencodeArguments(
                        inputPaths,
                        tempOutputPath,
                        includeAudio,
                        targetWidth,
                        targetHeight,
                        targetFps),
                    cancellationToken);
            }

            if (completed.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(completed.Stderr)
                    ? "ffmpeg 合并失败"
                    : completed.Stderr.Trim();
                throw new InvalidOperationException(message);
            }

            if (!await HasValidMergedDurationAsync(
                    tempOutputPath,
                    expectedTotalDuration,
                    copyMergeAllowed,
                    targetWidth,
                    targetHeight,
                    ffprobe,
                    cancellationToken))
            {
                throw new InvalidOperationException("合并发布失败：输出视频时长异常。");
            }

            File.Move(tempOutputPath, normalizedOutputPath, overwrite: true);
            WriteMergePublishInputsManifest(normalizedOutputPath, inputs);
            return normalizedOutputPath;
        }
        finally
        {
            TryDeleteFile(fileListPath);
            TryDeleteFile(tempOutputPath);
        }
    }

    private static IReadOnlyList<string> BuildMergePublishCopyArguments(string fileListPath, string outputPath) =>
    [
        "-y",
        "-hide_banner",
        "-loglevel",
        "error",
        "-f",
        "concat",
        "-safe",
        "0",
        "-i",
        fileListPath,
        "-c",
        "copy",
        "-movflags",
        "+faststart",
        outputPath
    ];

    private static IReadOnlyList<string> BuildMergePublishReencodeArguments(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        bool includeAudio,
        int targetWidth,
        int targetHeight,
        double targetFps)
    {
        var args = new List<string>
        {
            "-y",
            "-hide_banner",
            "-loglevel",
            "error"
        };
        foreach (var path in inputPaths)
        {
            args.Add("-i");
            args.Add(path);
        }

        var (filterComplex, audioMap) = BuildMergePublishReencodeFilter(
            inputPaths.Count,
            includeAudio,
            targetWidth,
            targetHeight,
            targetFps);
        args.AddRange(
        [
            "-filter_complex",
            filterComplex,
            "-map",
            "[v]",
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-pix_fmt",
            "yuv420p"
        ]);
        if (includeAudio)
        {
            args.AddRange(
            [
                "-map",
                audioMap ?? "[a]",
                "-c:a",
                "aac",
                "-b:a",
                "128k"
            ]);
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(
        [
            "-movflags",
            "+faststart",
            outputPath
        ]);
        return args;
    }

    private static (string FilterComplex, string? AudioMap) BuildMergePublishReencodeFilter(
        int inputCount,
        bool includeAudio,
        int targetWidth,
        int targetHeight,
        double targetFps)
    {
        var filterParts = new List<string>();
        var concatInputs = new StringBuilder();
        var fpsText = targetFps.ToString("0.######", CultureInfo.InvariantCulture);
        for (var index = 0; index < inputCount; index++)
        {
            filterParts.Add(
                $"[{index}:v:0]scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=decrease," +
                $"pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fpsText},format=yuv420p[v{index}]");
            concatInputs.Append($"[v{index}]");
            if (includeAudio)
            {
                filterParts.Add(
                    $"[{index}:a:0]aresample=async=1:first_pts=0," +
                    $"aformat=sample_rates=48000:sample_fmts=fltp:channel_layouts=stereo,asetpts=PTS-STARTPTS[a{index}]");
                concatInputs.Append($"[a{index}]");
            }
        }

        if (includeAudio)
        {
            filterParts.Add($"{concatInputs}concat=n={inputCount}:v=1:a=1[v][a]");
            return (string.Join(';', filterParts), "[a]");
        }

        filterParts.Add($"{concatInputs}concat=n={inputCount}:v=1:a=0[v]");
        return (string.Join(';', filterParts), null);
    }

    private static async Task<MergePublishProbe?> ProbeMergePublishMediaAsync(
        string ffprobe,
        string path,
        CancellationToken cancellationToken)
    {
        var result = await RunMergeProcessAsync(
            ffprobe,
            [
                "-v",
                "error",
                "-print_format",
                "json",
                "-show_streams",
                "-show_format",
                path
            ],
            cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            var root = document.RootElement;
            var duration = 0.0;
            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var durationElement))
            {
                duration = ParseJsonDouble(durationElement);
            }

            JsonElement? videoStream = null;
            JsonElement? audioStream = null;
            if (root.TryGetProperty("streams", out var streams) &&
                streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out var codecTypeElement))
                    {
                        continue;
                    }

                    var codecType = codecTypeElement.GetString();
                    if (videoStream is null && string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        videoStream = stream.Clone();
                    }
                    else if (audioStream is null && string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        audioStream = stream.Clone();
                    }
                }
            }

            var width = videoStream is { } video ? ParseJsonInt(GetPropertyOrDefault(video, "width")) : 0;
            var height = videoStream is { } videoForHeight ? ParseJsonInt(GetPropertyOrDefault(videoForHeight, "height")) : 0;
            var fps = videoStream is { } videoForFps
                ? ParseFrameRate(
                    GetJsonString(GetPropertyOrDefault(videoForFps, "avg_frame_rate"))
                    ?? GetJsonString(GetPropertyOrDefault(videoForFps, "r_frame_rate")))
                : 0.0;
            var audioCodec = audioStream is { } audio && audio.TryGetProperty("codec_name", out var codecName)
                ? codecName.GetString() ?? string.Empty
                : string.Empty;
            return new MergePublishProbe(duration, width, height, fps, audioCodec.Trim().ToLowerInvariant());
        }
        catch
        {
            return null;
        }
    }

    private static (int Width, int Height, double Fps) ResolveMergePublishReencodeTargets(
        IReadOnlyList<MergePublishProbe> probes)
    {
        var width = probes.Select(probe => probe.Width).Where(value => value > 0).DefaultIfEmpty(1080).Max();
        var height = probes.Select(probe => probe.Height).Where(value => value > 0).DefaultIfEmpty(1920).Max();
        var fps = probes.Select(probe => probe.FrameRateFps).Where(value => value > 0).DefaultIfEmpty(MergePublishFallbackFps).Max();
        return (EnsureEvenDimension(width, 1080), EnsureEvenDimension(height, 1920), Math.Max(1.0, fps));
    }

    private static bool CanCopyMergePublishInputs(IReadOnlyList<MergePublishProbe> probes, int inputCount)
    {
        if (probes.Count != inputCount)
        {
            return false;
        }

        if (probes.Count < 2)
        {
            return true;
        }

        var first = probes[0];
        if (first.Width <= 0 || first.Height <= 0)
        {
            return false;
        }

        foreach (var probe in probes.Skip(1))
        {
            if (probe.Width != first.Width || probe.Height != first.Height)
            {
                return false;
            }

            if (first.FrameRateFps > 0 &&
                probe.FrameRateFps > 0 &&
                Math.Abs(probe.FrameRateFps - first.FrameRateFps) > 0.01)
            {
                return false;
            }

            if (!string.Equals(probe.AudioCodecName, first.AudioCodecName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> HasReusableMergedOutputAsync(
        string outputPath,
        IReadOnlyList<MergePublishInput> inputs,
        double expectedTotalDuration,
        bool copyMergeAllowed,
        int targetWidth,
        int targetHeight,
        string ffprobe,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(outputPath))
        {
            return false;
        }

        var outputInfo = new FileInfo(outputPath);
        if (outputInfo.Length <= 0)
        {
            return false;
        }

        var latestInputMtime = inputs.Max(input => input.MtimeTicks);
        if (outputInfo.LastWriteTimeUtc.Ticks < latestInputMtime)
        {
            return false;
        }

        var manifestPath = Path.ChangeExtension(outputPath, ".inputs.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
            if (!document.RootElement.TryGetProperty("inputs", out var inputsElement) ||
                inputsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var existingInputs = JsonSerializer.Deserialize<List<MergePublishInput>>(
                inputsElement.GetRawText(),
                MergeJsonOptions) ?? [];
            if (!AreSameMergeInputs(existingInputs, inputs))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return await HasValidMergedDurationAsync(
            outputPath,
            expectedTotalDuration,
            copyMergeAllowed,
            targetWidth,
            targetHeight,
            ffprobe,
            cancellationToken);
    }

    private static async Task<bool> HasValidMergedDurationAsync(
        string candidatePath,
        double expectedTotalDuration,
        bool copyMergeAllowed,
        int targetWidth,
        int targetHeight,
        string ffprobe,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(candidatePath) || new FileInfo(candidatePath).Length <= 0)
        {
            return false;
        }

        if (expectedTotalDuration <= 0)
        {
            return true;
        }

        var probe = await ProbeMergePublishMediaAsync(ffprobe, candidatePath, cancellationToken);
        if (probe is null)
        {
            return false;
        }

        if (!copyMergeAllowed && (probe.Width != targetWidth || probe.Height != targetHeight))
        {
            return false;
        }

        var durationTolerance = Math.Max(5.0, expectedTotalDuration * 0.15);
        return Math.Abs(Math.Max(0.0, probe.DurationSeconds) - expectedTotalDuration) <= durationTolerance;
    }

    private static IReadOnlyList<MergePublishInput> BuildMergePublishInputs(
        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> group)
    {
        return group.Select(item =>
        {
            var path = Path.GetFullPath(item.VideoPath);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new FileNotFoundException("合并发布素材文件不存在。", path);
            }

            return new MergePublishInput(
                EpisodeIndex: item.EpisodeIndex,
                Path: path,
                FileName: Path.GetFileName(path),
                Size: info.Length,
                MtimeTicks: info.LastWriteTimeUtc.Ticks);
        }).ToArray();
    }

    private static bool AreSameMergeInputs(
        IReadOnlyList<MergePublishInput> left,
        IReadOnlyList<MergePublishInput> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].EpisodeIndex != right[index].EpisodeIndex ||
                left[index].Size != right[index].Size ||
                left[index].MtimeTicks != right[index].MtimeTicks ||
                !string.Equals(Path.GetFullPath(left[index].Path), Path.GetFullPath(right[index].Path), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteMergePublishInputsManifest(
        string outputPath,
        IReadOnlyList<MergePublishInput> inputs)
    {
        var payload = new
        {
            inputs,
            updated_at = DateTimeOffset.Now.ToString("O")
        };
        File.WriteAllText(
            Path.ChangeExtension(outputPath, ".inputs.json"),
            JsonSerializer.Serialize(payload, MergeJsonOptions),
            Encoding.UTF8);
    }

    private static void WriteMergePublishSidecar(
        string outputPath,
        string description,
        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> group)
    {
        var payload = new
        {
            description,
            caption = description,
            source = "merge_publish",
            created_at = DateTimeOffset.Now.ToString("O"),
            inputs = BuildMergePublishInputs(group)
        };
        File.WriteAllText(
            Path.ChangeExtension(outputPath, ".publish.json"),
            JsonSerializer.Serialize(payload, MergeJsonOptions),
            Encoding.UTF8);
    }

    private static IReadOnlyList<string> ResolveMaterialPublishStateKeys(
        WeixinMaterialPublishPage.PublishVideoItem publishItem)
    {
        var keys = new List<int> { publishItem.EpisodeIndex };
        var inputsPath = Path.ChangeExtension(publishItem.VideoPath, ".inputs.json");
        if (File.Exists(inputsPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(inputsPath, Encoding.UTF8));
                if (document.RootElement.TryGetProperty("inputs", out var inputs) &&
                    inputs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in inputs.EnumerateArray())
                    {
                        if (item.TryGetProperty("episode_index", out var episodeIndex) &&
                            episodeIndex.ValueKind == JsonValueKind.Number &&
                            episodeIndex.TryGetInt32(out var parsed) &&
                            parsed > 0)
                        {
                            keys.Add(parsed);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        return keys
            .Where(key => key > 0)
            .Distinct()
            .Select(key => key.ToString(CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static async Task<MergeProcessResult> RunMergeProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var startInfo = new global::System.Diagnostics.ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(fileName) ? "ffmpeg" : fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = global::System.Diagnostics.Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"无法启动进程：{startInfo.FileName}");
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new MergeProcessResult(process.ExitCode, stdout, stderr);
    }

    private static string ResolveMergeFfmpegBinary(WeixinVideoPublishOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.FfmpegPath) &&
            !string.Equals(options.FfmpegPath.Trim(), "ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            return options.FfmpegPath.Trim();
        }

        return BundledToolResolver.TryResolveBinary("ffmpeg") ?? "ffmpeg";
    }

    private static string ResolveMergeFfprobeBinary(WeixinVideoPublishOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.FfprobePath) &&
            !string.Equals(options.FfprobePath.Trim(), "ffprobe", StringComparison.OrdinalIgnoreCase))
        {
            return options.FfprobePath.Trim();
        }

        return BundledToolResolver.TryResolveBinary("ffprobe") ?? "ffprobe";
    }

    private static string BuildConcatFileLine(string path)
    {
        var text = Path.GetFullPath(path)
            .Replace("\\", "/", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        return $"file '{text}'";
    }

    private static int EnsureEvenDimension(int value, int fallback)
    {
        var resolved = value <= 0 ? fallback : value;
        if (resolved % 2 != 0)
        {
            resolved++;
        }

        return Math.Max(2, resolved);
    }

    private static JsonElement GetPropertyOrDefault(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? value : default;

    private static int ParseJsonInt(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
        {
            return value;
        }

        return int.TryParse(GetJsonString(element), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static double ParseJsonDouble(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
        {
            return value;
        }

        return double.TryParse(GetJsonString(element), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0.0;
    }

    private static string? GetJsonString(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };

    private static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0.0;
        }

        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            Math.Abs(denominator) > double.Epsilon)
        {
            return numerator / denominator;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0.0;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);
        }

        var result = builder.ToString().Trim(' ', '.');
        return result.Length <= 80 ? result : result[..80].Trim(' ', '.');
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryKillProcess(global::System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
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

    private static IReadOnlyDictionary<string, MaterialPublishStateEntry> UpsertMaterialPublishEntries(
        IReadOnlyDictionary<string, MaterialPublishStateEntry> source,
        IReadOnlyList<string> keys,
        MaterialPublishStateEntry value)
    {
        IReadOnlyDictionary<string, MaterialPublishStateEntry> result = source;
        foreach (var key in keys.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            result = WeixinMaterialPublishStateService.UpsertEntry(result, key, value);
        }

        return result;
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

    private sealed record MergePublishInput(
        [property: JsonPropertyName("episode_index")] int EpisodeIndex,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("file_name")] string FileName,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("mtime_ticks")] long MtimeTicks);

    private sealed record MergePublishProbe(
        double DurationSeconds,
        int Width,
        int Height,
        double FrameRateFps,
        string AudioCodecName);

    private sealed record MergeProcessResult(
        int ExitCode,
        string Stdout,
        string Stderr);
}
