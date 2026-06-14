namespace ShortDrama.Core.Models;

public sealed record DramaSearchItem(
    string BookId,
    string Title,
    string Category,
    int EpisodeTotal,
    string Intro,
    string PosterUrl);
