using Avalonia.Threading;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Ui.Controls;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Services;

/// <summary>将 <see cref="BrowserSessionHost"/> 暴露为发布流程可用的内置浏览器提供者。</summary>
public sealed class EmbeddedBrowserProvider : IEmbeddedBrowserProvider
{
    private readonly BrowserSessionHost _browserHost;
    private readonly Func<TikTokAccountProfile, AccountItemViewModel?> _resolveAccountVm;
    private readonly Action<AccountItemViewModel>? _onFocusBrowser;
    private readonly Action? _onEnsureMounted;

    public EmbeddedBrowserProvider(
        BrowserSessionHost browserHost,
        Func<TikTokAccountProfile, AccountItemViewModel?> resolveAccountVm,
        Action<AccountItemViewModel>? onFocusBrowser = null,
        Action? onEnsureMounted = null)
    {
        _browserHost = browserHost;
        _resolveAccountVm = resolveAccountVm;
        _onFocusBrowser = onFocusBrowser;
        _onEnsureMounted = onEnsureMounted;
    }

    public async Task<IEmbeddedBrowser?> GetBrowserAsync(
        TikTokAccountProfile account,
        CancellationToken ct,
        EmbeddedBrowserAccessOptions? options = null)
    {
        var ready = await EnsureBrowserReadyAsync(account, ct, options).ConfigureAwait(false);
        return ready.Ok ? _browserHost.TryGetHost(account.Id) : null;
    }

    public async Task<QueueBrowserReadyResult> EnsureBrowserReadyAsync(
        TikTokAccountProfile account,
        CancellationToken ct,
        EmbeddedBrowserAccessOptions? options = null,
        Action<string>? log = null)
    {
        var accountVm = _resolveAccountVm(account);
        if (accountVm is null)
            return QueueBrowserReadyResult.NotReady($"未找到账号：{account.DisplayName}");

        var bringToFront = options?.BringToFront == true;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (bringToFront)
                _onFocusBrowser?.Invoke(accountVm);
            else
                _onEnsureMounted?.Invoke();

            _browserHost.InvalidateHostIfNetworkChanged(account);
            _browserHost.GetOrCreateHost(accountVm);
            if (bringToFront)
                _browserHost.ShowAccount(accountVm);
        }).GetTask();

        if (bringToFront)
            await Task.Delay(800, ct).ConfigureAwait(false);

        var result = await _browserHost
            .PrepareForPublishAsync(accountVm, bringToFront, ct, log)
            .ConfigureAwait(false);
        return result.Ok
            ? QueueBrowserReadyResult.Ready()
            : QueueBrowserReadyResult.NotReady(result.Message);
    }
}
