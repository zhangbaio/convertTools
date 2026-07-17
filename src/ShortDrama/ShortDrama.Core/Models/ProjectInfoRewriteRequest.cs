namespace ShortDrama.Core.Models;

public sealed record ProjectInfoRewriteRequest(
    string ProjectDir,
    string ConfigFile,
    string OutputFilePath,
    bool Overwrite = false,
    IReadOnlyList<string>? ForbiddenTitles = null,
    IReadOnlyList<string>? ForbiddenSynopses = null,
    int TargetSynopsisLength = 0,
    string RewriteVariantKey = "");
