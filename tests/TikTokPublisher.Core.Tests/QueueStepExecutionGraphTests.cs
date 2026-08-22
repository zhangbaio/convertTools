using FluentAssertions;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Tests;

public sealed class QueueStepExecutionGraphTests
{
    [Fact]
    public void BuildDependencies_Models_Content_FanOut_And_Proof_Join()
    {
        var dependencies = QueueStepExecutionGraph.BuildDependencies(
        [
            QueueStepRegistry.GenerateEpisodeScript,
            QueueStepRegistry.GenerateAiDramaMaterials,
            QueueStepRegistry.GenerateAiScriptOutline,
            QueueStepRegistry.GenerateRoleVector,
            QueueStepRegistry.GenerateProofMaterial,
            QueueStepRegistry.GenerateTimestampCertificate,
        ]);

        dependencies[QueueStepRegistry.GenerateEpisodeScript].Should().BeEmpty();
        dependencies[QueueStepRegistry.GenerateAiDramaMaterials].Should().BeEmpty();
        dependencies[QueueStepRegistry.GenerateAiScriptOutline].Should().BeEmpty();
        dependencies[QueueStepRegistry.GenerateTimestampCertificate].Should().BeEmpty();
        dependencies[QueueStepRegistry.GenerateRoleVector]
            .Should().Equal(QueueStepRegistry.GenerateAiDramaMaterials);
        dependencies[QueueStepRegistry.GenerateProofMaterial].Should().Equal(
            QueueStepRegistry.GenerateEpisodeScript,
            QueueStepRegistry.GenerateAiDramaMaterials,
            QueueStepRegistry.GenerateAiScriptOutline,
            QueueStepRegistry.GenerateRoleVector);
    }

    [Fact]
    public async Task AiTextResourceScheduler_Limits_Concurrency_Across_Projects()
    {
        var running = 0;
        var maximum = 0;
        var tasks = Enumerable.Range(0, 9).Select(index =>
            QueueStepResourceScheduler.RunAsync(
                index % 2 == 0
                    ? QueueStepRegistry.GenerateEpisodeScript
                    : QueueStepRegistry.GenerateAiScriptOutline,
                async () =>
                {
                    var current = Interlocked.Increment(ref running);
                    UpdateMaximum(current);
                    try
                    {
                        await Task.Delay(40);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref running);
                    }
                },
                log: null,
                CancellationToken.None));

        await Task.WhenAll(tasks);

        maximum.Should().Be(3);

        void UpdateMaximum(int value)
        {
            var observed = Volatile.Read(ref maximum);
            while (value > observed)
            {
                var previous = Interlocked.CompareExchange(ref maximum, value, observed);
                if (previous == observed) return;
                observed = previous;
            }
        }
    }
}
