using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services;

public static class TikTokLicenseContractAutofill
{
    public static async Task<TikTokLicenseContractLookupResult> EnsureAsync(
        TikTokAccountProfile account,
        Action<string>? log,
        Func<TikTokAccountProfile, Task>? save,
        CancellationToken ct)
    {
        var result = await TikTokLicenseContractLookup.TryReadAsync(account, log, ct).ConfigureAwait(false);
        if (result.Status != TikTokLicenseContractLookupStatus.Single)
            return result;
        if (!TikTokLicenseContractLookup.TryApply(account, result.LicenseId))
            return TikTokLicenseContractLookupResult.AlreadyConfigured();
        if (save is not null)
            await save(account).ConfigureAwait(false);
        log?.Invoke(TikTokLicenseContractLookup.SavedLog(account.TiktokContractId));
        return result;
    }
}
