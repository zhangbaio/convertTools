using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Adx.Storage;

namespace PlatformPublisher.Adx.Automation;

public sealed class AdxAutomationService
{
    private const string MaterialRoute = "#/pages_plugs/adx/playlet/material-by-playlet";
    private readonly AdxSettingsStore _settingsStore;
    private readonly AdxCredentialStore _credentialStore;
    private readonly AdxSessionStore _sessionStore;
    private readonly AdxBatchStore _batchStore;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private AdxLoginStatus? _runtimeStatus;

    public event EventHandler<AdxLoginStatus>? LoginStatusChanged;
    public bool IsBusy => _operationGate.CurrentCount == 0;

    public AdxAutomationService(AdxSettingsStore settingsStore, AdxCredentialStore credentialStore,
        AdxSessionStore sessionStore, AdxBatchStore batchStore)
    {
        _settingsStore = settingsStore;
        _credentialStore = credentialStore;
        _sessionStore = sessionStore;
        _batchStore = batchStore;
    }

    public AdxSettings LoadSettings() => _settingsStore.Load();

    public void SaveSettings(AdxSettings settings)
    {
        var previousIdentity = _settingsStore.Load().Identity;
        var normalized = settings.Normalize();
        _settingsStore.Save(normalized);
        if (!string.Equals(previousIdentity, normalized.Identity, StringComparison.Ordinal))
            _sessionStore.Clear();
        _runtimeStatus = null;
        NotifyStatus();
    }

    public void SavePassword(string password)
    {
        _credentialStore.Save(password);
        _sessionStore.Clear();
        _runtimeStatus = null;
        NotifyStatus();
    }

    public AdxLoginStatus GetLoginStatus()
    {
        var settings = LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.Username) || !_credentialStore.IsConfigured)
            return new(AdxLoginState.NotConfigured, settings.Username, _credentialStore.IsConfigured, Message: "请配置 ADX 账号和密码。");
        if (_runtimeStatus is not null) return _runtimeStatus;
        var session = _sessionStore.Load(settings.Identity);
        return session is null
            ? new(AdxLoginState.LoggedOut, settings.Username, true, Message: "配置已保存，请登录 ADX。")
            : new(AdxLoginState.LoggedIn, settings.Username, true, session.Value.LastVerifiedAt, "ADX 已登录。");
    }

    public async Task<AdxLoginStatus> LoginAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        var settings = LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.Username) || !_credentialStore.IsConfigured)
        {
            _operationGate.Release();
            throw new InvalidOperationException("请先保存 ADX 账号和密码。");
        }
        SetStatus(new(AdxLoginState.Checking, settings.Username, true, Message: "正在验证 ADX 登录状态…"));
        try
        {
            await WithPageAsync(settings, async (page, context) =>
            {
                await OpenMaterialPageAsync(page, settings, _credentialStore.Load());
                _sessionStore.Save(settings.Identity, await context.StorageStateAsync());
                return true;
            }, cancellationToken);
            _runtimeStatus = null;
            var status = GetLoginStatus();
            LoginStatusChanged?.Invoke(this, status);
            return status;
        }
        catch (Exception ex)
        {
            _sessionStore.Clear();
            SetStatus(new(AdxLoginState.Failed, settings.Username, true, Message: ex.Message));
            throw;
        }
        finally { _operationGate.Release(); }
    }

    public void Logout()
    {
        if (IsBusy) throw new InvalidOperationException("ADX 正在执行任务，请等待任务结束后退出登录。");
        _sessionStore.Clear();
        _runtimeStatus = null;
        NotifyStatus();
    }

    public async Task<AdxQueryResult> QueryAsync(AdxQueryRequest request, IProgress<AdxProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
        ValidateRequest(request.OriginalTitle, request.WorkflowDirectory);
        var settings = LoadSettings();
        EnsureConfigured(settings);
        return await WithPageAsync(settings, async (page, context) =>
        {
            progress?.Report(new("query", $"正在 ADX 按原剧名查询：{request.OriginalTitle}"));
            await QueryPageAsync(page, settings, request.OriginalTitle);
            var cards = page.Locator(".adx-material-card");
            var total = await cards.CountAsync();
            var count = Math.Min(Math.Clamp(request.Limit, 1, 200), total);
            var baseDirectory = Path.Combine(Path.GetFullPath(request.WorkflowDirectory), "materials", "adx");
            var candidates = new List<AdxCandidate>();
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var card = cards.Nth(index);
                var text = await card.InnerTextAsync();
                var id = Regex.Match(text, @"ID\s*[:：]\s*(\d+)", RegexOptions.IgnoreCase).Groups[1].Value;
                if (string.IsNullOrEmpty(id)) id = $"rank-{index + 1}";
                var cover = await card.Locator("img").First.GetAttributeAsync("src");
                var fileName = $"{SafeName(request.SeriesName, "视频号剧集")}-TOP{index + 1:000}-{id}.mp4";
                candidates.Add(new(id, index + 1, cover, Metric(text, "曝光"), Metric(text, "播放"), Metric(text, "点赞"), FindDownloadedFile(baseDirectory, fileName) is not null));
            }
            _sessionStore.Save(settings.Identity, await context.StorageStateAsync());
            progress?.Report(new("query", $"ADX 查询完成：返回 {candidates.Count}/{total} 条素材。", candidates.Count, total));
            return new AdxQueryResult(Guid.NewGuid().ToString("N"), total, candidates);
        }, cancellationToken);
        }
        finally { _operationGate.Release(); }
    }

    public async Task<AdxDownloadResult> DownloadAsync(AdxDownloadRequest request,
        IProgress<AdxProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
        ValidateRequest(request.OriginalTitle, request.WorkflowDirectory);
        if (request.MaterialIds.Count == 0) throw new ArgumentException("请至少选择一条 ADX 素材。", nameof(request));
        var settings = LoadSettings();
        EnsureConfigured(settings);
        return await WithPageAsync(settings, async (page, context) =>
        {
            await QueryPageAsync(page, settings, request.OriginalTitle);
            var cards = page.Locator(".adx-material-card");
            var available = await cards.CountAsync();
            var requested = request.MaterialIds.ToHashSet(StringComparer.Ordinal);
            var redownload = request.RedownloadMaterialIds?.ToHashSet(StringComparer.Ordinal) ?? [];
            var candidates = new List<AdxCandidate>();
            for (var index = 0; index < available; index++)
            {
                var card = cards.Nth(index);
                var cardText = await card.InnerTextAsync();
                var id = Regex.Match(cardText, @"ID\s*[:：]\s*(\d+)", RegexOptions.IgnoreCase).Groups[1].Value;
                if (string.IsNullOrEmpty(id)) id = $"rank-{index + 1}";
                if (!requested.Contains(id)) continue;
                candidates.Add(new(id, index + 1, await card.Locator("img").First.GetAttributeAsync("src"),
                    Metric(cardText, "曝光"), Metric(cardText, "播放"), Metric(cardText, "点赞"), false));
            }
            if (candidates.Count == 0) throw new InvalidOperationException("ADX 查询结果中未找到所选素材，请重新查询。");

            var baseDirectory = Path.Combine(Path.GetFullPath(request.WorkflowDirectory), "materials", "adx");
            var downloadDirectory = CreateRunDirectory(baseDirectory);
            var records = new List<AdxBatchItem>();
            var active = new List<Task>();
            var sync = new object();
            var completed = 0;
            progress?.Report(new("download", $"已创建本次素材目录：{downloadDirectory}", 0, candidates.Count));

            foreach (var candidate in candidates.OrderBy(item => item.Rank))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stem = $"{SafeName(request.SeriesName, "视频号剧集")}-TOP{candidate.Rank:000}-{candidate.MaterialId}";
                var existing = FindDownloadedFile(baseDirectory, stem + ".mp4");
                if (existing is not null && !redownload.Contains(candidate.MaterialId))
                {
                    var existingCover = Path.Combine(Path.GetDirectoryName(existing)!, stem + ".cover.jpg");
                    records.Add(new AdxBatchItem { MaterialId = candidate.MaterialId, Rank = candidate.Rank,
                        VideoPath = existing, CoverPath = File.Exists(existingCover) ? existingCover : null, Status = "skipped" });
                    completed++;
                    progress?.Report(new("download", $"TOP {candidate.Rank} 已存在，跳过下载。", completed, candidates.Count));
                    continue;
                }
                while (active.Count >= settings.DownloadConcurrency)
                {
                    var finished = await Task.WhenAny(active);
                    active.Remove(finished);
                    await finished;
                }

                var card = cards.Nth(candidate.Rank - 1);
                await SetCheckedAsync(card, true);
                IDownload download;
                try
                {
                    download = await page.RunAndWaitForDownloadAsync(
                        () => page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("逐个下载") }).ClickAsync(),
                        new() { Timeout = 120_000 });
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    completed++;
                    progress?.Report(new("download", $"TOP {candidate.Rank} 启动下载失败：{ex.Message}", completed, candidates.Count, true));
                    continue;
                }
                finally
                {
                    try { await SetCheckedAsync(card, false); }
                    catch { }
                }

                var task = SaveDownloadSafeAsync(download, candidate, request, context, downloadDirectory, stem,
                    item => { lock (sync) records.Add(item); },
                    () =>
                    {
                        var current = Interlocked.Increment(ref completed);
                        progress?.Report(new("download", $"ADX 下载进度：{current}/{candidates.Count}", current, candidates.Count));
                    }, progress, candidates.Count, cancellationToken);
                active.Add(task);
            }
            await Task.WhenAll(active);
            cancellationToken.ThrowIfCancellationRequested();
            records = records.OrderBy(item => item.Rank).ToList();
            if (records.Count == 0) throw new InvalidOperationException("所选 ADX 素材均下载失败。");
            var now = DateTimeOffset.UtcNow;
            var manifest = new AdxBatchManifest
            {
                BatchId = Path.GetFileName(downloadDirectory), WorkflowDir = Path.GetFullPath(request.WorkflowDirectory),
                SeriesName = request.SeriesName, NewTitle = request.SeriesName, OriginalTitle = request.OriginalTitle,
                CreatedAt = now, UpdatedAt = now, Items = records,
                ManifestPath = Path.Combine(downloadDirectory, AdxBatchStore.ManifestFileName),
            };
            _batchStore.Write(manifest);
            _sessionStore.Save(settings.Identity, await context.StorageStateAsync());
            var message = $"ADX 素材准备完成：{records.Count} 条，目录 {downloadDirectory}";
            progress?.Report(new("completed", message, records.Count, records.Count));
            return new AdxDownloadResult(downloadDirectory, records, message);
        }, cancellationToken);
        }
        finally { _operationGate.Release(); }
    }

    private void SetStatus(AdxLoginStatus status)
    {
        _runtimeStatus = status;
        LoginStatusChanged?.Invoke(this, status);
    }

    private void NotifyStatus() => LoginStatusChanged?.Invoke(this, GetLoginStatus());

    private static async Task SaveDownloadAsync(IDownload download, AdxCandidate candidate, AdxDownloadRequest request,
        IBrowserContext context, string directory, string stem, Action<AdxBatchItem> add,
        Action completed, CancellationToken cancellationToken)
    {
        var videoPath = Path.Combine(directory, stem + ".mp4");
        var temporary = videoPath + ".part";
        var coverPath = Path.Combine(directory, stem + ".cover.jpg");
        try
        {
            await download.SaveAsAsync(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new InvalidOperationException("下载文件为空。");
            File.Move(temporary, videoPath);
            string? resolvedCover = null;
            if (request.ReplaceCover && request.CoverMode == AdxCoverMode.Project && File.Exists(request.ProjectCoverPath))
            {
                File.Copy(request.ProjectCoverPath!, coverPath, true);
                resolvedCover = coverPath;
            }
            else if (request.ReplaceCover && request.CoverMode == AdxCoverMode.Adx && Uri.TryCreate(candidate.CoverUrl, UriKind.Absolute, out _))
            {
                var response = await context.APIRequest.GetAsync(candidate.CoverUrl!, new() { Timeout = 60_000 });
                if (response.Ok) { await File.WriteAllBytesAsync(coverPath, await response.BodyAsync(), cancellationToken); resolvedCover = coverPath; }
            }
            var sidecar = new { source = "adx", materialId = candidate.MaterialId, rank = candidate.Rank,
                originalTitle = request.OriginalTitle, newTitle = request.SeriesName, downloadedAt = DateTimeOffset.UtcNow,
                coverPath = resolvedCover ?? string.Empty };
            await File.WriteAllTextAsync(Path.Combine(directory, stem + ".publish.json"),
                JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            add(new AdxBatchItem { MaterialId = candidate.MaterialId, Rank = candidate.Rank,
                VideoPath = videoPath, CoverPath = resolvedCover, Status = "downloaded" });
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            completed();
        }
    }

    private static async Task SaveDownloadSafeAsync(IDownload download, AdxCandidate candidate,
        AdxDownloadRequest request, IBrowserContext context, string directory, string stem,
        Action<AdxBatchItem> add, Action completed, IProgress<AdxProgress>? progress, int total,
        CancellationToken cancellationToken)
    {
        try { await SaveDownloadAsync(download, candidate, request, context, directory, stem, add, completed, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { progress?.Report(new("download", $"TOP {candidate.Rank} 下载失败：{ex.Message}", 0, total, true)); }
    }

    private static async Task SetCheckedAsync(ILocator card, bool value)
    {
        var checkbox = card.Locator("input[type=checkbox]").First;
        if (await checkbox.CountAsync() == 0) throw new InvalidOperationException("ADX 素材卡片中未找到复选框。");
        if (await checkbox.IsCheckedAsync() == value) return;
        await checkbox.SetCheckedAsync(value, new() { Force = true });
    }

    private static string CreateRunDirectory(string baseDirectory)
    {
        Directory.CreateDirectory(baseDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        var directory = Path.Combine(baseDirectory, timestamp);
        for (var suffix = 2; Directory.Exists(directory); suffix++) directory = Path.Combine(baseDirectory, $"{timestamp}_{suffix:00}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private async Task<T> WithPageAsync<T>(AdxSettings settings, Func<IPage, IBrowserContext, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchBrowserAsync(playwright, settings.Headless);
        var session = _sessionStore.Load(settings.Identity);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            AcceptDownloads = true,
            ViewportSize = settings.Headless ? new ViewportSize { Width = 1440, Height = 960 } : ViewportSize.NoViewport,
            StorageState = session?.StorageState,
        });
        using var registration = cancellationToken.Register(() => _ = context.CloseAsync());
        try { return await action(await context.NewPageAsync(), context); }
        catch (AdxAuthenticationException ex)
        {
            _sessionStore.Clear();
            SetStatus(new(AdxLoginState.Expired, settings.Username, _credentialStore.IsConfigured,
                Message: "ADX 登录状态已失效，请在系统设置中重新登录。"));
            throw new InvalidOperationException("ADX 登录状态已失效，请在系统设置中重新登录。", ex);
        }
        catch (PlaywrightException ex) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("ADX 操作已取消。", ex, cancellationToken); }
    }

    private static async Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright, bool headless)
    {
        var options = new BrowserTypeLaunchOptions { Headless = headless, Args = ["--disable-blink-features=AutomationControlled", "--start-maximized"] };
        foreach (var channel in new[] { "chrome", "msedge", string.Empty })
        {
            try { options.Channel = string.IsNullOrEmpty(channel) ? null : channel; return await playwright.Chromium.LaunchAsync(options); }
            catch when (!string.IsNullOrEmpty(channel)) { }
        }
        throw new InvalidOperationException("无法启动 ADX 浏览器，请安装 Google Chrome 或 Microsoft Edge。");
    }

    private async Task OpenMaterialPageAsync(IPage page, AdxSettings settings, string password)
    {
        var root = settings.BaseUrl.TrimEnd('/') + "/";
        var hasSession = _sessionStore.Load(settings.Identity) is not null;
        await page.GotoAsync(hasSession ? root + MaterialRoute : root, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        var passwordInput = page.Locator("input[type=password]").First;
        var materialInput = page.GetByPlaceholder("剧名（需全匹配）", new() { Exact = true });
        var state = await WaitForViewAsync(passwordInput, materialInput);
        if (state == "unknown") throw new AdxAuthenticationException("无法识别 ADX 登录页面。");
        if (state == "login")
        {
            await page.Locator("input[type=text]").First.FillAsync(settings.Username);
            await passwordInput.FillAsync(password);
            await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("登录") }).ClickAsync();
            await passwordInput.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 20_000 });
        }
        if (!page.Url.Contains("material-by-playlet", StringComparison.OrdinalIgnoreCase))
            await page.GotoAsync(root + MaterialRoute, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        try { await materialInput.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 }); }
        catch (PlaywrightException ex) { throw new AdxAuthenticationException("ADX 登录未通过或登录状态已失效。", ex); }
    }

    private static async Task<string> WaitForViewAsync(ILocator passwordInput, ILocator materialInput)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await materialInput.IsVisibleAsync()) return "materials";
            if (await passwordInput.IsVisibleAsync()) return "login";
            await Task.Delay(150);
        }
        return "unknown";
    }

    private async Task QueryPageAsync(IPage page, AdxSettings settings, string originalTitle)
    {
        await OpenMaterialPageAsync(page, settings, _credentialStore.Load());
        var input = page.GetByPlaceholder("剧名（需全匹配）", new() { Exact = true });
        await input.FillAsync(originalTitle);
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("查询") }).ClickAsync();
        try { await page.Locator(".adx-material-card").First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 }); }
        catch
        {
            var body = await page.Locator("body").InnerTextAsync();
            if (body.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("ADX 增值接口返回 403，当前 AppKey 未开通短剧增值服务。");
            if (body.Contains("对应的短剧不存在") || body.Contains("请输入短剧ID或剧名")) throw new InvalidOperationException($"ADX 未找到精确剧名“{originalTitle}”，请确认原剧名。");
            throw new InvalidOperationException("ADX 素材查询未返回结果。");
        }
    }

    internal static long Metric(string text, string label)
    {
        var match = Regex.Match(text, Regex.Escape(label) + @"\s*([0-9.]+)\s*([wW万]?)");
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return 0;
        return checked((long)Math.Round(value * (match.Groups[2].Success && match.Groups[2].Value.Length > 0 ? 10_000 : 1)));
    }

    internal static string SafeName(string value, string fallback)
    {
        var result = Regex.Replace(value, "[\\\\/:*?\"<>|\\r\\n\\t]+", "-");
        result = Regex.Replace(result, @"\s+", " ").Trim();
        return string.IsNullOrEmpty(result) ? fallback : result[..Math.Min(80, result.Length)];
    }

    internal static string? FindDownloadedFile(string baseDirectory, string fileName)
    {
        if (!Directory.Exists(baseDirectory)) return null;
        var direct = Path.Combine(baseDirectory, fileName);
        if (File.Exists(direct) && new FileInfo(direct).Length > 0) return direct;
        return Directory.EnumerateDirectories(baseDirectory).Select(directory => Path.Combine(directory, fileName)).FirstOrDefault(path => File.Exists(path) && new FileInfo(path).Length > 0);
    }

    private static void ValidateRequest(string originalTitle, string workflowDirectory)
    {
        if (string.IsNullOrWhiteSpace(originalTitle)) throw new ArgumentException("项目缺少原剧名，无法查询 ADX。");
        if (!Directory.Exists(workflowDirectory)) throw new DirectoryNotFoundException($"工作目录不存在：{workflowDirectory}");
    }

    private void EnsureConfigured(AdxSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Username) || !_credentialStore.IsConfigured)
            throw new InvalidOperationException("请先保存 ADX 账号和密码并完成登录。");
        if (_sessionStore.Load(settings.Identity) is null)
            throw new InvalidOperationException("ADX 尚未登录，请先在系统设置的“ADX素材服务”中登录。");
    }

    private sealed class AdxAuthenticationException : Exception
    {
        public AdxAuthenticationException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
