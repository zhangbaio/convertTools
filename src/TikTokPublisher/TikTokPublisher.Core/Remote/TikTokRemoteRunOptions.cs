using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Remote;

public static class TikTokRemoteRunOptions
{
    public static QueueRunOptions BuildFeishuTikTokUploadRunOptions(
        ClientSettings settings,
        TikTokRemoteCommand? command = null)
    {
        var commandSteps = command?.EnabledSteps is { Count: > 0 }
            ? command.EnabledSteps
            : null;
        var enabledSteps = commandSteps ?? LoadFeishuTikTokUploadEnabledSteps(settings);
        var queueOptions = command?.QueueOptions;

        return new QueueRunOptions
        {
            EnabledSteps = NormalizeSteps(enabledSteps).ToList(),
            AutoArchiveAfterUpload = RemoteOptionBool(
                queueOptions,
                "auto_archive_after_upload",
                settings.FeishuTiktokUploadAutoArchiveAfterUpload),
            ForceRerunCompletedSteps = RemoteOptionBool(
                queueOptions,
                "force_rerun_completed_steps",
                settings.FeishuTiktokUploadForceRerunCompletedSteps),
            PreferUploadWhenReady = RemoteOptionBool(
                queueOptions,
                "prefer_upload_when_ready",
                settings.FeishuTiktokUploadPreferUploadWhenReady),
            SyncManagementAfterUpload =
                RemoteOptionBool(queueOptions, "sync_management_on_upload_success", false) ||
                RemoteOptionBool(queueOptions, "sync_management_after_upload", false),
            ProjectConcurrency = 4,
        };
    }

    public static IReadOnlyList<string> LoadFeishuTikTokUploadEnabledSteps(ClientSettings settings)
    {
        var raw = (settings.FeishuTiktokUploadEnabledStepsJson ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return TikTokRemoteCommandStepDefaults.FullUploadDefaultEnabledSteps;

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return TikTokRemoteCommandStepDefaults.FullUploadDefaultEnabledSteps;

            var normalized = TikTokRemoteCommandParser.NormalizeEnabledSteps(document.RootElement);
            return normalized.Count > 0
                ? normalized
                : TikTokRemoteCommandStepDefaults.FullUploadDefaultEnabledSteps;
        }
        catch
        {
            return TikTokRemoteCommandStepDefaults.FullUploadDefaultEnabledSteps;
        }
    }

    public static string DumpFeishuTikTokUploadEnabledSteps(IEnumerable<string>? enabledSteps)
    {
        var normalized = NormalizeSteps(enabledSteps).ToList();
        return JsonSerializer.Serialize(normalized);
    }

    private static IEnumerable<string> NormalizeSteps(IEnumerable<string>? enabledSteps)
    {
        var known = QueueStepRegistry.All.Select(step => step.Key).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var steps = enabledSteps?
            .Select(step => (step ?? "").Trim())
            .Where(step => step.Length > 0 && known.Contains(step) && seen.Add(step))
            .ToList() ?? [];
        return QueueStepRegistry.OrderEnabledSteps(steps);
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
