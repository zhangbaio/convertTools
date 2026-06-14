using Microsoft.Playwright;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Notifications;
using ShortDrama.Infrastructure.Automation.Weixin;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;

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

        progress?.Report("寰俊鍓ч泦涓婁紶锛氭鏌ユ祻瑙堝櫒杩愯鏃?..");
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
        Directory.CreateDirectory(config.Browser.UserDataDirectory);

        using var playwright = await _browserRuntimeService.CreatePlaywrightAsync(cancellationToken);
        await using var context = await playwright.Chromium.LaunchPersistentContextAsync(
            config.Browser.UserDataDirectory,
            new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = config.Browser.Headless,
            SlowMo = config.Browser.SlowMoMs,
            UserAgent = config.Browser.UserAgent,
            ViewportSize = config.Browser.Headless
                ? new ViewportSize
                {
                    Width = config.Browser.Viewport.Width,
                    Height = config.Browser.Viewport.Height
                }
                : null,
            Args =
            [
                "--disable-blink-features=AutomationControlled",
                "--no-sandbox",
                "--start-maximized"
            ]
        });

        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        progress?.Report($"寰俊涓婁紶锛氭鍦ㄦ墦寮€鍚庡彴 {config.BaseUrl}");

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
            progress?.Report($"寰俊涓婁紶锛氬悗鍙伴椤靛凡鎵撳紑锛屾埅鍥惧凡淇濆瓨鍒?{homeScreenshotPath}");
        }
        else
        {
            progress?.Report("寰俊涓婁紶锛氬悗鍙伴椤靛凡鎵撳紑銆");
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
            progress?.Report("寰俊涓婁紶锛氭湭妫€娴嬪埌鏈夋晥鐧诲綍鎬侊紝璇峰湪娴忚鍣ㄤ腑鎵爜鐧诲綍銆");
            var loginDecision = await WaitForLoginCompletionAsync(
                request,
                page,
                progress,
                cancellationToken);
            if (string.Equals(loginDecision, "stop", StringComparison.Ordinal))
            {
                return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "寰俊涓婁紶宸插仠姝紝鍙户缁繍琛屻€");
            }

            await context.StorageStateAsync(new BrowserContextStorageStateOptions
            {
                Path = config.AuthFilePath
            });
            progress?.Report($"寰俊涓婁紶锛氱櫥褰曟€佸凡鏇存柊鍒?{config.AuthFilePath}");
        }
        else
        {
            progress?.Report("寰俊涓婁紶锛氬凡澶嶇敤鏈夋晥鐧诲綍鎬併€");
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
            progress?.Report($"寰俊涓婁紶锛氬凡淇濆瓨鐧诲綍鎬?{config.AuthFilePath}");
            return publishResult;
        }

        progress?.Report($"微信剧集上传：正在导航到 {config.Navigation.Section} -> {config.Navigation.Item} -> {config.Navigation.EntryButton}");
        var resolution = await ExecuteSeriesStageAsync(
            request,
            config,
            "series-navigate",
            "瀵艰埅鍒板墽闆嗕笂浼犻〉闈",
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
            "绛夊緟绗竴椤靛氨缁",
            () => _seriesSubmissionPage.WaitForReadyAsync(page, config.FirstPage, cancellationToken),
            progress,
            cancellationToken);
        if (TryBuildSeriesInterruptionResult(resolution, request, resolvedConfigPath, out interruptionResult))
        {
            return interruptionResult;
        }

        progress?.Report("寰俊鍓ч泦涓婁紶锛氬紑濮嬭嚜鍔ㄥ～鍐欑涓€椤佃〃鍗?..");
        resolution = await ExecuteSeriesStageAsync(
            request,
            config,
            "first-page-actions",
            "濉啓绗竴椤佃〃鍗",
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
        progress?.Report($"寰俊鍓ч泦涓婁紶锛氱涓€椤靛凡濉啓瀹屾垚锛屾埅鍥惧凡淇濆瓨鍒?{firstPageScreenshotPath}");

        resolution = await ExecuteSeriesStageAsync(
            request,
            config,
            "second-page-entry",
            "杩涘叆绗簩椤",
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
            "涓婁紶绗簩椤佃棰",
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
            "杩涘叆鎻愬椤",
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
            return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "寰俊涓婁紶宸插仠姝紝鍙户缁繍琛屻€");
        }

        resolution = await ExecuteSeriesStageAsync(
            request,
            config,
            "submit-final",
            "鎵ц鏈€缁堟彁瀹",
            () => _seriesSubmissionPage.ExecuteFinalSubmitAsync(page, config.Submit, progress, cancellationToken),
            progress,
            cancellationToken);
        if (TryBuildSeriesInterruptionResult(resolution, request, resolvedConfigPath, out interruptionResult))
        {
            return interruptionResult;
        }
        if (config.Submit.Enabled)
        {
            progress?.Report("寰俊鍓ч泦涓婁紶锛氬凡鎵ц鏈€缁堟彁浜ゃ€");
        }

        await context.StorageStateAsync(new BrowserContextStorageStateOptions
        {
            Path = config.AuthFilePath
        });
        progress?.Report($"寰俊鍓ч泦涓婁紶锛氬凡淇濆瓨鐧诲綍鎬?{config.AuthFilePath}");

        if (config.Browser.KeepOpenSeconds > 0)
        {
            progress?.Report($"寰俊涓婁紶锛氭寜閰嶇疆淇濈暀娴忚鍣?{config.Browser.KeepOpenSeconds} 绉掋€");
            await Task.Delay(TimeSpan.FromSeconds(config.Browser.KeepOpenSeconds), cancellationToken);
        }

        return new WeixinUploadResult(
            Ok: true,
            ProjectDir: request.ProjectDir,
            ConfigPath: resolvedConfigPath,
            Message: config.Submit.Enabled
                ? "C# 寰俊鍓ч泦涓婁紶宸叉墽琛屽埌鏈€缁堟彁浜ゃ€"
                : "C# 寰俊鍓ч泦涓婁紶宸叉墽琛屽埌鎻愬椤碉紝绛夊緟浜哄伐鏈€缁堢‘璁ゃ€");
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
            return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "褰撳墠椤圭洰宸茬鐢ㄥ井淇＄礌鏉愪笂浼犮€");
        }

        var projectInfo = await _projectInfoParser.ParseAsync(request.ProjectDir, cancellationToken);
        var allPublishItems = WeixinMaterialPublishPage.ResolvePublishVideoItems(request.ProjectDir, config.VideoPublish);
        if (allPublishItems.Count == 0)
        {
            return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "褰撳墠椤圭洰鏈壘鍒板彲鍙戣〃鐨勭礌鏉愯棰戙€");
        }

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
                        ? "寰俊绱犳潗涓婁紶锛氬凡寮€鍚柊涓€杞噸澶嶅彂甯冿紝鏈疆浼氶噸鏂板彂甯冪洰鏍囩礌鏉愶紱涓柇鍚庝細缁窇鍓╀綑瑙嗛銆"
                        : "寰俊绱犳潗涓婁紶锛氱户缁笂涓€杞湭瀹屾垚鐨勯噸澶嶅彂甯冿紝璺宠繃鏈疆宸叉垚鍔熻棰戙€");
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

            progress?.Report($"寰俊绱犳潗涓婁紶锛氬綋鍓嶇瓥鐣?{runStrategy} 涓嬫病鏈夊彲鎵ц鐨勮棰戙€");
            return new WeixinUploadResult(true, request.ProjectDir, resolvedConfigPath, "褰撳墠绛栫暐涓嬫病鏈夊彲鎵ц鐨勭礌鏉愯棰戙€");
        }

        var description = WeixinMaterialPublishPage.BuildPublishDescription(projectInfo, config.VideoPublish);
        var shortTitle = WeixinMaterialPublishPage.BuildShortTitle(projectInfo, config.VideoPublish);
        progress?.Report($"寰俊绱犳潗涓婁紶锛氬噯澶囧彂琛?{selectedVideos.Count} 鏉¤棰戙€傜瓥鐣?{runStrategy}銆");

        for (var index = 0; index < selectedVideos.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var publishItem = selectedVideos[index];
            var videoPath = publishItem.VideoPath;
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
                await _materialPublishPage.ChooseOptionsAsync(page, config.VideoPublish, projectInfo.Title, progress, cancellationToken);
                if (config.VideoPublish.FillShortTitle)
                {
                    await _materialPublishPage.FillShortTitleAsync(page, shortTitle, progress, cancellationToken);
                }

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
                    return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "寰俊绱犳潗涓婁紶宸插仠姝紝鍙户缁繍琛屻€");
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
                        new MaterialPublishStateEntry("interrupted", videoPath, DateTimeOffset.Now, "宸插彇娑"))
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

                return new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, $"寰俊绱犳潗涓婁紶澶辫触锛氱{publishItem.EpisodeIndex}闆?{ex.Message}");
            }
        }

        if (config.Browser.KeepOpenSeconds > 0)
        {
            progress?.Report($"寰俊涓婁紶锛氭寜閰嶇疆淇濈暀娴忚鍣?{config.Browser.KeepOpenSeconds} 绉掋€");
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
            Message: $"C# 寰俊绱犳潗涓婁紶宸插畬鎴愶紝鍏卞鐞?{selectedVideos.Count} 鏉¤棰戙€");
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
                    progress?.Report("寰俊鍓ч泦涓婁紶锛氬凡纭鐧诲綍鎴愬姛銆");
                    return "resume";
                }

                await Task.Delay(1000, cancellationToken);
            }

            throw new TimeoutException("寰俊鍓ч泦涓婁紶锛氱瓑寰呮壂鐮佺櫥褰曡秴鏃躲€");
        }

        while (!await _homePage.IsLoggedInAsync(page, cancellationToken))
        {
            var decision = await RequestDecisionAsync(
                request,
                stage: "login-required",
                message: "鏈娴嬪埌鍙鐢ㄧ櫥褰曟€併€傝鍦ㄦ墦寮€鐨勬祻瑙堝櫒涓壂鐮佺櫥褰曡棰戝彿鍚庡彴锛屽畬鎴愬悗鐐瑰嚮缁х画鎵ц锛涗篃鍙互鍏堝垏鍒颁汉宸ュ鐞嗘ā寮忋€",
                options: null,
                progress,
                cancellationToken);
            if (string.Equals(decision, "stop", StringComparison.Ordinal))
            {
                progress?.Report("寰俊鍓ч泦涓婁紶锛氱櫥褰曢樁娈靛凡鍋滄銆");
                return decision;
            }
        }

        progress?.Report("寰俊鍓ч泦涓婁紶锛氬凡纭鐧诲綍鎴愬姛銆");
        return "resume";
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
            progress?.Report("寰俊涓婁紶锛氬凡鍙戦€佺櫥褰曚簩缁寸爜鎻愰啋銆");
        }
        catch (Exception ex)
        {
            progress?.Report($"寰俊涓婁紶锛氱櫥褰曚簩缁寸爜鎻愰啋鍙戦€佸け璐? {ex.Message}");
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
            progress?.Report("寰俊鍓ч泦涓婁紶锛氭彁瀹￠〉宸插氨缁紝鑷姩缁х画鍚庣画娴佺▼銆");
            return "resume";
        }

        return await RequestDecisionAsync(
            request,
            stage: "submit-ready",
            message: config.Submit.Enabled
                ? "鎻愬椤靛凡灏辩华銆傜‘璁ら〉闈㈠唴瀹规棤璇悗鐐瑰嚮缁х画鎵ц锛岀郴缁熷皢鑷姩鐐瑰嚮鏈€缁堟彁瀹℃寜閽紱涔熷彲浠ュ仠姝㈠綋鍓嶄笂浼犳楠ゃ€"
                : "鎻愬椤靛凡灏辩华銆傝鎵嬪姩妫€鏌ュ苟鍐冲畾鏄惁鏈€缁堟彁瀹★紱澶勭悊瀹屾垚鍚庣偣鍑荤户缁墽琛岋紝鎴栧仠姝㈠綋鍓嶄笂浼犳楠ゃ€",
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
            progress?.Report("寰俊鍓ч泦涓婁紶锛氬凡鍒囨崲鍒颁汉宸ュ鐞嗘ā寮忥紝绛夊緟缁х画鎴栧仠姝€");
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
                    Message: "娴忚鍣ㄥ凡浜ょ敱浣犳墜鍔ㄥ鐞嗐€傚畬鎴愬綋鍓嶉〉闈㈡搷浣滃悗鐐瑰嚮缁х画鎵ц锛屾垨鍋滄褰撳墠涓婁紶姝ラ銆",
                    Options: manualOptions),
                cancellationToken);
        }

        progress?.Report($"寰俊鍓ч泦涓婁紶锛氭敹鍒版搷浣滃喅绛?{decision}");
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
                result = new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "寰俊鍓ч泦涓婁紶宸茶烦杩囧綋鍓嶉」鐩€");
                return true;
            case SeriesStageResolution.Stop:
                result = new WeixinUploadResult(false, request.ProjectDir, resolvedConfigPath, "寰俊鍓ч泦涓婁紶宸插仠姝紝鍙户缁繍琛屻€");
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
