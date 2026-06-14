using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace ShortDrama.Cli.Commands;

public sealed class BatchFileRenameCommand
{
    private readonly IBatchFileRenamer _batchFileRenamer;
    private readonly ILogger<BatchFileRenameCommand> _logger;

    public BatchFileRenameCommand(
        IBatchFileRenamer batchFileRenamer,
        ILogger<BatchFileRenameCommand> logger)
    {
        _batchFileRenamer = batchFileRenamer;
        _logger = logger;
    }

    public Command Create()
    {
        var batchFileRename = new Command("batch-file-rename", "Batch rename project files");
        var run = new Command("run", "Rename video files using VideoNameTemplate or an explicit template");

        var projectDirOption = new Option<DirectoryInfo>(
            "--project-dir",
            "Project directory")
        {
            IsRequired = true
        };

        var inputDirOption = new Option<DirectoryInfo?>(
            "--input-dir",
            "Optional input directory. Defaults to <project-dir>/videos");

        var configFileOption = new Option<FileInfo?>(
            "--config-file",
            "Optional config.txt path. When set, VideoNameTemplate is read from it.");

        var nameTemplateOption = new Option<string?>(
            "--name-template",
            "Optional file name template. Supports {name} and {index}.");

        var overwriteOption = new Option<bool>(
            "--overwrite",
            "Overwrite destination files if they already exist");

        var jsonOutputOption = new Option<bool>(
            "--json-output",
            "Print machine-readable JSON result");

        run.AddOption(projectDirOption);
        run.AddOption(inputDirOption);
        run.AddOption(configFileOption);
        run.AddOption(nameTemplateOption);
        run.AddOption(overwriteOption);
        run.AddOption(jsonOutputOption);

        run.SetHandler(async (InvocationContext context) =>
        {
            var projectDir = context.ParseResult.GetValueForOption(projectDirOption)!;
            var inputDir = context.ParseResult.GetValueForOption(inputDirOption);
            var configFile = context.ParseResult.GetValueForOption(configFileOption);
            var nameTemplate = context.ParseResult.GetValueForOption(nameTemplateOption);
            var overwrite = context.ParseResult.GetValueForOption(overwriteOption);
            var jsonOutput = context.ParseResult.GetValueForOption(jsonOutputOption);

            context.ExitCode = await ExecuteAsync(
                projectDir.FullName,
                inputDir?.FullName,
                configFile?.FullName,
                nameTemplate,
                overwrite,
                jsonOutput,
                context.GetCancellationToken());
        });

        batchFileRename.AddCommand(run);
        return batchFileRename;
    }

    public async Task<int> ExecuteAsync(
        string projectDir,
        string? inputDir,
        string? configFile,
        string? nameTemplate,
        bool overwrite,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _batchFileRenamer.RenameAsync(
                new BatchFileRenameRequest(
                    projectDir,
                    inputDir ?? Path.Combine(projectDir, "videos"),
                    configFile,
                    nameTemplate,
                    overwrite),
                cancellationToken);

            if (jsonOutput)
            {
                var items = string.Join(",\n", result.Items.Select(item => $$"""
                    {
                      "inputFile": "{{EscapeJson(item.InputFilePath)}}",
                      "outputFile": "{{EscapeJson(item.OutputFilePath)}}"
                    }
                """));

                Console.WriteLine($$"""
                {
                  "ok": true,
                  "renamedCount": {{result.RenamedCount}},
                  "items": [
                {{items}}
                  ]
                }
                """);
            }
            else
            {
                _logger.LogInformation("Batch renamed {Count} files.", result.RenamedCount);
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to batch rename files");

            if (jsonOutput)
            {
                Console.WriteLine($$"""
                {
                  "ok": false,
                  "errorCode": "BATCH_FILE_RENAME_FAILED",
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
