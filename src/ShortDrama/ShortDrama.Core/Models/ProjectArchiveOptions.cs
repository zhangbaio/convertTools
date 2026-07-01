namespace ShortDrama.Core.Models;

public sealed record ProjectArchiveOptions(
    IReadOnlyCollection<int>? PreserveWorkflowVideoEpisodes = null);
