using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Core.Services;

/// <summary>剧集上传前置校验（对齐 Python <c>publish_service._ensure_tiktok_upload_prerequisites</c>）。</summary>
public static class TikTokUploadPrerequisiteService
{
    public static void EnsureApiContractConfigured(TikTokAccountProfile account)
    {
        if (!TikTokUploadChannels.IsApi(account.TiktokUploadStrategy))
            return;
        if (!string.IsNullOrWhiteSpace(account.TiktokContractId))
            return;

        throw new InvalidOperationException(
            "接口上传前请先配置合同。请在「账号管理 → 发布配置」填写合同 ID 后再上传。");
    }

    public static void EnsureUploadPrerequisites(TikTokAccountProfile account, Action<string>? log = null)
    {
        EnsureApiContractConfigured(account);

        var missing = new List<string>();
        if (!TikTokUploadChannels.IsApi(account.TiktokUploadStrategy)
            && string.IsNullOrWhiteSpace(account.TiktokContractId))
        {
            missing.Add("关联合同 ID（发布配置）");
        }

        if (string.IsNullOrWhiteSpace(account.TiktokLoginEmail))
            missing.Add("TikTok 用户名（登录设置）");

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
        if (TikTokApiSeriesDocument.ReadPrice(account.TiktokExpectedFullPriceValue) is not null)
            return;

        var hint =
            "上传前请先配置预期价格。请在「账号管理 → 发布配置」填写美元价格，例如 0.3。";
        log?.Invoke(hint);
        throw new InvalidOperationException(hint);
    }

    public static bool PaidUploadPossible(TikTokAccountProfile account)
    {
        if (account.TiktokPaidRatioEnabled && account.TiktokPaidRatioPercent > 0.0)
            return true;
        return account.TiktokPaidEnabled;
    }
}
