using Microsoft.Playwright;
using ShortDrama.Infrastructure.Automation.Weixin;

namespace TikTokPublisher.Core.Services.ProjectImages.FableCut;

internal sealed class FableCutScreenshotRenderer : IAsyncDisposable
{
    private const int ViewportWidth = 1920;
    private const int ViewportHeight = 1080;
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;

    private FableCutScreenshotRenderer(IPlaywright playwright, IBrowser browser)
    {
        _playwright = playwright;
        _browser = browser;
    }

    public static async Task<FableCutScreenshotRenderer> CreateAsync(
        Action<string>? log,
        CancellationToken ct)
    {
        var runtime = new WeixinBrowserRuntimeService();
        // Prefer system Edge because drama sources are commonly H.264/H.265 and the
        // Playwright Chromium bundle may be built without proprietary media codecs.
        var status = await runtime.InspectInstalledEdgeAsync(ct).ConfigureAwait(false);
        if (!status.IsReady || string.IsNullOrWhiteSpace(status.BrowserExecutablePath))
            status = await runtime.InspectAsync(ct).ConfigureAwait(false);
        if (!status.IsReady || string.IsNullOrWhiteSpace(status.BrowserExecutablePath))
            throw new InvalidOperationException("FableCut 工程图无法启动浏览器：" + status.Message);

        var playwright = await runtime.CreatePlaywrightAsync(status, ct).ConfigureAwait(false);
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                ExecutablePath = status.BrowserExecutablePath,
                Headless = true,
                Args = ["--no-sandbox", "--disable-background-networking"],
            }).WaitAsync(ct).ConfigureAwait(false);
            log?.Invoke($"FableCut/浏览器：{Path.GetFileName(status.BrowserExecutablePath)}");
            return new FableCutScreenshotRenderer(playwright, browser);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    public async Task CaptureAsync(
        string url,
        string outputPath,
        double seekRatio,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var baseUri = new Uri(url, UriKind.Absolute);
        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
            DeviceScaleFactor = 1,
            ServiceWorkers = ServiceWorkerPolicy.Block,
        }).WaitAsync(ct).ConfigureAwait(false);

        await context.RouteAsync("**/*", async route =>
        {
            if (Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var requestUri) &&
                requestUri.IsLoopback && requestUri.Port == baseUri.Port)
            {
                var isEpisodeMedia = string.Equals(
                    requestUri.AbsolutePath,
                    "/media/episode",
                    StringComparison.Ordinal);
                if (isEpisodeMedia && route.Request.ResourceType is "fetch" or "xhr")
                {
                    await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
                }
                else if (isEpisodeMedia &&
                         string.Equals(route.Request.ResourceType, "media", StringComparison.OrdinalIgnoreCase))
                {
                    var headers = route.Request.Headers.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase);
                    headers["X-FableCut-Media-Element"] = "1";
                    await route.ContinueAsync(new RouteContinueOptions { Headers = headers }).ConfigureAwait(false);
                }
                else
                {
                    await route.ContinueAsync().ConfigureAwait(false);
                }
            }
            else
            {
                await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await context.AddInitScriptAsync(
            "localStorage.setItem('fablecut-track-size','m');" +
            "localStorage.setItem('fablecut-timeline-h','520');").ConfigureAwait(false);

        var page = await context.NewPageAsync().WaitAsync(ct).ConfigureAwait(false);
        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 45_000,
        }).WaitAsync(ct).ConfigureAwait(false);

        await page.WaitForFunctionAsync(
            "() => document.querySelector('#projectName')?.textContent.includes('connected')",
            null,
            new PageWaitForFunctionOptions { Timeout = 20_000 }).WaitAsync(ct).ConfigureAwait(false);
        await page.WaitForFunctionAsync(
            "() => typeof projDur === 'function' && typeof setZoom === 'function' && " +
            "typeof setTime === 'function' && typeof seekMediaWhilePaused === 'function' && " +
            "typeof drawFrame === 'function' && typeof els === 'object'",
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000 }).WaitAsync(ct).ConfigureAwait(false);

        await page.AddStyleTagAsync(new PageAddStyleTagOptions
        {
            Content = """
                .bin-item { min-height: 34px !important; padding: 4px 6px !important; }
                .bin-item img, .bin-item video { width: 42px !important; height: 28px !important; }
                .bin-item .meta, .bin-item .sub { line-height: 12px !important; }
                #projectName { font-size: 0 !important; }
                #projectName::after { content: "🟢 connected"; font-size: 12px; }
                """,
        }).WaitAsync(ct).ConfigureAwait(false);

        await page.EvaluateAsync(
            """
            () => {
                const width = els.timelineScroll.clientWidth || 800;
                setZoom(width / Math.max(projDur(), 1));
                els.timelineScroll.scrollLeft = 0;
            }
            """).WaitAsync(ct).ConfigureAwait(false);

        var mediaStatusJson = await page.EvaluateAsync<string>(
            """
            async ratio => {
                const target = Math.max(0, projDur() * ratio);
                setTime(target);
                const active = visibleClipsAt(target).filter(c => c.kind === 'video');
                const waitForMedia = (el, predicate, timeoutMs) => new Promise(resolve => {
                    if (predicate()) { resolve(); return; }
                    let settled = false;
                    const done = () => {
                        if (settled) return;
                        settled = true;
                        clearTimeout(timer);
                        el.removeEventListener('loadedmetadata', check);
                        el.removeEventListener('loadeddata', check);
                        el.removeEventListener('canplay', check);
                        el.removeEventListener('seeked', check);
                        el.removeEventListener('error', check);
                        resolve();
                    };
                    const check = () => {
                        if (predicate() || !!el.error) done();
                    };
                    const timer = setTimeout(done, timeoutMs);
                    for (const event of ['loadedmetadata', 'loadeddata', 'canplay', 'seeked', 'error'])
                        el.addEventListener(event, check);
                });
                const statuses = await Promise.all(active.map(async clip => {
                    const el = getClipEl(clip);
                    if (!el) return { id: clip.id, ready: false, error: 'missing-element' };
                    el.muted = true;
                    await waitForMedia(el, () => el.readyState >= 1 || !!el.error, 10000);
                    const mediaTime = mediaTimeAt(clip, target);
                    try { el.currentTime = mediaTime; } catch { }
                    await waitForMedia(
                        el,
                        () => (!!el.videoWidth && el.readyState >= 2 && Math.abs(el.currentTime - mediaTime) <= 0.08) || !!el.error,
                        10000);
                    return {
                        id: clip.id,
                        ready: !!el.videoWidth && !!el.videoHeight && el.readyState >= 2,
                        readyState: el.readyState,
                        currentTime: el.currentTime,
                        width: el.videoWidth,
                        height: el.videoHeight,
                        error: el.error?.code || 0,
                    };
                }));
                // The loopback server intentionally rejects fetch(src).arrayBuffer()
                // so the same episode is not decoded once per synthetic mediaId.
                // Supply small deterministic peak arrays for timeline evidence instead.
                for (const media of project.media || []) {
                    const duration = Math.max(0.5, Number(media.duration) || 0.5);
                    const count = Math.min(24000, Math.max(16, Math.ceil(duration * WAVE_PEAKS_PER_SEC)));
                    const peaks = new Float32Array(count);
                    let seed = 2166136261;
                    for (const ch of String(media.id || media.name || 'media')) {
                        seed ^= ch.charCodeAt(0);
                        seed = Math.imul(seed, 16777619) >>> 0;
                    }
                    for (let index = 0; index < count; index++) {
                        seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
                        const noise = (seed & 0xffff) / 0xffff;
                        const pulse = Math.abs(Math.sin(index * 0.19 + (seed & 31)));
                        peaks[index] = Math.min(0.92, 0.08 + noise * 0.28 + pulse * 0.34);
                    }
                    runtime.wavePeaks.set(media.id, { channels: [peaks], max: peaks });
                }
                state.dirtyTimeline = true;
                seekMediaWhilePaused();
                drawFrame();
                return JSON.stringify({
                    target,
                    readyCount: statuses.filter(item => item.ready).length,
                    statuses,
                });
            }
            """,
            Math.Clamp(seekRatio, 0.01, 0.99)).WaitAsync(ct).ConfigureAwait(false);

        using (var mediaStatus = System.Text.Json.JsonDocument.Parse(mediaStatusJson))
        {
            var readyCount = mediaStatus.RootElement.GetProperty("readyCount").GetInt32();
            if (readyCount == 0)
            {
                throw new InvalidOperationException(
                    "FableCut 无法解码节目监视器中的源视频。请确认视频编码受 Edge/Chromium 支持。" +
                    $" 媒体状态：{mediaStatusJson}");
            }
        }

        await Task.Delay(250, ct).ConfigureAwait(false);
        await page.EvaluateAsync("() => drawFrame()").WaitAsync(ct).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = outputPath,
            Type = ScreenshotType.Png,
            FullPage = false,
        }).WaitAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _browser.CloseAsync().ConfigureAwait(false); }
        finally { _playwright.Dispose(); }
    }
}
