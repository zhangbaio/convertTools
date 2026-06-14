using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Config;
using ShortDrama.Infrastructure.Imaging;

namespace ShortDrama.Infrastructure.Workflow;

public sealed class CostReportBuilder : ICostReportBuilder
{
    private readonly IProjectInfoParser _projectInfoParser;
    private readonly IConfigLocator _configLocator;
    private readonly ICostReportTemplateService _templateService;

    public CostReportBuilder(
        IProjectInfoParser projectInfoParser,
        IConfigLocator configLocator,
        ICostReportTemplateService templateService)
    {
        _projectInfoParser = projectInfoParser;
        _configLocator = configLocator;
        _templateService = templateService;
    }

    public async Task<CostReportBuildResult> BuildAsync(
        string projectDir,
        string templateDocxPath,
        string? configDir,
        string outputDir,
        CancellationToken cancellationToken)
    {
        var project = await _projectInfoParser.ParseAsync(projectDir, cancellationToken);
        var resolvedConfigDir = configDir
            ?? await _configLocator.FindConfigDirAsync(projectDir, cancellationToken);
        var resolvedProject = await OverrideCompanyNameFromConfigAsync(project, resolvedConfigDir, cancellationToken);

        Directory.CreateDirectory(outputDir);

        var request = new CostReportBuildRequest(
            TemplateDocxPath: templateDocxPath,
            ProjectDir: projectDir,
            ConfigDir: resolvedConfigDir,
            OutputDir: outputDir,
            Project: resolvedProject);

        var docxPath = string.Empty;
        var pngPath = Path.Combine(outputDir, "成本报表.png");

        await CostReportImageRenderer.RenderAsync(request, pngPath, cancellationToken);

        if (ShouldGenerateOriginalDocx(resolvedConfigDir))
        {
            docxPath = await _templateService.BuildDocxAsync(request, cancellationToken);
        }

        return new CostReportBuildResult(pngPath, docxPath, resolvedProject);
    }

    private static async Task<ProjectInfo> OverrideCompanyNameFromConfigAsync(
        ProjectInfo project,
        string resolvedConfigDir,
        CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(resolvedConfigDir, "config.txt");
        if (!File.Exists(configPath))
        {
            return project;
        }

        var map = await Task.Run(() => KeyValueConfigReader.Read(configPath), cancellationToken);
        if (!map.TryGetValue("CompanyName", out var companyName) || string.IsNullOrWhiteSpace(companyName))
        {
            return project;
        }

        return project with
        {
            CompanyName = companyName.Trim()
        };
    }

    private static bool ShouldGenerateOriginalDocx(string resolvedConfigDir)
    {
        var configPath = Path.Combine(resolvedConfigDir, "config.txt");
        if (!File.Exists(configPath))
        {
            return false;
        }

        try
        {
            var map = KeyValueConfigReader.Read(configPath);
            foreach (var key in new[] { "GenerateCostReportOriginalDocx", "GenerateCostReportDocx", "CostReportGenerateDocx" })
            {
                if (!map.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                return raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ||
                       raw.Trim().Equals("1", StringComparison.OrdinalIgnoreCase) ||
                       raw.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                       raw.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Fall back to default false.
        }

        return false;
    }
}
