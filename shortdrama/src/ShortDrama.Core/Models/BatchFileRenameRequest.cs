namespace ShortDrama.Core.Models;

public sealed record BatchFileRenameRequest(
    string ProjectDir,
    string InputDir,
    string? ConfigFile = null,
    string? NameTemplate = null,
    bool Overwrite = false);
