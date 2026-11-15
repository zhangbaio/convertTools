using Microsoft.Playwright;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services;

/// <summary>
/// 每部剧的外部浏览器上传各自打开一个浏览器，传完就关闭。
/// 同一账号的登录失效或单日上限仍会让这个账号正在上传的其他剧一起停下。
/// </summary>
public sealed class TikTokUploadBrowserPool
{
    public static TikTokUploadBrowserPool Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, AccountStop> _stops = new(StringComparer.OrdinalIgnoreCase);

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
        AccountStop stop;
        lock (_gate)
        {
            if (!_stops.TryGetValue(key, out stop!) || stop.Closed)
            {
                stop = new AccountStop();
                _stops[key] = stop;
            }

            stop.Users++;
        }

        try
        {
            var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
            var (playwright, browser, page) = await EmbeddedBrowserAutomationBridge
                .LaunchPageAsync(account, navigateUrl, authPath, headless, log, cancellationToken)
                .ConfigureAwait(false);
            return new PageLease(this, key, stop, playwright, browser, page);
        }
        catch
        {
            ReleaseStop(key, stop);
            throw;
        }
    }

    private void ReleaseStop(string key, AccountStop stop)
    {
        lock (_gate)
        {
            stop.Users = Math.Max(0, stop.Users - 1);
            if (stop.Users == 0 && _stops.TryGetValue(key, out var current) && ReferenceEquals(current, stop))
            {
                stop.Closed = true;
                _stops.Remove(key);
            }
        }
    }

    private static string SessionKey(TikTokAccountProfile account, bool headless)
    {
        var id = string.IsNullOrWhiteSpace(account.Id) ? account.DisplayName : account.Id;
        return (id ?? "default").Trim() + (headless ? "|headless" : "|headed");
    }

    internal sealed class AccountStop
    {
        internal CancellationTokenSource Source { get; } = new();
        internal int Users { get; set; }
        internal bool Closed { get; set; }
        internal string Reason { get; private set; } = "";

        internal void Stop(string reason)
        {
            if (string.IsNullOrWhiteSpace(Reason))
                Reason = reason.Trim();
            try { Source.Cancel(); }
            catch (ObjectDisposedException) { /* 浏览器已经释放 */ }
        }
    }

    public sealed class PageLease : IAsyncDisposable
    {
        private readonly TikTokUploadBrowserPool _pool;
        private readonly string _key;
        private readonly AccountStop _stop;
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;
        private readonly IPage _page;
        private int _released;

        internal PageLease(
            TikTokUploadBrowserPool pool,
            string key,
            AccountStop stop,
            IPlaywright playwright,
            IBrowser browser,
            IPage page)
        {
            _pool = pool;
            _key = key;
            _stop = stop;
            _playwright = playwright;
            _browser = browser;
            _page = page;
        }

        public IPage Page => _page;

        public CancellationToken Stopped => _stop.Source.Token;

        public string StopReason => _stop.Reason;

        public void StopAccount(string reason)
        {
            if (!string.IsNullOrWhiteSpace(reason))
                _stop.Stop(reason);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            try { await _browser.DisposeAsync().ConfigureAwait(false); }
            catch { /* 浏览器已经退出 */ }
            _playwright.Dispose();
            _pool.ReleaseStop(_key, _stop);
        }
    }
}
