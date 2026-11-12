using Microsoft.Playwright;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services;

/// <summary>
/// 同一个账号的外部浏览器上传共用一个浏览器进程，每部剧一个标签页。
/// 不同账号登录态不同，不能放进同一个浏览器。最后一部传完才关闭这个浏览器。
/// </summary>
public sealed class TikTokUploadBrowserPool
{
    public static TikTokUploadBrowserPool Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);

    private TikTokUploadBrowserPool()
    {
    }

    public async Task<PageLease> RentAsync(
        TikTokAccountProfile account,
        string? navigateUrl,
        bool headless,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var key = SessionKey(account, headless);
        Session session;
        var owner = false;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out session!) || session.Closing || session.LaunchFailed)
            {
                session = new Session(key);
                _sessions[key] = session;
                owner = true;
            }

            session.Users++;
        }

        if (owner)
            session.StartLaunch(account, navigateUrl, headless, log, cancellationToken);

        IPage? opened = null;
        try
        {
            var launched = await session.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            IPage? page = null;
            lock (_gate)
            {
                if (!launched.FirstPageTaken)
                {
                    launched.FirstPageTaken = true;
                    page = launched.FirstPage;
                }
            }

            if (page is null)
            {
                opened = await launched.Context.NewPageAsync().ConfigureAwait(false);
                page = opened;
                var url = string.IsNullOrWhiteSpace(navigateUrl) ? TikTokUrls.DefaultSeriesDraftUrl : navigateUrl;
                await EmbeddedBrowserAutomationBridge.PrepareExternalPageAsync(page, url, log, cancellationToken)
                    .ConfigureAwait(false);
                log?.Invoke($"同一账号继续使用已打开的上传浏览器，新开标签页：{account.DisplayName}");
                opened = null;
            }

            return new PageLease(this, session, page);
        }
        catch
        {
            await ReleaseAsync(session, opened).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReleaseAsync(Session session, IPage? page)
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
        try { await launched.Browser.DisposeAsync().ConfigureAwait(false); }
        catch { /* 浏览器已经退出 */ }
        launched.Playwright.Dispose();
    }

    private static string SessionKey(TikTokAccountProfile account, bool headless)
    {
        var id = string.IsNullOrWhiteSpace(account.Id) ? account.DisplayName : account.Id;
        return (id ?? "default").Trim() + (headless ? "|headless" : "|headed");
    }

    internal sealed class Session(string key)
    {
        internal string Key { get; } = key;
        internal TaskCompletionSource<LaunchedBrowser> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenSource StopSource { get; } = new();
        internal string StopReason { get; private set; } = "";
        internal int Users { get; set; }
        internal bool Closing { get; set; }
        internal bool LaunchFailed => Ready.Task.IsCompleted && !Ready.Task.IsCompletedSuccessfully;

        public void StartLaunch(
            TikTokAccountProfile account,
            string? navigateUrl,
            bool headless,
            Action<string>? log,
            CancellationToken cancellationToken)
        {
            _ = LaunchAsync(account, navigateUrl, headless, log, cancellationToken);
        }

        public void Stop(string reason)
        {
            if (string.IsNullOrWhiteSpace(StopReason))
                StopReason = reason.Trim();
            try { StopSource.Cancel(); }
            catch (ObjectDisposedException) { /* 浏览器已经释放 */ }
        }

        private async Task LaunchAsync(
            TikTokAccountProfile account,
            string? navigateUrl,
            bool headless,
            Action<string>? log,
            CancellationToken cancellationToken)
        {
            try
            {
                var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
                var (playwright, browser, page) = await EmbeddedBrowserAutomationBridge
                    .LaunchPageAsync(account, navigateUrl, authPath, headless, log, cancellationToken)
                    .ConfigureAwait(false);
                var context = page.Context;
                Ready.TrySetResult(new LaunchedBrowser(playwright, browser, context, page));
            }
            catch (Exception ex)
            {
                Ready.TrySetException(ex);
            }
        }
    }

    internal sealed class LaunchedBrowser(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage firstPage)
    {
        internal IPlaywright Playwright { get; } = playwright;
        internal IBrowser Browser { get; } = browser;
        internal IBrowserContext Context { get; } = context;
        internal IPage FirstPage { get; } = firstPage;
        internal bool FirstPageTaken { get; set; }
    }

    public sealed class PageLease : IAsyncDisposable
    {
        private readonly TikTokUploadBrowserPool _pool;
        private readonly Session _session;
        private readonly IPage _page;
        private int _released;

        internal PageLease(TikTokUploadBrowserPool pool, Session session, IPage page)
        {
            _pool = pool;
            _session = session;
            _page = page;
        }

        public IPage Page => _page;

        public CancellationToken Stopped => _session.StopSource.Token;

        public string StopReason => _session.StopReason;

        public void StopAccount(string reason)
        {
            if (!string.IsNullOrWhiteSpace(reason))
                _session.Stop(reason);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            await _pool.ReleaseAsync(_session, _page).ConfigureAwait(false);
        }
    }
}
