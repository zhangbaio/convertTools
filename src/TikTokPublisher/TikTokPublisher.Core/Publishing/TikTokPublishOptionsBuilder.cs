using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Publishing;

public static class TikTokPublishOptionsBuilder
{
    public static TikTokPublishOptions FromAccount(
        TikTokAccountProfile? account,
        string? workflowProjectDir = null,
        Action<string>? log = null)
    {
        if (account is null)
        {
            return new TikTokPublishOptions
            {
                TargetAudienceMode = "ai_recommend",
                GenreCount = 3,
            };
        }

        var options = TikTokPublishOptions.FromAccount(account);
        options.TargetAudienceMode = NormalizeTargetAudienceMode(account.TiktokTargetAudienceMode);
        options.PaidEnabled = TikTokPaidRatioService.DecidePaidForUpload(account, workflowProjectDir, log);
        return options;
    }

    private static string NormalizeTargetAudienceMode(string? mode)
    {
        var normalized = (mode ?? "ai_recommend").Trim();
        return normalized is "female" or "male" or "ai_recommend" ? normalized : "ai_recommend";
    }
}
