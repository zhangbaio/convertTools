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
    private static readonly SemaphoreSlim AiTextSlots = new(3, 3);
    private static readonly SemaphoreSlim VisualSlots = new(2, 2);
    private static readonly SemaphoreSlim DocumentSlots = new(2, 2);

    public static async Task RunAsync(
        string stepKey,
        Func<Task> action,
        Action<string>? log,
        CancellationToken ct)
    {
        var (gate, label) = ResolveGate(stepKey);
        if (gate is null)
        {
            await action().ConfigureAwait(false);
            return;
        }

        var acquired = gate.Wait(0);
        if (!acquired)
        {
            log?.Invoke($"等待全局{label}并发槽…");
            await gate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
        }

        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            if (acquired) gate.Release();
        }
    }

    private static (SemaphoreSlim? Gate, string Label) ResolveGate(string stepKey) => stepKey switch
    {
        QueueStepRegistry.GenerateEpisodeScript or QueueStepRegistry.GenerateAiScriptOutline =>
            (AiTextSlots, "AI 文本"),
        QueueStepRegistry.GenerateAiDramaMaterials or QueueStepRegistry.GenerateRoleVector or
            QueueStepRegistry.GenerateProjectImages =>
            (VisualSlots, "视觉处理"),
        QueueStepRegistry.GenerateProofMaterial or QueueStepRegistry.GenerateTimestampCertificate =>
            (DocumentSlots, "文档处理"),
        _ => (null, ""),
    };
}
