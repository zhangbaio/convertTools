namespace ShortDrama.Core.Models;

public sealed record ProjectInfoRewriteResult(
    string OutputFilePath,
    string Title,
    string Tagline,
    string Synopsis,
    string ShortTitle,
    string Tags);
