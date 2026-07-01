using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace ShortDrama.Cli.Commands;

public sealed class ParseInfoCommand
{
    private readonly IProjectInfoParser _projectInfoParser;
    private readonly ILogger<ParseInfoCommand> _logger;

    public ParseInfoCommand(
        IProjectInfoParser projectInfoParser,
        ILogger<ParseInfoCommand> logger)
    {
        _projectInfoParser = projectInfoParser;
        _logger = logger;
    }

    public Command Create()
    {
        var command = new Command("parse-info", "Parse 短剧信息.txt and print normalized project data");

        var projectDirOption = new Option<DirectoryInfo>(
            "--project-dir",
            "Project directory that contains 短剧信息.txt")
        {
            IsRequired = true
        };

        command.AddOption(projectDirOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            var projectDir = context.ParseResult.GetValueForOption(projectDirOption)!;
            context.ExitCode = await ExecuteAsync(projectDir.FullName, context.GetCancellationToken());
        });

        return command;
    }

    public async Task<int> ExecuteAsync(string projectDir, CancellationToken cancellationToken)
    {
        try
        {
            var project = await _projectInfoParser.ParseAsync(projectDir, cancellationToken);

            Console.WriteLine($$"""
            {
              "title": "{{EscapeJson(project.Title)}}",
              "originalTitle": "{{EscapeJson(project.OriginalTitle)}}",
              "episodes": {{project.EpisodeCount}},
              "minutes": {{project.TotalMinutes}},
              "costWan": {{project.CostAmountWan}},
              "company": "{{EscapeJson(project.CompanyName)}}"
            }
            """);

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse project info");
            return 1;
        }
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
