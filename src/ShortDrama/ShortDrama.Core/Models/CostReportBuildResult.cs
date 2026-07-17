namespace ShortDrama.Core.Models;

public sealed record CostReportBuildResult(
    string PngPath,
    string DocxPath,
    ProjectInfo Project);
