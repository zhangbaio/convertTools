using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace ShortDrama.Cli.Commands;

public sealed class ProjectResumeCommand
{
    private readonly IWorkService _workService;
    private readonly ILogger<ProjectResumeCommand> _logger;

    public ProjectResumeCommand(
        IWorkService workService,
        ILogger<ProjectResumeCommand> logger)
    {
        _workService = workService;
        _logger = logger;
    }

    public Command Create()
    {
        var project = new Command("project", "Run or resume a single shortdrama project");
        var resume = new Command("resume", "Resume one project from source/workflow/backup state");

        var projectDirOption = new Option<DirectoryInfo>(
            "--project-dir",
            "Source project directory")
        {
            IsRequired = true
        };

        var backupRootDirOption = new Option<DirectoryInfo?>(
            "--backup-root-dir",
            "Optional backup root directory. Defaults to sibling backup directory.");

        var forceOption = new Option<bool>(
            "--force",
            "Re-run the project even if it is already marked completed");

        var jsonOutputOption = new Option<bool>(
            "--json-output",
            "Print machine-readable JSON result");

        resume.AddOption(projectDirOption);
        resume.AddOption(backupRootDirOption);
        resume.AddOption(forceOption);
        resume.AddOption(jsonOutputOption);

        resume.SetHandler(async (InvocationContext context) =>
        {
            var projectDir = context.ParseResult.GetValueForOption(projectDirOption)!;
            var backupRootDir = context.ParseResult.GetValueForOption(backupRootDirOption);
            var force = context.ParseResult.GetValueForOption(forceOption);
            var jsonOutput = context.ParseResult.GetValueForOption(jsonOutputOption);

            context.ExitCode = await ExecuteAsync(
                projectDir.FullName,
                backupRootDir?.FullName,
                force,
                jsonOutput,
                context.GetCancellationToken());
        });

        project.AddCommand(resume);
        return project;
    }

    public async Task<int> ExecuteAsync(
        string sourceProjectDir,
        string? backupRootDir,
        bool force,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _workService.RunProjectAsync(sourceProjectDir, backupRootDir, force, progress: null, cancellationToken);

            if (jsonOutput)
            {
                var steps = string.Join(",\n", result.Steps.Select(step => $$"""
                    {
                      "type": "{{EscapeJson(step.Type)}}",
                      "ok": {{step.Ok.ToString().ToLowerInvariant()}},
                      "errorCode": {{ToNullableJson(step.ErrorCode)}},
                      "message": {{ToNullableJson(step.Message)}}
                    }
                """));

                Console.WriteLine($$"""
                {
                  "ok": {{result.Ok.ToString().ToLowerInvariant()}},
                  "skipped": {{result.Skipped.ToString().ToLowerInvariant()}},
                  "projectKey": "{{EscapeJson(result.ProjectKey)}}",
                  "displayName": "{{EscapeJson(result.DisplayName)}}",
                  "workflowProjectDir": "{{EscapeJson(result.WorkflowProjectDir)}}",
                  "message": {{ToNullableJson(result.Message)}},
                  "steps": [
                {{steps}}
                  ]
                }
                """);
            }
            else
            {
                Console.WriteLine(result.Message ?? (result.Ok ? "处理完成" : "处理失败"));
                Console.WriteLine($"项目: {result.DisplayName}");
                Console.WriteLine($"输出目录: {result.WorkflowProjectDir}");
            }

            return result.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume project");

            if (jsonOutput)
            {
                Console.WriteLine($$"""
                {
                  "ok": false,
                  "errorCode": "PROJECT_RESUME_FAILED",
                  "message": "{{EscapeJson(ex.Message)}}"
                }
                """);
            }

            return 1;
        }
    }

    private static string ToNullableJson(string? value)
    {
        return value is null ? "null" : $"\"{EscapeJson(value)}\"";
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
