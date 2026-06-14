using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IWorkflowRunner
{
    Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition definition,
        IProgress<WorkflowRuntimeEvent>? progress,
        CancellationToken cancellationToken);
}
