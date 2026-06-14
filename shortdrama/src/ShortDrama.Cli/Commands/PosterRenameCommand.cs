using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace ShortDrama.Cli.Commands;

public sealed class PosterRenameCommand
{
    private readonly IPosterRenamer _posterRenamer;
    private readonly ILogger<PosterRenameCommand> _logger;

    public PosterRenameCommand(
        IPosterRenamer posterRenamer,
        ILogger<PosterRenameCommand> logger)
    {
        _posterRenamer = posterRenamer;
        _logger = logger;
    }

    public Command Create()
    {
        var posterRename = new Command("poster-rename", "Rename poster image files");
        var rename = new Command("run", "Rename project poster to a standardized file name");

        var projectDirOption = new Option<DirectoryInfo>(
            "--project-dir",
            "Project directory")
        {
            IsRequired = true
        };

        var inputFileOption = new Option<FileInfo?>(
            "--input-file",
            "Optional source poster path. Defaults to 海报图片.* or the first poster-like image in project dir");

        var outputFileOption = new Option<FileInfo?>(
            "--output-file",
            "Optional destination poster path");

        var configFileOption = new Option<FileInfo?>(
            "--config-file",
            "Optional config.txt path. Required when --use-ai is enabled.");

        var nameTemplateOption = new Option<string?>(
            "--name-template",
            "Optional file name template. Supports {name}. Default is {name}-海报");

        var useAiOption = new Option<bool>(
            "--use-ai",
            "Use the chat model in config.txt to generate a poster title before renaming");

        var overwriteOption = new Option<bool>(
            "--overwrite",
            "Overwrite destination file if it already exists");

        var jsonOutputOption = new Option<bool>(
            "--json-output",
            "Print machine-readable JSON result");

        rename.AddOption(projectDirOption);
        rename.AddOption(inputFileOption);
        rename.AddOption(outputFileOption);
        rename.AddOption(configFileOption);
        rename.AddOption(nameTemplateOption);
        rename.AddOption(useAiOption);
        rename.AddOption(overwriteOption);
        rename.AddOption(jsonOutputOption);

        rename.SetHandler(async (InvocationContext context) =>
        {
            var projectDir = context.ParseResult.GetValueForOption(projectDirOption)!;
            var inputFile = context.ParseResult.GetValueForOption(inputFileOption);
            var outputFile = context.ParseResult.GetValueForOption(outputFileOption);
            var configFile = context.ParseResult.GetValueForOption(configFileOption);
            var nameTemplate = context.ParseResult.GetValueForOption(nameTemplateOption);
            var useAi = context.ParseResult.GetValueForOption(useAiOption);
            var overwrite = context.ParseResult.GetValueForOption(overwriteOption);
            var jsonOutput = context.ParseResult.GetValueForOption(jsonOutputOption);

            context.ExitCode = await ExecuteAsync(
                projectDir.FullName,
                inputFile?.FullName,
                outputFile?.FullName,
                configFile?.FullName,
                nameTemplate,
                useAi,
                overwrite,
                jsonOutput,
                context.GetCancellationToken());
        });

        posterRename.AddCommand(rename);
        return posterRename;
    }

    public async Task<int> ExecuteAsync(
        string projectDir,
        string? inputFile,
        string? outputFile,
        string? configFile,
        string? nameTemplate,
        bool useAi,
        bool overwrite,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configFile))
            {
                throw new InvalidOperationException("必须提供 --config-file，海报图片现在只走 AI 生成逻辑。");
            }

            var result = await _posterRenamer.RenameAsync(
                new PosterRenameRequest(
                    projectDir,
                    inputFile,
                    outputFile,
                    configFile,
                    nameTemplate,
                    useAi,
                    overwrite),
                cancellationToken);

            if (jsonOutput)
            {
                Console.WriteLine($$"""
                {
                  "ok": true,
                  "inputFile": "{{EscapeJson(result.InputFilePath)}}",
                  "outputFile": "{{EscapeJson(result.OutputFilePath)}}",
                  "posterName": "{{EscapeJson(result.PosterName)}}"
                }
                """);
            }
            else
            {
                _logger.LogInformation("Renamed poster to {Path}", result.OutputFilePath);
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename poster");

            if (jsonOutput)
            {
                Console.WriteLine($$"""
                {
                  "ok": false,
                  "errorCode": "POSTER_RENAME_FAILED",
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
