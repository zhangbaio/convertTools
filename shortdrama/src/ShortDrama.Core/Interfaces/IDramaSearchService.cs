using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IDramaSearchService
{
    Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        string keyword,
        int page,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DramaSearchItem>> GetTodayAsync(
        CancellationToken cancellationToken);
}
