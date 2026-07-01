using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

// ─────────────────────────────────────────────────────────────────────────────
// P1 可行性验证：视频号上传能否用 .NET 经 CDP 驱动（含 CEF 内嵌模型的连接方式）
//
// 背景：现有 Python 生产代码用 Playwright `set_input_files` 上传视频号视频，
//   真实文件输入选择器 =
//     .weui-desktop-form__control-group_label-r:has(.weui-desktop-form__label:has-text("选取视频")) input[type=file]
//   等待完成靠轮询页面文本「已上传成功 n/m」。
//
// 本 spike 用 Playwright.NET 复刻该路径，验证三条 .NET→CDP 能力：
//   A. Playwright Locator.SetInputFilesAsync（高层 API，最终落到 CDP DOM.setFileInputFiles）
//   B. 原生 CDP DOM.setFileInputFiles（CEF 必须支持的底层原语）
//   C. ConnectOverCDP 连接一个「外部已启动的浏览器」再 setInputFiles
//      —— 这正是内嵌 CEF 的驱动模型：CEF 开 remote-debugging，.NET 连上去自动化
//
// probe 模式：无头、离线、无需登录，对本地 weui 结构页面验证 A/B/C。（本机即可跑）
// channels 模式：有头、持久化 profile，跑真实视频号上传（需扫码登录+真实视频，真机跑）。
// ─────────────────────────────────────────────────────────────────────────────

const string VideoInputSelector =
    ".weui-desktop-form__control-group_label-r:has(.weui-desktop-form__label:has-text(\"选取视频\")) input[type=file]";

// 与真实视频号第二页同构的最小结构（用真实选择器即可命中 → 顺带验证选择器引擎）
const string LocalWeuiHtml = """
<!doctype html><html><head><meta charset="utf-8"><title>weui upload probe</title></head>
<body>
  <div class="weui-desktop-form__control-group_label-r">
    <label class="weui-desktop-form__label">选取视频</label>
    <input type="file" accept="video/*" />
  </div>
</body></html>
""";

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "probe";
string chromePath = Environment.GetEnvironmentVariable("SPIKE_CHROME")
    ?? @"C:\Users\PC\AppData\Local\ms-playwright\chromium-1169\chrome-win\chrome.exe";

int exitCode = mode switch
{
    "channels" => await RunChannelsAsync(args, chromePath),
    "real" => await RunRealAsync(args, chromePath),
    "diag" => await RunDiagAsync(args, chromePath),
    "dump" => await RunDumpAsync(args, chromePath),
    "cdpshot" => await RunCdpShotAsync(args),
    "fill" => await RunFillAsync(args, chromePath),
    "extra" => await RunExtraAsync(args, chromePath),
    _ => await RunProbeAsync(chromePath),
};
return exitCode;

// ─────────────────────────── PROBE（本机可跑） ───────────────────────────
async Task<int> RunProbeAsync(string chrome)
{
    Console.WriteLine("=== P1 CDP 可行性 probe（无头/离线/无需登录）===");
    Console.WriteLine($"Chromium: {chrome}");
    string sample = CreateSampleVideo();
    Console.WriteLine($"样本文件: {sample} ({new FileInfo(sample).Length} bytes)\n");

    var results = new List<(string name, bool ok, string detail)>();
    using var pw = await Playwright.CreateAsync();

    // ---- A: Playwright 高层 SetInputFilesAsync（用真实生产选择器）----
    try
    {
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = chrome });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync(LocalWeuiHtml);
        var input = page.Locator(VideoInputSelector);
        await input.SetInputFilesAsync(sample);
        string readback = await input.EvaluateAsync<string>(
            "el => el.files && el.files[0] ? (el.files[0].name + '|' + el.files[0].size) : ''");
        bool ok = readback.StartsWith(Path.GetFileName(sample) + "|") && !readback.EndsWith("|0");
        results.Add(("A. Playwright SetInputFilesAsync + 生产选择器", ok, readback));
        Console.WriteLine($"[A] readback = \"{readback}\" -> {(ok ? "PASS" : "FAIL")}");
    }
    catch (Exception ex) { results.Add(("A. Playwright SetInputFilesAsync", false, ex.Message)); Console.WriteLine($"[A] EXCEPTION {ex.Message}"); }

    // ---- B: 原生 CDP DOM.setFileInputFiles（CEF 必须支持的底层原语）----
    try
    {
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = chrome });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync(LocalWeuiHtml);
        var cdp = await page.Context.NewCDPSessionAsync(page);

        // 拿到 input 元素的 Runtime objectId，再用 DOM.setFileInputFiles 注入文件
        var eval = await cdp.SendAsync("Runtime.evaluate", new Dictionary<string, object>
        {
            ["expression"] = "document.querySelector('input[type=file]')",
        });
        string objectId = eval!.Value.GetProperty("result").GetProperty("objectId").GetString()!;
        await cdp.SendAsync("DOM.setFileInputFiles", new Dictionary<string, object>
        {
            ["files"] = new[] { sample },
            ["objectId"] = objectId,
        });
        string readback = await page.EvalOnSelectorAsync<string>("input[type=file]",
            "el => el.files && el.files[0] ? (el.files[0].name + '|' + el.files[0].size) : ''");
        bool ok = readback.StartsWith(Path.GetFileName(sample) + "|") && !readback.EndsWith("|0");
        results.Add(("B. 原生 CDP DOM.setFileInputFiles", ok, readback));
        Console.WriteLine($"[B] readback = \"{readback}\" -> {(ok ? "PASS" : "FAIL")}");
    }
    catch (Exception ex) { results.Add(("B. 原生 CDP DOM.setFileInputFiles", false, ex.Message)); Console.WriteLine($"[B] EXCEPTION {ex.Message}"); }

    // ---- C: ConnectOverCDP 到外部已启动浏览器再 setInputFiles（== CEF 内嵌驱动模型）----
    Process? external = null;
    try
    {
        string userDir = Path.Combine(Path.GetTempPath(), "cdp-spike-userdir-" + Guid.NewGuid().ToString("N")[..8]);
        int port = 9333;
        external = Process.Start(new ProcessStartInfo
        {
            FileName = chrome,
            Arguments = $"--headless=new --remote-debugging-port={port} --user-data-dir=\"{userDir}\" --no-first-run about:blank",
            UseShellExecute = false,
        });
        string? wsOrHttp = await WaitForCdpEndpointAsync(port, TimeSpan.FromSeconds(15));
        if (wsOrHttp is null) throw new Exception("CDP 端点未就绪");

        await using var browser = await pw.Chromium.ConnectOverCDPAsync($"http://localhost:{port}");
        var ctx = browser.Contexts.Count > 0 ? browser.Contexts[0] : await browser.NewContextAsync();
        var page = ctx.Pages.Count > 0 ? ctx.Pages[0] : await ctx.NewPageAsync();
        await page.SetContentAsync(LocalWeuiHtml);
        var input = page.Locator(VideoInputSelector);
        await input.SetInputFilesAsync(sample);
        string readback = await input.EvaluateAsync<string>(
            "el => el.files && el.files[0] ? (el.files[0].name + '|' + el.files[0].size) : ''");
        bool ok = readback.StartsWith(Path.GetFileName(sample) + "|") && !readback.EndsWith("|0");
        results.Add(("C. ConnectOverCDP(外部浏览器) + setInputFiles", ok, readback));
        Console.WriteLine($"[C] readback = \"{readback}\" -> {(ok ? "PASS" : "FAIL")}");
    }
    catch (Exception ex) { results.Add(("C. ConnectOverCDP + setInputFiles", false, ex.Message)); Console.WriteLine($"[C] EXCEPTION {ex.Message}"); }
    finally { try { external?.Kill(true); } catch { } }

    Console.WriteLine("\n=== 结论 ===");
    foreach (var (name, ok, detail) in results)
        Console.WriteLine($"  {(ok ? "✅" : "❌")} {name}  [{detail}]");
    bool allCore = results.Count >= 2 && results[0].ok && results[1].ok;
    Console.WriteLine($"\n核心原语(A 高层 + B 原生CDP) : {(allCore ? "可行 ✅" : "有问题 ❌")}");
    Console.WriteLine("→ A/B 通过即证明 .NET 能经 CDP 用 setInputFiles 上传文件（视频号上传的关键动作）。");
    Console.WriteLine("→ C 通过即证明「连接外部/内嵌浏览器 CDP 端点」驱动可行（CEF 内嵌走同一路径）。");
    return allCore ? 0 : 1;
}

// ─────────────────────────── CHANNELS（真机跑真实视频号）───────────────────────────
async Task<int> RunChannelsAsync(string[] a, string chrome)
{
    string video = a.Length > 1 ? a[1] : "";
    string userDir = a.Length > 2 ? a[2] : Path.Combine(Path.GetTempPath(), "channels-spike-profile");
    if (string.IsNullOrWhiteSpace(video) || !File.Exists(video))
    {
        Console.WriteLine("用法: ChannelsUploadCdpSpike channels <视频文件绝对路径> [用户数据目录]");
        Console.WriteLine("说明: 首次运行会打开视频号登录页，请扫码登录；登录态保存在用户数据目录，后续复用。");
        return 2;
    }
    Console.WriteLine("=== 真实视频号上传验证（有头/持久化 profile）===");
    Console.WriteLine($"视频: {video}\nprofile: {userDir}");

    using var pw = await Playwright.CreateAsync();
    // 持久化上下文 = 每账号一个独立 profile 目录（对应最终架构的「每账号独立会话」）
    var ctx = await pw.Chromium.LaunchPersistentContextAsync(userDir, new()
    {
        Headless = false,
        ExecutablePath = chrome,
        ViewportSize = ViewportSize.NoViewport,
    });
    var page = ctx.Pages.Count > 0 ? ctx.Pages[0] : await ctx.NewPageAsync();

    await page.GotoAsync("https://channels.weixin.qq.com/platform/login", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
    Console.WriteLine("→ 若未登录，请在弹出的浏览器里扫码登录，然后回到本终端按回车继续…");
    Console.ReadLine();

    // 进入发表页（视频号助手：发表视频）
    await page.GotoAsync("https://channels.weixin.qq.com/platform/post/create", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
    // 生产代码用 ready_text 判定第二页就绪
    try { await page.GetByText("请选择要上传的视频文件").First.WaitForAsync(new() { Timeout = 30000 }); }
    catch { Console.WriteLine("⚠️ 未检测到「请选择要上传的视频文件」，页面结构可能变化，仍尝试定位文件输入。"); }

    var input = page.Locator(VideoInputSelector);
    await input.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15000 });
    Console.WriteLine("→ 定位到视频文件输入，开始 SetInputFilesAsync…");
    await input.SetInputFilesAsync(video);

    // 复刻生产等待逻辑：轮询页面文本「已上传成功」/进度 n/m，出现错误文案则失败
    string[] success = { "已上传成功", "上传成功", "处理完成" };
    string[] error = { "上传失败", "未能上传", "上传异常", "不符合要求", "格式不支持", "超出限制" };
    var deadline = DateTime.UtcNow.AddMinutes(30);
    while (DateTime.UtcNow < deadline)
    {
        string body = await page.InnerTextAsync("body");
        foreach (var e in error) if (body.Contains(e)) { Console.WriteLine($"❌ 检测到上传错误文案: {e}"); return 1; }
        var m = System.Text.RegularExpressions.Regex.Match(body, @"已上传成功\s*(\d+)\s*/\s*(\d+)");
        if (m.Success) Console.WriteLine($"⏱ 进度 {m.Groups[1].Value}/{m.Groups[2].Value}");
        if (success.Any(s => body.Contains(s))) { Console.WriteLine("✅ 视频上传完成（检测到成功文案）。"); break; }
        await Task.Delay(3000);
    }

    Console.WriteLine("\n✅ 到此已证明：.NET(Playwright/CDP) 能登录态复用 + 定位视频号文件输入 + 上传视频并等待完成。");
    Console.WriteLine("⏹ 安全起见，本 spike 不点「确认提审/发表」。按回车关闭浏览器。");
    Console.ReadLine();
    await ctx.CloseAsync();
    return 0;
}

// ─────────────── REAL：有头 + 复用昭服登录态 + 真实视频上传（不提审）───────────────
async Task<int> RunRealAsync(string[] a, string chrome)
{
    string authFile = a.Length > 1 && a[1].Length > 0
        ? a[1]
        : @"C:\Users\PC\.weixin_channel_tool\profiles\profile-6\wx_auth_state.json"; // 昭服
    string video = a.Length > 2 && a[2].Length > 0
        ? a[2]
        : @"E:\video2\workflow\_槐下老翁修仙问长生\material-clip-output\百岁永生-第87集-剪辑.mp4";
    string shotDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
    Directory.CreateDirectory(shotDir);

    Console.WriteLine("=== 真实视频号上传（有头 / 复用昭服登录态 / 不提审）===");
    Console.WriteLine($"登录态: {authFile}\n视频:   {video}\n截图:   {shotDir}");
    if (!File.Exists(authFile)) { Console.WriteLine("❌ 登录态文件不存在"); return 2; }
    if (!File.Exists(video)) { Console.WriteLine("❌ 视频文件不存在"); return 2; }

    using var pw = await Playwright.CreateAsync();
    string[] launchArgs = { "--disable-blink-features=AutomationControlled", "--start-maximized" };
    IBrowser browser;
    try
    {
        // Edge 基于 Chromium 但含 H.264/AAC 专有编解码器 → 通过视频号「浏览器格式校验」
        // （Playwright 自带 Chromium 是开源版，缺专有编解码器，会误报"不支持此视频格式"）
        browser = await pw.Chromium.LaunchAsync(new() { Headless = false, Channel = "msedge", Args = launchArgs });
        Console.WriteLine("→ 浏览器：系统 Edge（含专有编解码器）");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Edge 启动失败（{ex.Message}），回退自带 Chromium（可能缺 H.264 编解码器）。");
        browser = await pw.Chromium.LaunchAsync(new() { Headless = false, ExecutablePath = chrome, Args = launchArgs });
    }
    var ctx = await browser.NewContextAsync(new()
    {
        StorageStatePath = authFile,             // 复用视频号登录态，免扫码
        UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        ViewportSize = ViewportSize.NoViewport,
    });
    var page = await ctx.NewPageAsync();

    Console.WriteLine("→ 打开发表页 platform/post/create …");
    await page.GotoAsync("https://channels.weixin.qq.com/platform/post/create",
        new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
    try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 20000 }); } catch { }
    await page.WaitForTimeoutAsync(6000);  // SPA 编辑器懒加载，需等其渲染出文件输入
    await page.ScreenshotAsync(new() { Path = Path.Combine(shotDir, "01-landing.png"), FullPage = true });
    Console.WriteLine($"   当前 URL: {page.Url}");

    if (page.Url.Contains("/login"))
    {
        Console.WriteLine("❌ 被重定向到登录页 → 昭服登录态已失效/未登录。已截图 01-landing.png。");
        await ctx.CloseAsync();
        return 1;
    }

    // 新版编辑器：页面唯一的 input[type=file][accept*=video]（display:none，可直接 setInputFiles）
    var input = page.Locator("input[type=file][accept*='video']").First;
    try { await input.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15000 }); }
    catch
    {
        input = page.Locator("input[type=file]").First;
        try { await input.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 5000 }); }
        catch
        {
            Console.WriteLine("❌ 未定位到视频文件输入。已截图 err-no-input.png。");
            await page.ScreenshotAsync(new() { Path = Path.Combine(shotDir, "err-no-input.png"), FullPage = true });
            await ctx.CloseAsync();
            return 1;
        }
    }

    Console.WriteLine("→ SetInputFilesAsync 上传视频 …");
    await input.SetInputFilesAsync(video);
    await page.WaitForTimeoutAsync(4000);
    await page.ScreenshotAsync(new() { Path = Path.Combine(shotDir, "02-after-setfiles.png"), FullPage = true });

    // 新版编辑器上传成功指示：进度/成功文案，或编辑器出现「删除/更换/封面/描述」等已入编辑态控件
    string[] success = { "已上传成功", "上传成功", "处理完成", "上传完成", "更换", "删除视频", "封面", "添加描述", "位置" };
    string[] error = { "上传失败", "未能上传", "上传异常", "不符合要求", "格式不支持", "超出限制", "视频时长", "大小超过" };
    var deadline = DateTime.UtcNow.AddMinutes(6);
    bool done = false; string last = "";
    while (DateTime.UtcNow < deadline)
    {
        string body;
        try { body = await page.InnerTextAsync("body"); } catch { body = ""; }
        foreach (var e in error)
            if (body.Contains(e))
            {
                Console.WriteLine($"❌ 检测到上传错误文案：{e}");
                await page.ScreenshotAsync(new() { Path = Path.Combine(shotDir, "03-error.png"), FullPage = true });
                await ctx.CloseAsync();
                return 1;
            }
        var m = System.Text.RegularExpressions.Regex.Match(body, @"已上传成功\s*(\d+)\s*/\s*(\d+)");
        string prog = m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : "";
        if (prog != last && prog.Length > 0) { Console.WriteLine($"⏱ 进度 {prog}"); last = prog; }
        if (success.Any(s => body.Contains(s))) { done = true; break; }
        await page.WaitForTimeoutAsync(3000);
    }

    await page.ScreenshotAsync(new() { Path = Path.Combine(shotDir, "03-final.png"), FullPage = true });
    Console.WriteLine(done
        ? "\n✅ 成功：检测到「已上传成功」——.NET(有头)复用昭服登录态并完成视频号视频上传。"
        : "\n⚠️ 超时未检测到成功文案（见 03-final.png 判断实际状态）。");
    Console.WriteLine("⏹ 未点「确认提审/发表」，仅到草稿上传态（安全可逆）。");
    await ctx.CloseAsync();
    return done ? 0 : 1;
}

// ─────────── CDPSHOT：连内嵌 WebView2 的 CDP 端口截图（验证 WebView2 渲染 + CDP 可驱动）───────────
async Task<int> RunCdpShotAsync(string[] a)
{
    int port = a.Length > 1 && int.TryParse(a[1], out var p) ? p : 9222;
    string outPng = Path.Combine(AppContext.BaseDirectory, "cdpshot.png");
    Console.WriteLine($"=== 连接内嵌 WebView2 的 CDP 端口 {port} 截图 ===");
    var ep = await WaitForCdpEndpointAsync(port, TimeSpan.FromSeconds(30));
    if (ep is null) { Console.WriteLine("❌ CDP 端点未就绪（WebView2 未起或未开 remote-debugging）"); return 1; }

    using var pw = await Playwright.CreateAsync();
    var browser = await pw.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}");
    IPage? page = null;
    for (int i = 0; i < 30; i++)
    {
        var ctx = browser.Contexts.FirstOrDefault();
        var pages = ctx?.Pages ?? new List<IPage>();
        page = pages.FirstOrDefault(pg => pg.Url.Contains("weixin"))
               ?? pages.FirstOrDefault(pg => !string.IsNullOrEmpty(pg.Url) && pg.Url != "about:blank")
               ?? pages.FirstOrDefault();
        if (page != null && page.Url.Contains("weixin")) break;
        await Task.Delay(1000);
    }
    if (page is null) { Console.WriteLine("❌ 未找到 WebView2 页面"); return 1; }
    try { await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 15000 }); } catch { }
    await page.WaitForTimeoutAsync(2500);
    Console.WriteLine($"✅ 已连上内嵌 WebView2：URL={page.Url}");
    Console.WriteLine($"   Title={await page.TitleAsync()}");
    await page.ScreenshotAsync(new() { Path = outPng });
    Console.WriteLine($"📸 截图已存：{outPng}");
    return 0;
}

// ─────────── EXTRA：封面上传 / 原创声明 / 点击链接 / 挂载短剧（交互式探测，不发表）───────────
async Task<int> RunExtraAsync(string[] a, string chrome)
{
    string authFile = @"C:\Users\PC\.weixin_channel_tool\profiles\profile-6\wx_auth_state.json";
    string video = @"E:\video2\workflow\_槐下老翁修仙问长生\material-clip-output\百岁永生-第87集-剪辑.mp4";
    string cover = @"E:\video2\workflow\_槐下老翁修仙问长生\百岁永生.png";
    string dramaName = a.Length > 1 && a[1].Length > 0 ? a[1] : "槐下老翁修仙问长生"; // 新剧名（视频号上注册名）
    string shotDir = Path.Combine(AppContext.BaseDirectory, "screenshots-extra");
    Directory.CreateDirectory(shotDir);

    using var pw = await Playwright.CreateAsync();
    string[] launchArgs = { "--disable-blink-features=AutomationControlled", "--start-maximized" };
    IBrowser browser;
    try { browser = await pw.Chromium.LaunchAsync(new() { Headless = false, Channel = "msedge", Args = launchArgs }); }
    catch { browser = await pw.Chromium.LaunchAsync(new() { Headless = false, ExecutablePath = chrome, Args = launchArgs }); }
    var ctx = await browser.NewContextAsync(new()
    {
        StorageStatePath = authFile,
        UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        ViewportSize = ViewportSize.NoViewport,
    });
    var page = await ctx.NewPageAsync();

    async Task Shot(string name) => await page.ScreenshotAsync(new() { Path = Path.Combine(shotDir, name + ".png"), FullPage = true });
    async Task DumpVisible(string tag)
    {
        string js = """
        () => {
          const all=[]; const walk=r=>{let n;try{n=r.querySelectorAll('*')}catch{return} n.forEach(e=>{all.push(e); if(e.shadowRoot)walk(e.shadowRoot)})}; walk(document);
          const vis=e=>{try{const r=e.getBoundingClientRect();return r.width>4&&r.height>4}catch{return false}};
          const btns=[...new Set(all.filter(e=>vis(e)&&(e.tagName.toLowerCase()==='button'||e.getAttribute('role')==='button'||(e.className?.toString?.()||'').includes('btn'))).map(e=>(e.innerText||'').trim().slice(0,18)).filter(t=>t))];
          const inps=[...new Set(all.filter(e=>vis(e)&&['input','textarea'].includes(e.tagName.toLowerCase())).map(e=>(e.getAttribute('placeholder')||('['+e.getAttribute('type')+']'))).filter(t=>t))];
          const files=all.filter(e=>e.tagName.toLowerCase()==='input'&&e.getAttribute('type')==='file').map(e=>e.getAttribute('accept')||'*');
          return {btns,inps,files};
        }
        """;
        try { var d = await page.EvaluateAsync<JsonElement>(js);
            Console.WriteLine($"[{tag}] 按钮: {string.Join(" | ", d.GetProperty("btns").EnumerateArray().Select(x => x.GetString()))}");
            Console.WriteLine($"[{tag}] 输入: {string.Join(" | ", d.GetProperty("inps").EnumerateArray().Select(x => x.GetString()))}");
            Console.WriteLine($"[{tag}] 文件输入 accept: {string.Join(" | ", d.GetProperty("files").EnumerateArray().Select(x => x.GetString()))}");
        } catch (Exception ex) { Console.WriteLine($"[{tag}] dump EX {ex.Message}"); }
    }
    async Task<bool> ClickFirst(string tag, params string[] locs)
    {
        foreach (var s in locs)
        {
            try { var l = page.Locator(s).First; if (await l.CountAsync() > 0 && await l.IsVisibleAsync()) { await l.ClickAsync(new() { Timeout = 5000 }); Console.WriteLine($"[{tag}] 点击 {s} ✅"); return true; } }
            catch (Exception ex) { Console.WriteLine($"[{tag}] {s} 失败 {ex.Message}"); }
        }
        Console.WriteLine($"[{tag}] 未找到可点击项"); return false;
    }

    // 上传视频
    Console.WriteLine("→ 上传视频…");
    await page.GotoAsync("https://channels.weixin.qq.com/platform/post/create", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
    try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 20000 }); } catch { }
    await page.WaitForTimeoutAsync(6000);
    await page.Locator("input[type=file][accept*='video']").First.SetInputFilesAsync(video);
    try { await page.GetByText("删除").First.WaitForAsync(new() { Timeout = 150000 }); } catch { }
    await page.WaitForTimeoutAsync(2000);

    async Task CloseOverlay() { await page.Keyboard.PressAsync("Escape"); await page.WaitForTimeoutAsync(600); }

    // ① 点击链接（表单干净时先做）
    Console.WriteLine("\n===== ① 点击链接 =====");
    await ClickFirst("打开链接", ".post-with-link", ".link-placeholder", "text=选择链接");
    await page.WaitForTimeoutAsync(1200); await Shot("20-link-open"); await DumpVisible("链接下拉");

    // ② 挂载到视频号剧集（链接 → 视频号剧集 → 选择剧集 → 搜索/选择 → 确定）
    Console.WriteLine("\n===== ② 挂载到视频号剧集 =====");
    if (await ClickFirst("选视频号剧集", "text=视频号剧集"))
    {
        await page.WaitForTimeoutAsync(1200); await Shot("21-drama-panel"); await DumpVisible("剧集面板");
        // 选完「视频号剧集」后新增一行「选择需要关联的剧集」，点它弹出剧集选择器
        await ClickFirst("打开剧集选择器",
            "text=选择需要关联的剧集", "text=选择需要添加的剧集", "text=选择剧集",
            "text=选择需要关联", "text=选择需要添加");
        await page.WaitForTimeoutAsync(1200); await Shot("21b-drama-picker"); await DumpVisible("剧集选择器");
        // 搜索框（取可见的那个，避开位置行/折叠面板里的隐藏同名输入）—— best-effort
        try
        {
            var di = page.Locator("input[placeholder*='搜索内容']:visible").First;
            await di.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 8000 });
            await di.FillAsync(dramaName);
            Console.WriteLine($"[剧集] 搜索「{dramaName}」，等待结果…");
            await page.WaitForTimeoutAsync(3500);
        }
        catch (Exception ex) { Console.WriteLine($"[剧集] 搜索框不可用（{ex.Message}），改为在已加载列表中直接找"); await page.WaitForTimeoutAsync(2500); }
        await Shot("22-drama-search"); await DumpVisible("剧集结果");
        // 结果里点匹配新剧名的项：遍历所有匹配，点第一个「可见」的（避开隐藏文本节点）
        try
        {
            var matches = await page.GetByText(dramaName, new() { Exact = false }).AllAsync();
            bool clicked = false;
            foreach (var m in matches)
            {
                if (!await m.IsVisibleAsync()) continue;
                await m.ScrollIntoViewIfNeededAsync();
                await m.ClickAsync(new() { Timeout = 5000 });
                clicked = true;
                Console.WriteLine($"[剧集] 已点选「{dramaName}」✅（匹配 {matches.Count} 项，点中首个可见）");
                break;
            }
            if (!clicked) Console.WriteLine($"[剧集] 有 {matches.Count} 个「{dramaName}」文本但均不可见（见 22-drama-search.png）");
            await page.WaitForTimeoutAsync(1200); await Shot("23-drama-picked"); await DumpVisible("剧集选中后");
            await ClickFirst("剧集确定", "button:has-text('确定')", "button:has-text('确认')", "button:has-text('完成')", "button:has-text('添加')");
        }
        catch (Exception ex) { Console.WriteLine($"[剧集] 选择失败 {ex.Message}"); await Shot("23-drama-error"); }
    }
    await CloseOverlay(); await Shot("24-after-drama");

    // ③ 封面上传（封面「编辑」按钮被 img 遮挡 → Force 点击 → 弹框 → 上传图片 → 确定）
    Console.WriteLine("\n===== ③ 封面上传 =====");
    try { await page.Locator(".cover-preview-wrap .edit-btn").First.ClickAsync(new() { Force = true, Timeout = 5000 }); Console.WriteLine("[打开封面编辑] Force 点击 .edit-btn ✅"); }
    catch (Exception ex) { Console.WriteLine($"[打开封面编辑] Force 点击失败 {ex.Message}"); }
    await page.WaitForTimeoutAsync(1500); await Shot("30-cover-open"); await DumpVisible("封面弹窗");
    try
    {
        // 弹框里可能先要点「上传封面/本地上传」再出现文件输入
        await ClickFirst("封面上传入口", "text=上传封面", "text=本地上传", "text=上传图片", "text=更换封面");
        await page.WaitForTimeoutAsync(600);
        var ci = page.Locator("input[type=file][accept*='image']").Last;
        await ci.SetInputFilesAsync(cover);
        await page.WaitForTimeoutAsync(2500); await Shot("31-cover-set"); await DumpVisible("封面已选图");
        await ClickFirst("封面确定", "button:has-text('确定')", "button:has-text('确认')", "button:has-text('完成')", "button:has-text('保存')");
        await page.WaitForTimeoutAsync(1500); await Shot("32-cover-confirmed");
        Console.WriteLine("[封面] 已上传并尝试确定");
    }
    catch (Exception ex) { Console.WriteLine($"[封面] 失败 {ex.Message}"); await Shot("31-cover-error"); }
    await CloseOverlay();

    // ④ 原创声明（最后：勾选框→对话框→勾同意→声明原创）
    Console.WriteLine("\n===== ④ 原创声明 =====");
    await ClickFirst("原创勾选", ".declare-original-checkbox label", ".declare-original-checkbox");
    await page.WaitForTimeoutAsync(1500); await Shot("40-original-dialog"); await DumpVisible("原创对话框");
    // 勾选「我已阅读并同意」——「声明原创」按钮才启用
    await ClickFirst("勾同意", ".weui-desktop-dialog .ant-checkbox", ".weui-desktop-dialog__bd .ant-checkbox-input", "text=我已阅读并同意");
    await page.WaitForTimeoutAsync(600); await Shot("41-original-agreed-check");
    await ClickFirst("点声明原创", ".weui-desktop-dialog button:has-text('声明原创')", "button:has-text('声明原创')");
    await page.WaitForTimeoutAsync(1500); await Shot("42-original-done"); await DumpVisible("原创完成");

    Console.WriteLine("\n⏹ 全部交互完成，未点发表。截图目录: " + shotDir);
    await ctx.CloseAsync();
    return 0;
}

// ─────────────── FILL：逐个表单项验证选择器（填描述+短标题，不点发表）───────────────
async Task<int> RunFillAsync(string[] a, string chrome)
{
    string authFile = a.Length > 1 && a[1].Length > 0 ? a[1]
        : @"C:\Users\PC\.weixin_channel_tool\profiles\profile-6\wx_auth_state.json";
    string video = a.Length > 2 && a[2].Length > 0 ? a[2]
        : @"E:\video2\workflow\_槐下老翁修仙问长生\material-clip-output\百岁永生-第87集-剪辑.mp4";
    string shotDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
    Directory.CreateDirectory(shotDir);

    using var pw = await Playwright.CreateAsync();
    string[] launchArgs = { "--disable-blink-features=AutomationControlled", "--start-maximized" };
    IBrowser browser;
    try { browser = await pw.Chromium.LaunchAsync(new() { Headless = false, Channel = "msedge", Args = launchArgs }); }
    catch { browser = await pw.Chromium.LaunchAsync(new() { Headless = false, ExecutablePath = chrome, Args = launchArgs }); }
    var ctx = await browser.NewContextAsync(new()
    {
        StorageStatePath = authFile,
        UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        ViewportSize = ViewportSize.NoViewport,
    });
    var page = await ctx.NewPageAsync();

    Console.WriteLine("→ 打开发表页并上传视频（Edge）…");
    await page.GotoAsync("https://channels.weixin.qq.com/platform/post/create",
        new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
    try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 20000 }); } catch { }
    await page.WaitForTimeoutAsync(6000);
    var fileInput = page.Locator("input[type=file][accept*='video']").First;
    await fileInput.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15000 });
    await fileInput.SetInputFilesAsync(video);
    try { await page.GetByText("删除").First.WaitForAsync(new() { Timeout = 150000 }); }
    catch { Console.WriteLine("⚠️ 未等到「删除」，继续尝试填表。"); }
    await page.WaitForTimeoutAsync(2000);

    var results = new List<(string field, bool ok, string detail)>();

    // 逐项：Playwright 选择器自动穿透 open shadow DOM（wujie 微前端）
    async Task Try(string field, string selector, Func<ILocator, Task> act, Func<ILocator, Task<string>> verify)
    {
        try
        {
            var loc = page.Locator(selector).First;
            await loc.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 8000 });
            await act(loc);
            await page.WaitForTimeoutAsync(400);
            string v = await verify(loc);
            results.Add((field, v.Length > 0, $"{selector} → \"{v}\""));
            Console.WriteLine($"[{field}] {selector} → \"{v}\"  {(v.Length > 0 ? "OK" : "空")}");
        }
        catch (Exception ex) { results.Add((field, false, $"{selector} EX {ex.Message}")); Console.WriteLine($"[{field}] {selector} EXCEPTION {ex.Message}"); }
    }

    // 1) 视频描述（contenteditable）
    await Try("视频描述", "div.input-editor",
        l => l.FillAsync("【自动化验证】槐下老翁修仙问长生 精彩片段"),
        l => l.InnerTextAsync());
    // 2) 短标题
    await Try("短标题", "input[placeholder*='填写短标题']",
        l => l.FillAsync("修仙问长生"),
        l => l.InputValueAsync());

    await page.ScreenshotAsync(new() { Path = Path.Combine(shotDir, "fill-result.png"), FullPage = true });

    // 发表/保存草稿 按钮存在性（不点击）
    int publishBtn = await page.Locator("button.weui-desktop-btn_primary", new() { HasTextString = "发表" }).CountAsync();
    int draftBtn = await page.Locator("button.weui-desktop-btn_default", new() { HasTextString = "保存草稿" }).CountAsync();
    Console.WriteLine($"[发表按钮] 存在={publishBtn>0}  [保存草稿] 存在={draftBtn>0}（均不点击）");

    Console.WriteLine("\n===== 逐项结果 =====");
    foreach (var (f, ok, d) in results) Console.WriteLine($"  {(ok ? "✅" : "❌")} {f}: {d}");
    Console.WriteLine("⏹ 已填描述/短标题，未点发表（安全）。见 fill-result.png。");
    await ctx.CloseAsync();
    return results.All(r => r.ok) ? 0 : 1;
}

// ─────────────── DUMP：上传后一次性导出发表页真实 DOM + 逐控件结构 ───────────────
async Task<int> RunDumpAsync(string[] a, string chrome)
{
    string authFile = a.Length > 1 && a[1].Length > 0 ? a[1]
        : @"C:\Users\PC\.weixin_channel_tool\profiles\profile-6\wx_auth_state.json";
    string video = a.Length > 2 && a[2].Length > 0 ? a[2]
        : @"E:\video2\workflow\_槐下老翁修仙问长生\material-clip-output\百岁永生-第87集-剪辑.mp4";
    string outDir = Path.Combine(AppContext.BaseDirectory, "dump");
    Directory.CreateDirectory(outDir);

    using var pw = await Playwright.CreateAsync();
    string[] launchArgs = { "--disable-blink-features=AutomationControlled", "--start-maximized" };
    IBrowser browser;
    try { browser = await pw.Chromium.LaunchAsync(new() { Headless = false, Channel = "msedge", Args = launchArgs }); }
    catch { browser = await pw.Chromium.LaunchAsync(new() { Headless = false, ExecutablePath = chrome, Args = launchArgs }); }
    var ctx = await browser.NewContextAsync(new()
    {
        StorageStatePath = authFile,
        UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        ViewportSize = ViewportSize.NoViewport,
    });
    var page = await ctx.NewPageAsync();

    Console.WriteLine("→ 打开发表页并上传视频（Edge）…");
    await page.GotoAsync("https://channels.weixin.qq.com/platform/post/create",
        new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
    try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 20000 }); } catch { }
    await page.WaitForTimeoutAsync(6000);
    var fileInput = page.Locator("input[type=file][accept*='video']").First;
    await fileInput.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15000 });
    await fileInput.SetInputFilesAsync(video);

    // 等视频被接收（出现「删除」控件即认为编辑器进入已上传态）
    Console.WriteLine("→ 等待视频进入编辑态（最多 150s）…");
    try { await page.GetByText("删除").First.WaitForAsync(new() { Timeout = 150000 }); }
    catch { Console.WriteLine("⚠️ 未等到「删除」，仍继续 dump 当前 DOM。"); }
    await page.WaitForTimeoutAsync(3000);

    // 1) 整页 HTML
    string html = await page.ContentAsync();
    await File.WriteAllTextAsync(Path.Combine(outDir, "dom-create.html"), html);

    // 2) 逐控件结构化：递归穿透 Shadow DOM（wujie 微前端把表单渲染在 shadow root 里）
    string js = """
    () => {
      const vis = el => { try { const r = el.getBoundingClientRect(); return r.width>0 && r.height>0; } catch { return false; } };
      const all = [];
      const walk = (root) => {
        let nodes; try { nodes = root.querySelectorAll('*'); } catch { return; }
        nodes.forEach(el => { all.push(el); if (el.shadowRoot) walk(el.shadowRoot); });
      };
      walk(document);
      const nearestLabel = el => {
        let p = el;
        for (let i=0;i<8 && p;i++){
          const scope = p.parentElement || (p.getRootNode && p.getRootNode().host);
          if (!scope) break;
          const lab = scope.querySelector && scope.querySelector('.weui-desktop-form__label, label, [class*="label"]');
          if (lab && lab.innerText && lab.innerText.trim()) return lab.innerText.trim().slice(0,24);
          p = scope;
        }
        return '';
      };
      const isCtl = el => { const t=el.tagName.toLowerCase(); return t==='input'||t==='textarea'||el.getAttribute('contenteditable')!==null; };
      const controls = all.filter(isCtl).map(el => ({
        tag: el.tagName.toLowerCase(), type: el.getAttribute('type')||'', editable: el.getAttribute('contenteditable')||'',
        placeholder: el.getAttribute('placeholder')||el.getAttribute('data-placeholder')||'',
        id: el.id||'', name: el.getAttribute('name')||'', aria: el.getAttribute('aria-label')||'',
        cls: (el.className?.toString?.()||'').slice(0,150), label: nearestLabel(el),
        text: (el.innerText||'').trim().slice(0,40), visible: vis(el),
      }));
      const isBtn = el => { const t=el.tagName.toLowerCase(); return t==='button'||el.getAttribute('role')==='button'||(el.className?.toString?.()||'').includes('weui-desktop-btn'); };
      const buttons = all.filter(isBtn).map(b => ({ text:(b.innerText||'').trim().slice(0,30), cls:(b.className?.toString?.()||'').slice(0,120), visible: vis(b) })).filter(b => b.text);
      const rows = all.filter(el => el.classList && (el.classList.contains('weui-desktop-form__label')||(el.className?.toString?.()||'').includes('form__label')))
        .map(l => ({ label:(l.innerText||'').trim().slice(0,20) })).filter(r => r.label);
      // 收集所有 shadow root 的 innerHTML（真实表单标记）
      const shadowHtml = all.filter(el => el.shadowRoot).map(el => `<!-- shadow host: ${el.tagName.toLowerCase()}.${(el.className?.toString?.()||'').slice(0,40)} -->\n` + el.shadowRoot.innerHTML).join('\n\n');
      return { controls, buttons, rows, shadowHtml };
    }
    """;
    var data = await page.EvaluateAsync<JsonElement>(js);
    string shadowHtml = data.TryGetProperty("shadowHtml", out var sh) ? (sh.GetString() ?? "") : "";
    await File.WriteAllTextAsync(Path.Combine(outDir, "dom-shadow.html"), shadowHtml);
    await File.WriteAllTextAsync(Path.Combine(outDir, "dom-fields.json"),
        JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    await page.ScreenshotAsync(new() { Path = Path.Combine(outDir, "dump.png"), FullPage = true });

    // 3) 控制台打印精简摘要（便于直接查看）
    Console.WriteLine("\n===== CONTROLS (input/textarea/contenteditable) =====");
    foreach (var c in data.GetProperty("controls").EnumerateArray())
        Console.WriteLine($"[{c.GetProperty("tag").GetString()}] label={q(c,"label")} type={q(c,"type")} editable={q(c,"editable")} ph={q(c,"placeholder")} id={q(c,"id")} vis={c.GetProperty("visible").GetBoolean()} cls={q(c,"cls")}");
    Console.WriteLine("\n===== BUTTONS =====");
    foreach (var b in data.GetProperty("buttons").EnumerateArray())
        if (b.GetProperty("visible").GetBoolean())
            Console.WriteLine($"btn: {q(b,"text")}  cls={q(b,"cls")}");
    Console.WriteLine("\n===== FORM LABELS =====");
    foreach (var r in data.GetProperty("rows").EnumerateArray())
        Console.WriteLine($"label: {q(r,"label")}");

    Console.WriteLine($"\n✅ 已导出: {outDir}\\dom-create.html, dom-fields.json, dump.png");
    await ctx.CloseAsync();
    return 0;

    static string q(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? (v.GetString() ?? "") : "";
}

// ─────────────── DIAG：诊断发表页真实结构 ───────────────
async Task<int> RunDiagAsync(string[] a, string chrome)
{
    string authFile = a.Length > 1 && a[1].Length > 0 ? a[1]
        : @"C:\Users\PC\.weixin_channel_tool\profiles\profile-6\wx_auth_state.json";
    string shotDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
    Directory.CreateDirectory(shotDir);
    using var pw = await Playwright.CreateAsync();
    await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = false, ExecutablePath = chrome });
    var ctx = await browser.NewContextAsync(new()
    {
        StorageStatePath = authFile,
        ViewportSize = new() { Width = 1440, Height = 900 },
    });
    var page = await ctx.NewPageAsync();

    foreach (var url in new[] { "https://channels.weixin.qq.com/platform/post/create" })
    {
        Console.WriteLine($"\n=== 诊断 {url} ===");
        await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 20000 }); } catch { }
        await page.WaitForTimeoutAsync(6000);
        Console.WriteLine($"URL={page.Url}  Title={await page.TitleAsync()}");

        // 主 frame + 所有子 frame 里的 file input
        foreach (var fr in page.Frames)
        {
            int n = await fr.Locator("input[type=file]").CountAsync();
            if (n > 0)
            {
                Console.WriteLine($"[frame {fr.Url}] input[type=file] x{n}");
                for (int i = 0; i < n; i++)
                {
                    var oh = await fr.Locator("input[type=file]").Nth(i)
                        .EvaluateAsync<string>("el => el.outerHTML.slice(0,200)");
                    Console.WriteLine($"   #{i}: {oh}");
                }
            }
        }
        // 关键文案是否存在
        foreach (var t in new[] { "选取视频", "请选择要上传的视频文件", "发表视频", "上传", "拖拽" })
        {
            int c = await page.GetByText(t).CountAsync();
            if (c > 0) Console.WriteLine($"文案「{t}」x{c}");
        }
        // 可能的「发表视频」入口按钮
        foreach (var t in new[] { "发表视频", "发表", "上传视频" })
        {
            int c = await page.GetByRole(AriaRole.Button, new() { NameString = t }).CountAsync();
            if (c > 0) Console.WriteLine($"按钮「{t}」x{c}");
        }
        await page.ScreenshotAsync(new() { Path = Path.Combine(shotDir, "diag-create.png"), FullPage = true });
    }
    await ctx.CloseAsync();
    return 0;
}

// ─────────────────────────── 辅助 ───────────────────────────
static string CreateSampleVideo()
{
    string p = Path.Combine(Path.GetTempPath(), "cdp-spike-sample.mp4");
    // 写一个非空的最小文件（内容不重要，只验证「文件被自动化附加到 input」）
    File.WriteAllBytes(p, new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32, 0xDE, 0xAD, 0xBE, 0xEF });
    return p;
}

static async Task<string?> WaitForCdpEndpointAsync(int port, TimeSpan timeout)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            var json = await http.GetStringAsync($"http://localhost:{port}/json/version");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var ws) ? ws.GetString() : "http";
        }
        catch { await Task.Delay(300); }
    }
    return null;
}
