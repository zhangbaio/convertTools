using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IProjectInfoParser
{
    Task<ProjectInfo> ParseAsync(string projectDir, CancellationToken cancellationToken);
}
