using Avalonia.Threading;
using TikTokPublisher.Core.Abstractions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Ui.Controls;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Services;

/// <summary>将 <see cref="BrowserSessionHost"/> 暴露为发布流程可用的内置浏览器提供者。</summary>
public sealed class EmbeddedBrowserProvider : IEmbeddedBrowserProvider
{
    private readonly BrowserSessionHost _browserHost;
    private readonly Func<TikTokAccountProfile, AccountItemViewModel?> _resolveAccountVm;
    private readonly Action<AccountItemViewModel>? _onFocusBrowser;

    public EmbeddedBrowserProvider(
        BrowserSessionHost browserHost,
        Func<TikTokAccountProfile, AccountItemViewModel?> resolveAccountVm,
        Action<AccountItemViewModel>? onFocusBrowser = null)
    {
        _browserHost = browserHost;
        _resolveAccountVm = resolveAccountVm;
        _onFocusBrowser = onFocusBrowser;
    }

    public async Task<IEmbeddedBrowser?> GetBrowserAsync(TikTokAccountProfile account, CancellationToken ct)
    {
        var accountVm = _resolveAccountVm(account);
        if (accountVm is null)
            return null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _browserHost.GetOrCreateHost(accountVm);
            _browserHost.ShowAccount(accountVm);
            _onFocusBrowser?.Invoke(accountVm);
        });

        var result = await _browserHost.PrepareForPublishAsync(accountVm, ct).ConfigureAwait(false);
        if (!result.Ok)
            return null;

        return _browserHost.TryGetHost(account.Id);
    }
}
