using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface ICostReportTemplateService
{
    Task<string> BuildDocxAsync(CostReportBuildRequest request, CancellationToken cancellationToken);
}
