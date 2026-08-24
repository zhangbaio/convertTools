using System.Collections.Concurrent;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

internal static class TikTokVisualEvidencePreparationService
{
    private static readonly ConcurrentDictionary<
        string,
        Lazy<Task<IReadOnlyList<string>>>> ActivePreparations =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<string>> EnsureCurrentAsync(
        string workflowProjectDirectory,
        string dramaTitle,
        ClientSettings settings,
        Action<string>? log,
        CancellationToken ct,
        int minimumRetainedFrameCount = 0)
    {
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        if (HasCurrentOutput(workflow, minimumRetainedFrameCount))
            return TikTokAiGenerationScreenshotService.ListGeneratedImages(workflow);

        var lazy = ActivePreparations.GetOrAdd(
            workflow,
            _ => new Lazy<Task<IReadOnlyList<string>>>(
                () => GenerateAsync(workflow, dramaTitle, settings, log, ct),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
                ActivePreparations.TryRemove(workflow, out _);
        }
    }

    internal static bool HasCurrentOutput(string workflowProjectDirectory, int minimumRetainedFrameCount)
    {
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        if (!TikTokAiGenerationScreenshotService.HasCurrentOutput(workflow))
            return false;

        var minimum = Math.Max(0, minimumRetainedFrameCount);
        return minimum == 0 ||
               TikTokAiGenerationScreenshotService.ListRetainedFrameImages(workflow).Take(minimum).Count() >= minimum;
    }

    private static Task<IReadOnlyList<string>> GenerateAsync(
        string workflow,
        string dramaTitle,
        ClientSettings settings,
        Action<string>? log,
        CancellationToken ct) =>
        QueueWorkloadResourceScheduler.RunAsync(
            QueueWorkloadResource.Visual,
            () => Task.Run(
                () => TikTokAiGenerationScreenshotService.Generate(
                    workflow,
                    dramaTitle,
                    settings,
                    log,
                    ct),
                ct),
            log,
            ct);
}
