using Microsoft.Playwright;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 同一个账号、同一种版本的上传共用一个浏览器，每部剧一个标签页。
/// 个人版和企业版登录文件不同，无头和有头也不混用。最后一部传完才关闭浏览器。
/// </summary>
public sealed class KuaishouUploadBrowserPool
{
    public static KuaishouUploadBrowserPool Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);

    private KuaishouUploadBrowserPool()
    {
    }

    public async Task<PageLease> RentAsync(
        KuaishouPersonalConfig config,
        bool headless,
        Func<CancellationToken, Task<(IPlaywright Playwright, IBrowser Browser, IBrowserContext Context)>> launch,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var key = SessionKey(config, headless);
        Session session;
        var owner = false;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out session!) || session.Closing || session.LaunchFailed)
            {
                session = new Session(key, config.AuthStatePath);
                _sessions[key] = session;
                owner = true;
            }

            session.Users++;
        }

        if (owner)
            session.StartLaunch(launch, cancellationToken);

        IPage? opened = null;
        try
        {
            var launched = await session.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var reuse = false;
            lock (_gate)
                reuse = session.OpenedPages++ > 0;

            opened = await launched.Context.NewPageAsync().ConfigureAwait(false);
            var timeout = Math.Clamp(config.LoginTimeoutSeconds, 30, 900) * 1000;
            var url = string.IsNullOrWhiteSpace(config.EntryUrl)
                ? "https://kdj.kuaishou.com/home/content/content-management"
                : config.EntryUrl.Trim();
            await opened.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = timeout,
            }).ConfigureAwait(false);
            await opened.WaitForTimeoutAsync(1200).ConfigureAwait(false);
            if (reuse)
                log?.Invoke("同一账号继续使用已打开的上传浏览器，新开标签页");

            var page = opened;
            opened = null;
            return new PageLease(this, session, page);
        }
        catch
        {
            await ReleaseAsync(session, opened, saveStorage: false).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReleaseAsync(Session session, IPage? page, bool saveStorage)
    {
        if (page is not null && !page.IsClosed)
        {
            try { await page.CloseAsync().ConfigureAwait(false); }
            catch { /* 页面已经关掉 */ }
        }

        bool last;
        lock (_gate)
        {
            session.Users = Math.Max(0, session.Users - 1);
            last = session.Users == 0;
            if (last)
            {
                session.Closing = true;
                if (_sessions.TryGetValue(session.Key, out var current) && ReferenceEquals(current, session))
                    _sessions.Remove(session.Key);
            }
        }

        if (!last || !session.Ready.Task.IsCompletedSuccessfully)
            return;

        var launched = session.Ready.Task.Result;
        if (saveStorage && !session.SkipStorageSave && !string.IsNullOrWhiteSpace(session.AuthStatePath))
        {
            try
            {
                await launched.Context.StorageStateAsync(new BrowserContextStorageStateOptions
                {
                    Path = session.AuthStatePath,
                }).ConfigureAwait(false);
            }
            catch { /* 登录态保存失败由下次校验暴露。 */ }
        }

        try { await launched.Context.CloseAsync().ConfigureAwait(false); }
        catch { /* 停止时浏览器可能已经关掉。 */ }
        try { await launched.Browser.CloseAsync().ConfigureAwait(false); }
        catch { /* 停止时浏览器可能已经关掉。 */ }
        launched.Playwright.Dispose();
    }

    internal static string SessionKey(KuaishouPersonalConfig config, bool headless)
    {
        var account = string.IsNullOrWhiteSpace(config.StorageAccountId) ? "default" : config.StorageAccountId.Trim();
        var edition = config.StoragePlatform == PublishPlatform.KuaishouEnterpriseRevenue
            ? "enterprise"
            : "personal";
        return account + "|" + edition + (headless ? "|headless" : "|headed");
    }

    internal sealed class Session(string key, string authStatePath)
    {
        internal string Key { get; } = key;
        internal string AuthStatePath { get; } = authStatePath ?? "";
        internal TaskCompletionSource<LaunchedBrowser> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenSource StopSource { get; } = new();
        internal string StopReason { get; private set; } = "";
        internal int Users { get; set; }
        internal int OpenedPages { get; set; }
        internal bool Closing { get; set; }
        internal bool SkipStorageSave { get; private set; }
        internal bool LaunchFailed => Ready.Task.IsCompleted && !Ready.Task.IsCompletedSuccessfully;

        public void StartLaunch(
            Func<CancellationToken, Task<(IPlaywright Playwright, IBrowser Browser, IBrowserContext Context)>> launch,
            CancellationToken cancellationToken)
        {
            _ = LaunchAsync(launch, cancellationToken);
        }

        public void Stop(string reason)
        {
            if (string.IsNullOrWhiteSpace(StopReason))
            {
                StopReason = reason.Trim();
                if (StopReason.Contains("登录态无效", StringComparison.Ordinal))
                    SkipStorageSave = true;
            }

            try { StopSource.Cancel(); }
            catch (ObjectDisposedException) { /* 浏览器已经释放 */ }
        }

        private async Task LaunchAsync(
            Func<CancellationToken, Task<(IPlaywright Playwright, IBrowser Browser, IBrowserContext Context)>> launch,
            CancellationToken cancellationToken)
        {
            try
            {
                var (playwright, browser, context) = await launch(cancellationToken).ConfigureAwait(false);
                Ready.TrySetResult(new LaunchedBrowser(playwright, browser, context));
            }
            catch (Exception ex)
            {
                Ready.TrySetException(ex);
            }
        }
    }

    internal sealed class LaunchedBrowser(IPlaywright playwright, IBrowser browser, IBrowserContext context)
    {
        internal IPlaywright Playwright { get; } = playwright;
        internal IBrowser Browser { get; } = browser;
        internal IBrowserContext Context { get; } = context;
    }

    public sealed class PageLease : IAsyncDisposable
    {
        private readonly KuaishouUploadBrowserPool _pool;
        private readonly Session _session;
        private readonly IPage _page;
        private int _released;

        internal PageLease(KuaishouUploadBrowserPool pool, Session session, IPage page)
        {
            _pool = pool;
            _session = session;
            _page = page;
        }

        public IPage Page => _page;

        public CancellationToken Stopped => _session.StopSource.Token;

        public string StopReason => _session.StopReason;

        public bool IsOnlyUser
        {
            get
            {
                lock (_pool._gate)
                    return _session.Users <= 1;
            }
        }

        public void StopAccount(string reason)
        {
            if (!string.IsNullOrWhiteSpace(reason))
                _session.Stop(reason);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            await _pool.ReleaseAsync(_session, _page, saveStorage: true).ConfigureAwait(false);
        }
    }
}
