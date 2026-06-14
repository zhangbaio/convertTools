using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace ShortDrama.Cli.Commands;

public sealed class ProjectInfoRewriteCommand
{
    private readonly IProjectInfoRewriter _projectInfoRewriter;
    private readonly ILogger<ProjectInfoRewriteCommand> _logger;

    public ProjectInfoRewriteCommand(
        IProjectInfoRewriter projectInfoRewriter,
        ILogger<ProjectInfoRewriteCommand> logger)
    {
        _projectInfoRewriter = projectInfoRewriter;
        _logger = logger;
    }

    public Command Create()
    {
        var rewrite = new Command("rewrite", "Rewrite shortdrama project info with an AI chat model");
        var projectInfo = new Command("project-info", "Rewrite 推荐语 and 简介 based on 短剧信息.txt");

        var projectDirOption = new Option<DirectoryInfo>(
            "--project-dir",
            "Project directory")
        {
            IsRequired = true
        };

        var configFileOption = new Option<FileInfo>(
            "--config-file",
            "config.txt path with ChatModel settings")
        {
            IsRequired = true
        };

        var outputFileOption = new Option<FileInfo?>(
            "--output-file",
            "Optional output file path. Defaults to <project-dir>/短剧信息_改写.txt");

        var overwriteOption = new Option<bool>(
            "--overwrite",
            "Overwrite output file if it already exists");

        var jsonOutputOption = new Option<bool>(
            "--json-output",
            "Print machine-readable JSON result");

        projectInfo.AddOption(projectDirOption);
        projectInfo.AddOption(configFileOption);
        projectInfo.AddOption(outputFileOption);
        projectInfo.AddOption(overwriteOption);
        projectInfo.AddOption(jsonOutputOption);

        projectInfo.SetHandler(async (InvocationContext context) =>
        {
            var projectDir = context.ParseResult.GetValueForOption(projectDirOption)!;
            var configFile = context.ParseResult.GetValueForOption(configFileOption)!;
            var outputFile = context.ParseResult.GetValueForOption(outputFileOption);
            var overwrite = context.ParseResult.GetValueForOption(overwriteOption);
            var jsonOutput = context.ParseResult.GetValueForOption(jsonOutputOption);

            context.ExitCode = await ExecuteAsync(
                projectDir.FullName,
                configFile.FullName,
                outputFile?.FullName,
                overwrite,
                jsonOutput,
                context.GetCancellationToken());
        });

        rewrite.AddCommand(projectInfo);
        return rewrite;
    }

    public async Task<int> ExecuteAsync(
        string projectDir,
        string configFile,
        string? outputFile,
        bool overwrite,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _projectInfoRewriter.RewriteAsync(
                new ProjectInfoRewriteRequest(
                    projectDir,
                    configFile,
                    outputFile ?? Path.Combine(projectDir, "短剧信息_改写.txt"),
                    overwrite),
                cancellationToken);

            if (jsonOutput)
            {
                Console.WriteLine($$"""
                {
                  "ok": true,
                  "outputFile": "{{EscapeJson(result.OutputFilePath)}}",
                  "title": "{{EscapeJson(result.Title)}}",
                  "tagline": "{{EscapeJson(result.Tagline)}}",
                  "synopsis": "{{EscapeJson(result.Synopsis)}}"
                }
                """);
            }
            else
            {
                _logger.LogInformation("Rewrote project info to {Path}", result.OutputFilePath);
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rewrite project info");

            if (jsonOutput)
            {
                Console.WriteLine($$"""
                {
                  "ok": false,
                  "errorCode": "REWRITE_FAILED",
                  "message": "{{EscapeJson(ex.Message)}}"
                }
                """);
            }

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
