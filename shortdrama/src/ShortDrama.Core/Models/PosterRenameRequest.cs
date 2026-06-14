namespace ShortDrama.Core.Models;

public sealed record PosterRenameRequest(
    string ProjectDir,
    string? InputFilePath = null,
    string? OutputFilePath = null,
    string? ConfigFile = null,
    string? NameTemplate = null,
    bool UseAi = false,
    bool Overwrite = false);
