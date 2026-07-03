using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Publishing;

public static class TikTokPublishOptionsBuilder
{
    public static TikTokPublishOptions FromAccount(TikTokAccountProfile? account)
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
        return options;
    }

    private static string NormalizeTargetAudienceMode(string? mode)
    {
        var normalized = (mode ?? "ai_recommend").Trim();
        return normalized is "female" or "male" or "ai_recommend" ? normalized : "ai_recommend";
    }
}
