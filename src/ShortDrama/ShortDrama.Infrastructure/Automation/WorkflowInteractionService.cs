using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation;

public sealed class WorkflowInteractionService : IWorkflowInteractionService
{
    private readonly object _sync = new();
    private TaskCompletionSource<string>? _pendingDecision;

    public WorkflowInteractionRequest? CurrentRequest { get; private set; }

    public event Action<WorkflowInteractionRequest?>? RequestChanged;

    public Task<string> RequestDecisionAsync(
        WorkflowInteractionRequest request,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<string> tcs;
        lock (_sync)
        {
            _pendingDecision?.TrySetCanceled();
            _pendingDecision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            CurrentRequest = request;
            tcs = _pendingDecision;
        }

        RequestChanged?.Invoke(request);
        cancellationToken.Register(() =>
        {
            lock (_sync)
            {
                _pendingDecision?.TrySetCanceled(cancellationToken);
            }
        });

        return tcs.Task;
    }

    public bool TryResolve(string decision)
    {
        TaskCompletionSource<string>? tcs;
        lock (_sync)
        {
            if (_pendingDecision is null)
            {
                return false;
            }

            tcs = _pendingDecision;
            _pendingDecision = null;
            CurrentRequest = null;
        }

        RequestChanged?.Invoke(null);
        return tcs.TrySetResult(decision);
    }

    public void Clear()
    {
        TaskCompletionSource<string>? tcs;
        lock (_sync)
        {
            tcs = _pendingDecision;
            _pendingDecision = null;
            CurrentRequest = null;
        }

        RequestChanged?.Invoke(null);
        tcs?.TrySetCanceled();
    }
}
