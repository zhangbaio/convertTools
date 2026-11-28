using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Remote;

public static class TikTokRemoteRunOptions
{
    public static QueueRunOptions BuildTikTokUploadRunOptions(
        TikTokRemoteCommand? command = null)
    {
        var commandSteps = command?.EnabledSteps is { Count: > 0 }
            ? command.EnabledSteps
            : null;
        var enabledSteps = commandSteps ?? TikTokRemoteCommandStepDefaults.FullUploadDefaultEnabledSteps;
        var queueOptions = command?.QueueOptions;

        return new QueueRunOptions
        {
            EnabledSteps = QueueStepRegistry.OrderEnabledSteps(enabledSteps.Select(step => (step ?? "").Trim())).ToList(),
            AutoArchiveAfterUpload = RemoteOptionBool(
                queueOptions,
                "auto_archive_after_upload",
                false),
            ForceRerunCompletedSteps = RemoteOptionBool(
                queueOptions,
                "force_rerun_completed_steps",
                false),
            SyncManagementAfterUpload =
                RemoteOptionBool(queueOptions, "sync_management_on_upload_success", false) ||
                RemoteOptionBool(queueOptions, "sync_management_after_upload", false),
            ProjectConcurrency = 4,
        };
    }

    private static bool RemoteOptionBool(
        IReadOnlyDictionary<string, object?>? queueOptions,
        string key,
        bool fallback)
    {
        if (queueOptions is null || !queueOptions.TryGetValue(key, out var value))
            return fallback;

        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            string s when int.TryParse(s, out var number) => number != 0,
            int i => i != 0,
            long l => l != 0,
            double d => Math.Abs(d) > double.Epsilon,
            _ => fallback,
        };
    }
}
