namespace ShortDrama.Core.Models;

public sealed record BatchFileRenameResult(
    int RenamedCount,
    IReadOnlyList<BatchFileRenameItem> Items);

public sealed record BatchFileRenameItem(
    string InputFilePath,
    string OutputFilePath);
