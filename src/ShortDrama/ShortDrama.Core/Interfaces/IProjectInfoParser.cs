using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IProjectInfoParser
{
    Task<ProjectInfo> ParseAsync(string projectDir, CancellationToken cancellationToken);

    async Task<PosterProjectInfo> ParsePosterAsync(string projectDir, CancellationToken cancellationToken)
    {
        var project = await ParseAsync(projectDir, cancellationToken);
        return new PosterProjectInfo(project.OriginalTitle, project.Title, project.Tagline, project.Synopsis);
    }
}
