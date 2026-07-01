using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IExternalProcessRunner
{
    Task<ExternalProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken);
}
