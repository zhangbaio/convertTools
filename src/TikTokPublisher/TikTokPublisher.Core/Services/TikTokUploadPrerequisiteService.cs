using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Core.Services;

/// <summary>剧集上传前置校验（对齐 Python <c>publish_service._ensure_tiktok_upload_prerequisites</c>）。</summary>
public static class TikTokUploadPrerequisiteService
{
    public static void EnsureUploadPrerequisites(TikTokAccountProfile account, Action<string>? log = null)
    {
        var missing = new List<string>();
        var contractMode = string.IsNullOrWhiteSpace(account.TiktokContractIdMode)
            ? TikTokPublishConstants.ContractIdModeManual
            : account.TiktokContractIdMode.Trim();

        if (string.Equals(contractMode, TikTokPublishConstants.ContractIdModeManual, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(account.TiktokContractId))
        {
            missing.Add("关联合同 ID（发布配置）");
        }

        if (string.IsNullOrWhiteSpace(account.TiktokLoginEmail))
            missing.Add("TikTok 用户名（登录设置）");

        if (IsCdpLoginMode(account) && string.IsNullOrWhiteSpace(account.TiktokLoginPassword))
            missing.Add("TikTok 密码（登录设置）");

        if (missing.Count > 0)
        {
            var hint = "TikTok 发布配置不完整，已跳过本次剧集上传，请在「账号管理」中补全当前账号："
                       + string.Join("、", missing);
            log?.Invoke(hint);
            throw new InvalidOperationException(hint);
        }

        EnsureCommercialConfigValid(account, log);
    }

    public static void EnsureCommercialConfigValid(TikTokAccountProfile account, Action<string>? log = null)
    {
        if (!PaidUploadPossible(account))
            return;

        var mode = string.IsNullOrWhiteSpace(account.TiktokExpectedFullPriceMode)
            ? "manual"
            : account.TiktokExpectedFullPriceMode.Trim();
        if (!string.Equals(mode, "manual", StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.IsNullOrWhiteSpace(account.TiktokExpectedFullPriceValue))
            return;

        var hint =
            "TikTok 发布配置不完整，已跳过本次剧集上传："
            + "「是否付费=是」或已启用「按比例收费」时，必须在「账号管理 → 发布配置」中"
            + "配置“预期全集价格设置”。";
        log?.Invoke(hint);
        throw new InvalidOperationException(hint);
    }

    public static bool PaidUploadPossible(TikTokAccountProfile account)
    {
        if (account.TiktokPaidRatioEnabled && account.TiktokPaidRatioPercent > 0.0)
            return true;
        return account.TiktokPaidEnabled;
    }

    private static bool IsCdpLoginMode(TikTokAccountProfile account)
    {
        var mode = (account.TiktokLoginBrowserMode ?? "").Trim().ToLowerInvariant();
        return mode is "cdp" or "fingerprint" or "fingerprint_browser";
    }
}
