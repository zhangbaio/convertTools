namespace ShortDrama.Core.Models;

public sealed record PosterRenameResult(
    string InputFilePath,
    string OutputFilePath,
    string PosterName);
