namespace ShortDrama.Core.Models;

public sealed record ProjectInfo(
    string OriginalTitle,
    string Title,
    string? Tagline,
    string? Synopsis,
    string? ShortTitle,
    string? Tags,
    int EpisodeCount,
    int TotalMinutes,
    decimal CostAmountWan,
    string CompanyName,
    string ProjectDir,
    string SourceFilePath);
