namespace ShortDrama.Core.Models;

public sealed record CostReportBuildRequest(
    string TemplateDocxPath,
    string ProjectDir,
    string ConfigDir,
    string OutputDir,
    ProjectInfo Project);
