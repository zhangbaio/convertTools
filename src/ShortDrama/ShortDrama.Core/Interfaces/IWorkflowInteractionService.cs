using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IWorkflowInteractionService
{
    WorkflowInteractionRequest? CurrentRequest { get; }

    event Action<WorkflowInteractionRequest?>? RequestChanged;

    Task<string> RequestDecisionAsync(
        WorkflowInteractionRequest request,
        CancellationToken cancellationToken);

    bool TryResolve(string decision);

    void Clear();
}
