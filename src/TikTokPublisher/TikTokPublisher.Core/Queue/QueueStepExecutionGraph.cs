using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Queue;

internal static class QueueStepExecutionGraph
{
    private static readonly HashSet<string> ParallelGenerationSteps =
    [
        QueueStepRegistry.GenerateEpisodeScript,
        QueueStepRegistry.GenerateAiDramaMaterials,
        QueueStepRegistry.GenerateAiScriptOutline,
        QueueStepRegistry.GenerateRoleVector,
        QueueStepRegistry.GenerateProjectImages,
        QueueStepRegistry.GenerateProofMaterial,
        QueueStepRegistry.GenerateTimestampCertificate,
    ];

    public static bool IsParallelGenerationStep(string stepKey) =>
        ParallelGenerationSteps.Contains(stepKey);

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildDependencies(
        IEnumerable<string> enabledSteps)
    {
        var selected = enabledSteps
            .Where(IsParallelGenerationStep)
            .ToHashSet(StringComparer.Ordinal);
        var result = selected.ToDictionary(
            step => step,
            _ => (IReadOnlyList<string>)Array.Empty<string>(),
            StringComparer.Ordinal);

        SetDependencies(QueueStepRegistry.GenerateRoleVector,
            QueueStepRegistry.GenerateAiDramaMaterials);
        SetDependencies(QueueStepRegistry.GenerateProjectImages,
            QueueStepRegistry.GenerateAiDramaMaterials,
            QueueStepRegistry.GenerateRoleVector);
        SetDependencies(QueueStepRegistry.GenerateProofMaterial,
            QueueStepRegistry.GenerateEpisodeScript,
            QueueStepRegistry.GenerateAiDramaMaterials,
            QueueStepRegistry.GenerateAiScriptOutline,
            QueueStepRegistry.GenerateRoleVector,
            QueueStepRegistry.GenerateProjectImages);
        return result;

        void SetDependencies(string step, params string[] dependencies)
        {
            if (!selected.Contains(step)) return;
            result[step] = dependencies.Where(selected.Contains).ToArray();
        }
    }
}

internal static class QueueStepResourceScheduler
{
    public static async Task RunAsync(
        string stepKey,
        Func<Task> action,
        Action<string>? log,
        CancellationToken ct)
    {
        var resource = ResolveResource(stepKey);
        if (resource is null)
        {
            await action().ConfigureAwait(false);
            return;
        }
        await QueueWorkloadResourceScheduler.RunAsync(resource.Value, action, log, ct)
            .ConfigureAwait(false);
    }

    private static QueueWorkloadResource? ResolveResource(string stepKey) => stepKey switch
    {
        QueueStepRegistry.GenerateRoleVector or
            QueueStepRegistry.GenerateProjectImages =>
            QueueWorkloadResource.Visual,
        QueueStepRegistry.GenerateTimestampCertificate =>
            QueueWorkloadResource.Document,
        _ => null,
    };
}

internal enum QueueWorkloadResource
{
    AiText,
    Asr,
    Ffmpeg,
    ImageGeneration,
    Visual,
    Document,
}

internal static class QueueWorkloadResourceScheduler
{
    private static readonly object ConfigurationLock = new();
    private static readonly object ThrottleLock = new();
    private static IReadOnlyDictionary<QueueWorkloadResource, SemaphoreSlim> _gates = CreateGates(
        aiText: 3, asr: 2, ffmpeg: 2, image: 2, visual: 2, document: 2);
    private static string _configurationSignature = "3|2|2|2|2|2";
    private static readonly Dictionary<QueueWorkloadResource, (int Failures, DateTimeOffset BlockedUntil)>
        ThrottleStates = new();

    public static void Configure(ClientSettings settings)
    {
        var limits = new[]
        {
            Math.Clamp(settings.TiktokAiTextConcurrency, 1, 12),
            Math.Clamp(settings.TiktokAsrConcurrency, 1, 8),
            Math.Clamp(settings.TiktokFfmpegConcurrency, 1, 8),
            Math.Clamp(settings.TiktokImageGenerationConcurrency, 1, 8),
            Math.Clamp(settings.TiktokVisualConcurrency, 1, 8),
            Math.Clamp(settings.TiktokDocumentConcurrency, 1, 8),
        };
        var signature = string.Join('|', limits);
        lock (ConfigurationLock)
        {
            if (string.Equals(signature, _configurationSignature, StringComparison.Ordinal)) return;
            _gates = CreateGates(limits[0], limits[1], limits[2], limits[3], limits[4], limits[5]);
            _configurationSignature = signature;
        }
    }

    public static async Task RunAsync(
        QueueWorkloadResource resource,
        Func<Task> action,
        Action<string>? log,
        CancellationToken ct)
    {
        await WaitForThrottleBackoffAsync(resource, log, ct).ConfigureAwait(false);
        SemaphoreSlim gate;
        lock (ConfigurationLock)
            gate = _gates[resource];
        var acquired = gate.Wait(0);
        if (!acquired)
        {
            log?.Invoke($"等待全局{LabelOf(resource)}并发槽…");
            var waitStarted = DateTimeOffset.UtcNow;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            var waited = DateTimeOffset.UtcNow - waitStarted;
            log?.Invoke($"已获得全局{LabelOf(resource)}并发槽，等待 {waited.TotalSeconds:0.00} 秒。");
        }

        try
        {
            await action().ConfigureAwait(false);
            RegisterSuccess(resource);
        }
        catch (Exception ex) when (IsThrottleFailure(ex))
        {
            RegisterThrottleFailure(resource, log);
            throw;
        }
        finally
        {
            if (acquired) gate.Release();
        }
    }

    public static async Task<T> RunAsync<T>(
        QueueWorkloadResource resource,
        Func<Task<T>> action,
        Action<string>? log,
        CancellationToken ct)
    {
        T? result = default;
        await RunAsync(
            resource,
            async () =>
            {
                result = await action().ConfigureAwait(false);
            },
            log,
            ct).ConfigureAwait(false);
        return result!;
    }

    private static string LabelOf(QueueWorkloadResource resource) => resource switch
    {
        QueueWorkloadResource.AiText => "AI 文本",
        QueueWorkloadResource.Asr => "ASR",
        QueueWorkloadResource.Ffmpeg => "FFmpeg",
        QueueWorkloadResource.ImageGeneration => "AI 图片",
        QueueWorkloadResource.Visual => "视觉处理",
        QueueWorkloadResource.Document => "文档处理",
        _ => resource.ToString(),
    };

    private static IReadOnlyDictionary<QueueWorkloadResource, SemaphoreSlim> CreateGates(
        int aiText,
        int asr,
        int ffmpeg,
        int image,
        int visual,
        int document) => new Dictionary<QueueWorkloadResource, SemaphoreSlim>
    {
        [QueueWorkloadResource.AiText] = new(aiText, aiText),
        [QueueWorkloadResource.Asr] = new(asr, asr),
        [QueueWorkloadResource.Ffmpeg] = new(ffmpeg, ffmpeg),
        [QueueWorkloadResource.ImageGeneration] = new(image, image),
        [QueueWorkloadResource.Visual] = new(visual, visual),
        [QueueWorkloadResource.Document] = new(document, document),
    };

    private static async Task WaitForThrottleBackoffAsync(
        QueueWorkloadResource resource,
        Action<string>? log,
        CancellationToken ct)
    {
        TimeSpan delay;
        lock (ThrottleLock)
        {
            delay = ThrottleStates.TryGetValue(resource, out var state)
                ? state.BlockedUntil - DateTimeOffset.UtcNow
                : TimeSpan.Zero;
        }
        if (delay <= TimeSpan.Zero) return;
        log?.Invoke($"全局{LabelOf(resource)}检测到限流，退避 {Math.Ceiling(delay.TotalSeconds)} 秒…");
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private static void RegisterThrottleFailure(QueueWorkloadResource resource, Action<string>? log)
    {
        lock (ThrottleLock)
        {
            var failures = ThrottleStates.TryGetValue(resource, out var state)
                ? Math.Min(state.Failures + 1, 5)
                : 1;
            var seconds = Math.Min(30, 1 << failures);
            ThrottleStates[resource] = (failures, DateTimeOffset.UtcNow.AddSeconds(seconds));
            log?.Invoke($"全局{LabelOf(resource)}触发限流，后续任务退避 {seconds} 秒。");
        }
    }

    private static void RegisterSuccess(QueueWorkloadResource resource)
    {
        lock (ThrottleLock)
        {
            if (!ThrottleStates.TryGetValue(resource, out var state)) return;
            var failures = Math.Max(0, state.Failures - 1);
            if (failures == 0)
                ThrottleStates.Remove(resource);
            else
                ThrottleStates[resource] = (failures, state.BlockedUntil);
        }
    }

    private static bool IsThrottleFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message ?? "";
            if (message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("限流", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
