namespace ShortDrama.Core.Models;

public sealed record ProjectInfoRewriteRequest(
    string ProjectDir,
    string ConfigFile,
    string OutputFilePath,
    bool Overwrite = false);
