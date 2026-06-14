using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface ICostReportBuilder
{
    Task<CostReportBuildResult> BuildAsync(
        string projectDir,
        string templateDocxPath,
        string? configDir,
        string outputDir,
        CancellationToken cancellationToken);
}
