using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IWorkflowDefinitionLoader
{
    Task<WorkflowDefinition> LoadAsync(string configPath, CancellationToken cancellationToken);
}
