using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace ShortDrama.Cli.Commands;

public sealed class CostReportBuildCommand
{
    private readonly ICostReportBuilder _costReportBuilder;
    private readonly ILogger<CostReportBuildCommand> _logger;

    public CostReportBuildCommand(
        ICostReportBuilder costReportBuilder,
        ILogger<CostReportBuildCommand> logger)
    {
        _costReportBuilder = costReportBuilder;
        _logger = logger;
    }

    public Command Create()
    {
        var costReport = new Command("cost-report", "Build cost report artifacts from a Word template");
        var build = new Command("build", "Generate cost report image from a Word template");

        var projectDirOption = new Option<DirectoryInfo>(
            "--project-dir",
            "Project directory that contains 短剧信息.txt")
        {
            IsRequired = true
        };

        var templateOption = new Option<FileInfo>(
            "--template",
            "Optional path to the cost report .docx template");

        var configDirOption = new Option<DirectoryInfo?>(
            "--config-dir",
            "Optional config directory that contains seal.png and sign.png");

        var outputDirOption = new Option<DirectoryInfo>(
            "--output-dir",
            "Output directory")
        {
            IsRequired = true
        };

        var jsonOutputOption = new Option<bool>(
            "--json-output",
            "Print machine-readable JSON result");

        build.AddOption(projectDirOption);
        build.AddOption(templateOption);
        build.AddOption(configDirOption);
        build.AddOption(outputDirOption);
        build.AddOption(jsonOutputOption);

        build.SetHandler(async (InvocationContext context) =>
        {
            var projectDir = context.ParseResult.GetValueForOption(projectDirOption)!;
            var template = context.ParseResult.GetValueForOption(templateOption);
            var configDir = context.ParseResult.GetValueForOption(configDirOption);
            var outputDir = context.ParseResult.GetValueForOption(outputDirOption)!;
            var jsonOutput = context.ParseResult.GetValueForOption(jsonOutputOption);

            context.ExitCode = await ExecuteAsync(
                projectDir.FullName,
                template?.FullName ?? string.Empty,
                configDir?.FullName,
                outputDir.FullName,
                jsonOutput,
                context.GetCancellationToken());
        });

        costReport.AddCommand(build);
        return costReport;
    }

    public async Task<int> ExecuteAsync(
        string projectDir,
        string templateDocxPath,
        string? configDir,
        string outputDir,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _costReportBuilder.BuildAsync(
                projectDir,
                templateDocxPath,
                configDir,
                outputDir,
                cancellationToken);

            if (jsonOutput)
            {
                WriteSuccessJson(result);
            }
            else
            {
                _logger.LogInformation("Generated png: {Path}", result.PngPath);
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build cost report");

            if (jsonOutput)
            {
                WriteErrorJson("UNHANDLED_ERROR", ex.Message);
            }

            return 1;
        }
    }

    private static void WriteSuccessJson(CostReportBuildResult result)
    {
        Console.WriteLine($$"""
        {
          "ok": true,
          "outputs": {
            "png": "{{EscapeJson(result.PngPath)}}"
          },
          "project": {
            "title": "{{EscapeJson(result.Project.Title)}}",
            "originalTitle": "{{EscapeJson(result.Project.OriginalTitle)}}",
            "episodes": {{result.Project.EpisodeCount}},
            "minutes": {{result.Project.TotalMinutes}},
            "costWan": {{result.Project.CostAmountWan}},
            "company": "{{EscapeJson(result.Project.CompanyName)}}"
          }
        }
        """);
    }

    private static void WriteErrorJson(string errorCode, string message)
    {
        Console.WriteLine($$"""
        {
          "ok": false,
          "errorCode": "{{EscapeJson(errorCode)}}",
          "message": "{{EscapeJson(message)}}"
        }
        """);
    }

    private static string EscapeJson(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
