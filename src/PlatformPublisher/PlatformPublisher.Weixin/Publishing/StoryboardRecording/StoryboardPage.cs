using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ShortDrama.Core.Interfaces;

namespace PlatformPublisher.Weixin.Publishing;

internal static partial class StoryboardPage
{
    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(StoryboardPage).Assembly.GetManifestResourceStream(
            "PlatformPublisher.Weixin.Publishing.StoryboardRecording.StoryboardPage.html")
            ?? throw new InvalidOperationException("分镜录屏模板缺失。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
    private static readonly (string Selector, string File)[] Overview =
        [("assets", "素材资产图.png"), ("canvas", "节点画布图.png")];

    public static async Task WriteAsync(string path, string title, int episode, string source,
        IReadOnlyList<StoryboardFrame> frames, double segmentLength, string preview, StoryboardTemplate template,
        StoryboardAnalysis? ai, CancellationToken ct)
    {
        var encoded = new List<string>(frames.Count);
        foreach (var frame in frames)
            encoded.Add(Convert.ToBase64String(await File.ReadAllBytesAsync(frame.Path, ct).ConfigureAwait(false)));
        var threeView = template == StoryboardTemplate.ThreeView;
        var shotCards = frames.Select((frame, index) =>
        {
            var image = $"<img src=\"data:image/jpeg;base64,{encoded[index]}\" alt=\"镜头 {index + 1}\">";
            var caption = FormattableString.Invariant($"<strong>镜头 {index + 1:00}</strong><span>{frame.Seconds:F2} 秒</span>");
            var prompt = ai is null ? "" : $"<p class=\"prompt\">{Encode(ai.Prompts[index])}</p>";
            if (!threeView)
                return $"<figure>{image}<figcaption>{caption}</figcaption>{prompt}</figure>";
            var start = frame.Seconds - segmentLength / 4;
            return FormattableString.Invariant(
                $"<tr><td>{image}</td><td>{caption}{prompt}</td><td><strong>{start:F2}–{start + segmentLength:F2} 秒</strong><button type=\"button\" data-offset=\"{index * segmentLength:F2}\" data-end=\"{(index + 1) * segmentLength:F2}\">预览第 {index + 1} 段</button></td></tr>");
        });
        var note = ai is null ? "镜头按剧集时间排列" : "火山方舟根据本集关键帧反推";
        var storyboard = threeView
            ? $"<table class=\"split-view\" aria-label=\"三视图分镜\"><thead><tr><th scope=\"col\">素材关键帧</th><th scope=\"col\">{(ai is null ? "分镜记录" : "AI 分镜提示词")}</th><th scope=\"col\">视频切片</th></tr></thead><tbody>{string.Concat(shotCards)}</tbody></table>"
            : $"<p>{note}</p><div class=\"shots\">{string.Concat(shotCards)}</div>";
        var assets = ai is null
            ? string.Concat(encoded.Select((image, index) =>
                $"<figure><img src=\"data:image/jpeg;base64,{image}\" alt=\"镜头 {index + 1}\"><figcaption>镜头 {index + 1}</figcaption></figure>"))
            : string.Concat(ai.Assets.Select(asset =>
            {
                var body = asset.Image is { } name
                    ? $"<img src=\"{Uri.EscapeDataString(name)}\" alt=\"{Encode(asset.Name)}\">"
                    : "<div class=\"asset-empty\">抽帧未确认可用参考</div>";
                return $"<figure>{body}<figcaption><strong>{Encode(asset.Label)}</strong><span>{Encode(asset.Name)}</span></figcaption></figure>";
            }));
        var nodes = new List<(string Label, string Description)> { ("源视频", $"第{episode}集"), ("关键帧", $"{frames.Count} 张视频截图") };
        if (ai is not null)
            nodes.Add(("火山方舟", $"{ai.Prompts.Length} 条提示词 · {ai.Assets.Count(asset => asset.Image is not null)} 张资产图"));
        nodes.Add(("拼接预览", $"{frames.Count} 段视频切片"));
        var values = new Dictionary<string, string>
        {
            ["Title"] = Encode(title), ["Episode"] = episode.ToString(), ["Filename"] = Encode(Path.GetFileName(source)),
            ["Template"] = threeView ? "three-view" : "comprehensive", ["AiClass"] = ai is null ? "" : " with-ai",
            ["AssetNote"] = ai is null ? "本集视频关键帧" : "本集火山方舟生成的资产", ["AssetCards"] = assets,
            ["Graph"] = string.Join("<div class=\"wire\" aria-hidden=\"true\"></div>",
                nodes.Select(node => $"<div class=\"node\"><strong>{node.Label}</strong><span>{node.Description}</span></div>")),
            ["Storyboard"] = storyboard,
            ["Video"] = Convert.ToBase64String(await File.ReadAllBytesAsync(preview, ct).ConfigureAwait(false)),
        };
        await File.WriteAllTextAsync(path, Placeholder().Replace(Template.Value, match => values[match.Groups[1].Value]), ct)
            .ConfigureAwait(false);
    }

    public static async Task<string> CaptureAsync(IWeixinBrowserRuntimeService runtime, IReadOnlyDictionary<int, string> pages,
        IReadOnlyList<int> recordingEpisodes, string stage, IProgress<string>? progress, CancellationToken ct)
    {
        var status = await runtime.InspectAsync(ct).ConfigureAwait(false);
        if (!status.IsReady)
            throw new InvalidOperationException(status.Message);
        runtime.ConfigureEnvironment(status);
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        { Headless = true, ExecutablePath = status.BrowserExecutablePath, Timeout = 15000 }).ConfigureAwait(false);

        var screenshots = await browser.NewContextAsync(Viewport()).ConfigureAwait(false);
        try
        {
            var page = await screenshots.NewPageAsync().ConfigureAwait(false);
            page.SetDefaultTimeout(8000);
            var first = true;
            foreach (var (episode, path) in pages)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"第{episode}集 · 正在截取分镜截图");
                await OpenAsync(page, path, "", ct).ConfigureAwait(false);
                if (first)
                {
                    foreach (var (view, file) in Overview)
                        await ScreenshotAsync(page, view, stage, file, ct).ConfigureAwait(false);
                    first = false;
                }
                await ScreenshotAsync(page, "storyboard", stage, $"第{episode}集分镜图.png", ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await screenshots.CloseAsync().ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        var options = Viewport();
        options.RecordVideoDir = stage;
        options.RecordVideoSize = new RecordVideoSize { Width = 1280, Height = 720 };
        var recording = await browser.NewContextAsync(options).ConfigureAwait(false);
        IVideo? video;
        try
        {
            var page = await recording.NewPageAsync().ConfigureAwait(false);
            page.SetDefaultTimeout(8000);
            var single = recordingEpisodes.Count == 1;
            foreach (var episode in recordingEpisodes)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"第{episode}集 · 正在录制拼接预览");
                await OpenAsync(page, pages[episode], single ? "" : "#preview", ct).ConfigureAwait(false);
                if (single)
                {
                    foreach (var view in new[] { "assets", "canvas", "storyboard" })
                    {
                        await page.Locator($"[data-view='{view}']").ClickAsync().WaitAsync(ct).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
                    }
                    await page.Locator("#play").ClickAsync().WaitAsync(ct).ConfigureAwait(false);
                }
                await page.WaitForFunctionAsync("document.querySelector('#preview-video').currentTime > 0", null,
                    new PageWaitForFunctionOptions { Timeout = 5000 }).WaitAsync(ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(single ? 10 : 13), ct).ConfigureAwait(false);
            }
            video = page.Video;
        }
        finally
        {
            await recording.CloseAsync().ConfigureAwait(false);
        }
        return await (video ?? throw new InvalidOperationException("浏览器未生成分镜录屏文件。")).PathAsync().ConfigureAwait(false);
    }

    private static BrowserNewContextOptions Viewport() =>
        new() { ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }, DeviceScaleFactor = 1 };

    private static async Task OpenAsync(IPage page, string path, string fragment, CancellationToken ct)
    {
        await page.GotoAsync(new Uri(path).AbsoluteUri + fragment, new PageGotoOptions { WaitUntil = WaitUntilState.Load })
            .WaitAsync(ct).ConfigureAwait(false);
        await page.EvaluateAsync("document.fonts.ready.then(() => true)").WaitAsync(ct).ConfigureAwait(false);
    }

    private static async Task ScreenshotAsync(IPage page, string view, string stage, string file, CancellationToken ct)
    {
        await page.Locator($"[data-view='{view}']").ClickAsync().WaitAsync(ct).ConfigureAwait(false);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(stage, file) }).WaitAsync(ct).ConfigureAwait(false);
    }

    private static string Encode(string input) => WebUtility.HtmlEncode(input);

    [GeneratedRegex("@@([A-Za-z]+)@@")]
    private static partial Regex Placeholder();
}
