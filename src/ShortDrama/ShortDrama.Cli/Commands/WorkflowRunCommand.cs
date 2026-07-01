using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace ShortDrama.Cli.Commands;

public sealed class WorkflowRunCommand
{
    private readonly IWorkflowDefinitionLoader _definitionLoader;
    private readonly IWorkflowRunner _workflowRunner;
    private readonly ILogger<WorkflowRunCommand> _logger;

    public WorkflowRunCommand(
        IWorkflowDefinitionLoader definitionLoader,
        IWorkflowRunner workflowRunner,
        ILogger<WorkflowRunCommand> logger)
    {
        _definitionLoader = definitionLoader;
        _workflowRunner = workflowRunner;
        _logger = logger;
    }

    public Command Create()
    {
        var workflow = new Command("workflow", "Run a declarative shortdrama workflow");
        var run = new Command("run", "Run a workflow definition file");

        var configOption = new Option<FileInfo>(
            "--config",
            "Workflow definition JSON path")
        {
            IsRequired = true
        };

        var jsonOutputOption = new Option<bool>(
            "--json-output",
            "Print machine-readable JSON result");

        run.AddOption(configOption);
        run.AddOption(jsonOutputOption);

        run.SetHandler(async (InvocationContext context) =>
        {
            var config = context.ParseResult.GetValueForOption(configOption)!;
            var jsonOutput = context.ParseResult.GetValueForOption(jsonOutputOption);

            context.ExitCode = await ExecuteAsync(
                config.FullName,
                jsonOutput,
                context.GetCancellationToken());
        });

        workflow.AddCommand(run);
        return workflow;
    }

    public async Task<int> ExecuteAsync(
        string configPath,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        try
        {
            var definition = await _definitionLoader.LoadAsync(configPath, cancellationToken);
            var result = await _workflowRunner.RunAsync(definition, progress: null, cancellationToken);

            if (jsonOutput)
            {
                WriteResultJson(result);
            }
            else
            {
                _logger.LogInformation("Workflow completed. ok={Ok}, steps={Count}", result.Ok, result.Steps.Count);
            }

            return result.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run workflow");

            if (jsonOutput)
            {
                Console.WriteLine($$"""
                {
                  "ok": false,
                  "errorCode": "WORKFLOW_FAILED",
                  "message": "{{EscapeJson(ex.Message)}}"
                }
                """);
            }

            return 1;
        }
    }

    private static void WriteResultJson(WorkflowRunResult result)
    {
        var stepLines = result.Steps.Select(step => $$"""
            {
              "type": "{{EscapeJson(step.Type)}}",
              "ok": {{step.Ok.ToString().ToLowerInvariant()}},
              "errorCode": {{AsJsonStringOrNull(step.ErrorCode)}},
              "message": {{AsJsonStringOrNull(step.Message)}}
            }
        """);

        Console.WriteLine($$"""
        {
          "ok": {{result.Ok.ToString().ToLowerInvariant()}},
          "steps": [
        {{string.Join(",\n", stepLines)}}
          ]
        }
        """);
    }

    private static string AsJsonStringOrNull(string? value)
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
