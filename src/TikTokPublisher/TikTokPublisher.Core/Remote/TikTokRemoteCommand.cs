using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Remote;

// TikTokUploadSeriesSpec 描述一条上传剧集及其可选集数（用于「剧名 + 集数」精准匹配）。
public sealed record TikTokUploadSeriesSpec(string Title, int EpisodeCnt = 0, string SeriesId = "");

public sealed record TikTokRemoteCommand(
    string Command,
    IReadOnlyList<string>? Titles = null,
    string WorkspacePath = "",
    string AccountProfileId = "",
    string AccountProfileName = "",
    IReadOnlyList<string>? AccountSelectors = null,
    bool AllAccounts = false,
    IReadOnlyList<string>? EnabledSteps = null,
    bool AutoRun = true,
    IReadOnlyDictionary<string, object?>? QueueOptions = null,
    string MatchMode = "",
    IReadOnlyList<TikTokUploadSeriesSpec>? Series = null)
{
    public bool IsUploadCommand =>
        string.Equals(Command, TikTokRemoteCommandNames.UploadSeries, StringComparison.Ordinal);

    // 仅当显式声明 title_episode 且存在带正整数集数的剧集时，才启用「剧名 + 集数」匹配。
    public bool UsesEpisodeMatching =>
        string.Equals(MatchMode, "title_episode", StringComparison.OrdinalIgnoreCase) &&
        Series is { Count: > 0 } &&
        Series.Any(spec => spec.EpisodeCnt > 0);

    public bool IsStartQueueCommand =>
        string.Equals(Command, TikTokRemoteCommandNames.StartQueue, StringComparison.Ordinal);

    public bool HasExplicitAccountSelection =>
        AllAccounts ||
        !string.IsNullOrWhiteSpace(AccountProfileId) ||
        !string.IsNullOrWhiteSpace(AccountProfileName) ||
        AccountSelectors is { Count: > 0 };

    public bool HasMultiAccountSelection =>
        AllAccounts || AccountSelectors is { Count: > 1 };
}

public static class TikTokRemoteCommandNames
{
    public const string UploadSeries = "tiktok_upload_series";
    public const string StartQueue = "tiktok_start_queue";
    public const string StopQueue = "tiktok_stop_queue";
    public const string QueryStatus = "tiktok_query_status";
    public const string ShowHelpText = "show_help_text";
    public const string ShowHelpCard = "show_help_card";
    public const string SwitchAccountProfile = "switch_account_profile";
}

public sealed record TikTokRemoteCommandResult(
    string Status,
    string SummaryText,
    string Command = "",
    string ReplyMessageType = TikTokRemoteReplyMessageTypes.Text,
    string ReplyContent = "")
{
    public static TikTokRemoteCommandResult Accepted(string command, string summary) =>
        new("accepted", summary, command, TikTokRemoteReplyMessageTypes.Text, summary);

    public static TikTokRemoteCommandResult Success(string command, string summary) =>
        new("success", summary, command, TikTokRemoteReplyMessageTypes.Text, summary);

    public static TikTokRemoteCommandResult SuccessCard(string command, string summary, string cardJson) =>
        new("success", summary, command, TikTokRemoteReplyMessageTypes.Interactive, cardJson);

    public static TikTokRemoteCommandResult Failed(string command, string summary) =>
        new("failed", summary, command, TikTokRemoteReplyMessageTypes.Text, summary);
}

public static class TikTokRemoteReplyMessageTypes
{
    public const string Text = "text";
    public const string Interactive = "interactive";
}

public static class TikTokRemoteCommandStepDefaults
{
    public static IReadOnlyList<string> FullUploadDefaultEnabledSteps { get; } = new[]
    {
        QueueStepRegistry.Download,
        QueueStepRegistry.RewriteInfo,
        QueueStepRegistry.GeneratePoster,
        QueueStepRegistry.SmallVideoRepair,
        QueueStepRegistry.SilenceDetect,
        QueueStepRegistry.SilenceRepair,
        QueueStepRegistry.MaterialValidate,
        QueueStepRegistry.UploadSeries,
    };
}
