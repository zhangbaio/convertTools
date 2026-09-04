using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;
using ShortDrama.Core.Interfaces;
using ShortDrama.Infrastructure.Automation.Weixin;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;

namespace PlatformPublisher.Weixin.Publishing;

public sealed class WeixinMaterialDownloadService
{
    private const string Origin = "https://channels.weixin.qq.com";
    private const string DownloadRootName = "系统高光下载";
    private const string DesktopUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    private readonly IWeixinAutomationConfigLoader _configLoader;
    private readonly WeixinBrowserRuntimeService _browserRuntime;
    private readonly WeixinHomePage _homePage;

    public WeixinMaterialDownloadService(IWeixinAutomationConfigLoader configLoader,
        WeixinBrowserRuntimeService browserRuntime, WeixinHomePage homePage)
    {
        _configLoader = configLoader;
        _browserRuntime = browserRuntime;
        _homePage = homePage;
    }

    public Task<MaterialDownloadResult> DownloadSystemHighlightsAsync(MaterialDownloadRequest request,
        IProgress<string>? progress, CancellationToken cancellationToken) =>
        RunAsync(request, progress, DownloadSystemHighlightsCoreAsync, cancellationToken);

    public Task<MaterialDownloadResult> DownloadByQueriesAsync(MaterialDownloadRequest request,
        IProgress<string>? progress, CancellationToken cancellationToken) =>
        RunAsync(request, progress, DownloadByQueriesCoreAsync, cancellationToken);

    private async Task<MaterialDownloadResult> RunAsync(MaterialDownloadRequest request, IProgress<string>? progress,
        Func<IPage, MaterialDownloadRequest, IProgress<string>?, CancellationToken, Task<MaterialDownloadResult>> action,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(request.WorkspaceDirectory))
            throw new DirectoryNotFoundException($"素材工作目录不存在：{request.WorkspaceDirectory}");
        if (request.Values.Count == 0) throw new InvalidOperationException("请至少填写一项查询内容。");

        var config = await _configLoader.LoadAsync(null, request.WorkspaceDirectory, cancellationToken);
        var runtime = await _browserRuntime.InspectAsync(cancellationToken);
        if (!runtime.IsReady) throw new InvalidOperationException(runtime.Message);
        _browserRuntime.ConfigureEnvironment(runtime);
        using var playwright = await _browserRuntime.CreatePlaywrightAsync(runtime, cancellationToken);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = runtime.BrowserExecutablePath,
            Headless = false,
            Args = ["--disable-blink-features=AutomationControlled", "--no-sandbox", "--start-maximized"],
        });
        var contextOptions = new BrowserNewContextOptions
        {
            ViewportSize = ViewportSize.NoViewport,
            UserAgent = config.Browser.UserAgent,
        };
        var authStatePath = !string.IsNullOrWhiteSpace(request.AuthStatePath) && File.Exists(request.AuthStatePath)
            ? request.AuthStatePath
            : config.AuthFilePath;
        if (!string.IsNullOrWhiteSpace(authStatePath) && File.Exists(authStatePath))
            contextOptions.StorageStatePath = authStatePath;
        await using var context = await browser.NewContextAsync(contextOptions);
        var page = await context.NewPageAsync();
        await page.GotoAsync(Origin, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        if (!await _homePage.IsLoggedInAsync(page, cancellationToken))
            throw new InvalidOperationException("当前登录态未登录视频号助手，请先登录后再下载素材。");

        var result = await action(page, request, progress, cancellationToken);
        if (!string.IsNullOrWhiteSpace(authStatePath))
            await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = authStatePath });
        return result;
    }

    private static async Task<MaterialDownloadResult> DownloadSystemHighlightsCoreAsync(IPage page,
        MaterialDownloadRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var root = Directory.CreateDirectory(Path.Combine(request.WorkspaceDirectory, DownloadRootName)).FullName;
        var downloaded = new List<DownloadedMaterial>();
        foreach (var (title, titleIndex) in request.Values.Select((value, index) => (value, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在读取系统高光：{title}（{titleIndex + 1}/{request.Values.Count}）");
            var captured = new ConcurrentQueue<string>();
            void Handler(object? _, IResponse response)
            {
                if (!response.Url.Contains("get-drama-highlight-video-list", StringComparison.OrdinalIgnoreCase)) return;
                _ = CaptureAsync(response, captured);
            }
            page.Response += Handler;
            try
            {
                await OpenSeriesDetailAsync(page, title, cancellationToken);
                await Task.Delay(1800, cancellationToken);
                var items = ParseArray(captured, "data", "highlightVideoList").Take(Math.Clamp(request.Limit, 1, 100)).ToArray();
                if (items.Length == 0)
                {
                    progress?.Report($"{title}：未获取到系统高光，可能尚未生成。");
                    continue;
                }
                var directory = Directory.CreateDirectory(Path.Combine(root, SafeName(title))).FullName;
                for (var index = 0; index < items.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = items[index];
                    var media = FirstMedia(item);
                    var videoUrl = Text(media, "fullUrl", "url");
                    if (string.IsNullOrWhiteSpace(videoUrl)) continue;
                    var type = Text(item, "typeName") ?? "高光";
                    var stem = $"{SafeName(title)}-{SafeName(type)}-{index + 1:D2}";
                    var videoPath = Path.Combine(directory, stem + ".mp4");
                    var coverUrl = Text(media, "fullCoverUrl", "coverUrl", "shareCoverUrl") ?? Text(item, "coverImgUrl");
                    var coverPath = string.IsNullOrWhiteSpace(coverUrl) ? null : Path.Combine(directory, stem + ".cover.jpg");
                    await DownloadFileAsync(videoUrl, videoPath, cancellationToken);
                    if (coverPath is not null) await TryDownloadAsync(coverUrl!, coverPath, cancellationToken);
                    var description = Text(Desc(item), "description") ?? string.Empty;
                    var shortTitle = Text(Desc(item), "shortTitle") ?? string.Empty;
                    await WriteSidecarAsync(videoPath, description, shortTitle, "system_highlight",
                        Text(Nested(item, "draft"), "objectId", "exportId") ?? Text(item, "exportId") ?? string.Empty,
                        cancellationToken);
                    downloaded.Add(new DownloadedMaterial(title, videoPath, coverPath, description, shortTitle));
                    progress?.Report($"{title}：已下载 {index + 1}/{items.Length} 条系统高光。");
                }
            }
            finally
            {
                page.Response -= Handler;
            }
        }
        return new MaterialDownloadResult(root, downloaded);
    }

    private static async Task<MaterialDownloadResult> DownloadByQueriesCoreAsync(IPage page,
        MaterialDownloadRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var root = Directory.CreateDirectory(Path.Combine(request.WorkspaceDirectory, DownloadRootName)).FullName;
        var captured = new ConcurrentQueue<string>();
        void Handler(object? _, IResponse response)
        {
            if (!response.Url.Contains("/post/post_list", StringComparison.OrdinalIgnoreCase) &&
                !response.Url.Contains("/post/post_search_user_page", StringComparison.OrdinalIgnoreCase)) return;
            _ = CaptureAsync(response, captured);
        }
        page.Response += Handler;
        var downloaded = new List<DownloadedMaterial>();
        try
        {
            await page.GotoAsync(Origin + "/platform/post/list",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 40_000 });
            foreach (var (query, queryIndex) in request.Values.Select((value, index) => (value, index)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (captured.TryDequeue(out _)) { }
                progress?.Report($"正在按标签或描述搜索：{query}（{queryIndex + 1}/{request.Values.Count}）");
                var input = page.Locator("input[placeholder*='搜索视频']:visible, input[placeholder*='搜索']:visible, .weui-desktop-search-bar input:visible, input[type='search']:visible").First;
                await input.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20_000 });
                await input.FillAsync(string.Empty);
                await input.FillAsync(query);
                await input.PressAsync("Enter");
                await Task.Delay(1600, cancellationToken);
                var items = ParseArray(captured, "data", "list")
                    .Where(item => (Text(Desc(item), "description") ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(Math.Clamp(request.Limit, 1, 50)).ToArray();
                foreach (var item in items)
                {
                    var media = FirstVideoMedia(item);
                    var videoUrl = Text(media, "fullUrl", "url");
                    if (string.IsNullOrWhiteSpace(videoUrl)) continue;
                    var desc = Desc(item);
                    var description = Text(desc, "description") ?? string.Empty;
                    var shortTitle = Text(desc, "shortTitle") ?? string.Empty;
                    var title = Text(Nested(desc, "component"), "title") ?? "未分类素材";
                    var directory = Directory.CreateDirectory(Path.Combine(root, SafeName(title))).FullName;
                    var objectId = Text(item, "objectId", "exportId") ?? Guid.NewGuid().ToString("N");
                    var stem = $"{SafeName(title)}-{SafeName(objectId)[..Math.Min(8, SafeName(objectId).Length)]}";
                    var videoPath = UniquePath(directory, stem, ".mp4");
                    var coverUrl = Text(media, "fullCoverUrl", "coverUrl", "thumbUrl");
                    var coverPath = string.IsNullOrWhiteSpace(coverUrl) ? null : Path.ChangeExtension(videoPath, ".cover.jpg");
                    await DownloadFileAsync(videoUrl, videoPath, cancellationToken);
                    if (coverPath is not null) await TryDownloadAsync(coverUrl!, coverPath, cancellationToken);
                    await WriteSidecarAsync(videoPath, description, shortTitle, "material_video_download", objectId, cancellationToken);
                    downloaded.Add(new DownloadedMaterial(title, videoPath, coverPath, description, shortTitle));
                    progress?.Report($"{query}：已下载 {Path.GetFileName(videoPath)}");
                }
            }
        }
        finally
        {
            page.Response -= Handler;
        }
        return new MaterialDownloadResult(root, downloaded);
    }

    private static async Task OpenSeriesDetailAsync(IPage page, string title, CancellationToken cancellationToken)
    {
        await page.GotoAsync(Origin + "/platform/playlet",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 40_000 });
        var search = page.Locator("input[placeholder*='剧集']:visible, input[placeholder*='名称']:visible, input[placeholder*='搜索']:visible, .weui-desktop-search-bar input:visible, input[type='search']:visible").First;
        await search.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20_000 });
        await search.FillAsync(string.Empty);
        await search.FillAsync(title);
        await Task.Delay(900, cancellationToken);
        var rows = page.Locator("tr.ant-table-row, table tbody tr, .weui-desktop-table tbody tr, [role='row']");
        ILocator? matched = null;
        for (var index = 0; index < Math.Min(await rows.CountAsync(), 50); index++)
        {
            var row = rows.Nth(index);
            if (await VisibleAsync(row) && (await row.InnerTextAsync()).Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                matched = row;
                break;
            }
        }
        if (matched is null) throw new InvalidOperationException($"剧集管理中搜索不到「{title}」。");
        var actions = matched.Locator("a.playlet-action-item, a, button");
        for (var index = 0; index < await actions.CountAsync(); index++)
        {
            var action = actions.Nth(index);
            var text = await action.InnerTextAsync();
            if (await VisibleAsync(action) && (text.Contains("管理") || text.Contains("详情")))
            {
                await action.ClickAsync();
                await Task.Delay(1200, cancellationToken);
                return;
            }
        }
        throw new InvalidOperationException($"未找到「{title}」的管理入口。");
    }

    private static async Task CaptureAsync(IResponse response, ConcurrentQueue<string> captured)
    {
        try { captured.Enqueue(await response.TextAsync()); } catch { }
    }

    private static IEnumerable<JsonElement> ParseArray(ConcurrentQueue<string> payloads, params string[] path)
    {
        foreach (var payload in payloads.Reverse())
        {
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(payload);
                var value = document.RootElement;
                foreach (var segment in path)
                {
                    if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) break;
                }
                if (value.ValueKind == JsonValueKind.Array)
                    foreach (var item in value.EnumerateArray()) yield return item.Clone();
            }
            finally { document?.Dispose(); }
        }
    }

    internal static IReadOnlyList<JsonElement> ParsePayloadItems(string payload, params string[] path)
    {
        var values = new ConcurrentQueue<string>();
        values.Enqueue(payload);
        return ParseArray(values, path).ToArray();
    }

    private static JsonElement Desc(JsonElement item) => Nested(Nested(item, "draft"), "desc") is { ValueKind: JsonValueKind.Object } nested
        ? nested
        : Nested(item, "desc");
    private static JsonElement FirstMedia(JsonElement item) => FirstArrayItem(Nested(Desc(item), "media"));
    private static JsonElement FirstVideoMedia(JsonElement item)
    {
        var media = Nested(Desc(item), "media");
        if (media.ValueKind != JsonValueKind.Array) return default;
        return media.EnumerateArray().FirstOrDefault(value =>
            Number(value, "mediaType") == 4 && !string.IsNullOrWhiteSpace(Text(value, "fullUrl", "url"))).Clone();
    }
    private static JsonElement FirstArrayItem(JsonElement value) => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0
        ? value[0].Clone()
        : default;
    private static JsonElement Nested(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var nested)
        ? nested.Clone()
        : default;
    private static string? Text(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (value.TryGetProperty(name, out var item) && item.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return item.ToString();
        return null;
    }
    private static int Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var item) && item.TryGetInt32(out var number) ? number : 0;

    private static async Task DownloadFileAsync(string url, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length > 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".part";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(Origin + "/");
        request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(temporary))
            await input.CopyToAsync(output, cancellationToken);
        if (new FileInfo(temporary).Length == 0) throw new InvalidOperationException("下载文件为空。");
        File.Move(temporary, destination, true);
    }

    private static async Task TryDownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        try { await DownloadFileAsync(url, destination, cancellationToken); } catch { }
    }

    private static async Task WriteSidecarAsync(string videoPath, string description, string shortTitle,
        string source, string objectId, CancellationToken cancellationToken)
    {
        var sidecar = Path.Combine(Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath) + ".publish.json");
        await File.WriteAllTextAsync(sidecar, JsonSerializer.Serialize(new
        {
            version = 1,
            description,
            caption = description,
            tags = description.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(value => value.StartsWith('#')).Select(value => value.TrimStart('#')).ToArray(),
            shortTitle,
            title = string.IsNullOrWhiteSpace(shortTitle) ? Path.GetFileNameWithoutExtension(videoPath) : shortTitle,
            source,
            object_id = objectId,
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    private static async Task<bool> VisibleAsync(ILocator locator)
    {
        try { return await locator.IsVisibleAsync(); } catch { return false; }
    }

    internal static string SafeName(string value)
    {
        var cleaned = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "素材" : cleaned[..Math.Min(80, cleaned.Length)];
    }

    private static string UniquePath(string directory, string stem, string extension)
    {
        var path = Path.Combine(directory, stem + extension);
        for (var index = 2; File.Exists(path); index++) path = Path.Combine(directory, $"{stem}-{index:D2}{extension}");
        return path;
    }
}

public sealed record MaterialDownloadRequest(string AccountId, string WorkspaceDirectory,
    IReadOnlyList<string> Values, int Limit = 10, string AuthStatePath = "");
public sealed record DownloadedMaterial(string Title, string VideoPath, string? CoverPath,
    string Description, string ShortTitle);
public sealed record MaterialDownloadResult(string DownloadRoot, IReadOnlyList<DownloadedMaterial> Items)
{
    public int DownloadedCount => Items.Count;
}
