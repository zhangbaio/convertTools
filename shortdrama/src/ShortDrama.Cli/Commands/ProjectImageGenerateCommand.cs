using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace ShortDrama.Cli.Commands;

public sealed class ProjectImageGenerateCommand
{
    private readonly IProjectImageGenerator _projectImageGenerator;
    private readonly ILogger<ProjectImageGenerateCommand> _logger;

    public ProjectImageGenerateCommand(
        IProjectImageGenerator projectImageGenerator,
        ILogger<ProjectImageGenerateCommand> logger)
    {
        _projectImageGenerator = projectImageGenerator;
        _logger = logger;
    }

    public Command Create()
    {
        var projectImage = new Command("project-image", "Generate editor-like project images");
        var generate = new Command("generate", "Generate 工程图 PNG files from source videos");

        var projectDirOption = new Option<DirectoryInfo>(
            "--project-dir",
            "Project directory")
        {
            IsRequired = true
        };

        var inputDirOption = new Option<DirectoryInfo?>(
            "--input-dir",
            "Optional input video directory. Defaults to <project-dir>/videos");

        var outputDirOption = new Option<DirectoryInfo?>(
            "--output-dir",
            "Optional output directory. Defaults to <project-dir>");

        var templateImageOption = new Option<DirectoryInfo?>(
            "--template-dir",
            "Directory containing 工程图_N.png template files. Defaults to ProjectImageTemplateDir in config.");

        var configFileOption = new Option<FileInfo?>(
            "--config-file",
            "Optional config.txt path. When set, ProjectImageCount is loaded from it.");

        var countOption = new Option<int?>(
            "--count",
            "Optional override for 工程图 count");

        var overwriteOption = new Option<bool>(
            "--overwrite",
            "Overwrite existing 工程图 outputs");

        var jsonOutputOption = new Option<bool>(
            "--json-output",
            "Print machine-readable JSON result");

        generate.AddOption(projectDirOption);
        generate.AddOption(inputDirOption);
        generate.AddOption(outputDirOption);
        generate.AddOption(templateImageOption);
        generate.AddOption(configFileOption);
        generate.AddOption(countOption);
        generate.AddOption(overwriteOption);
        generate.AddOption(jsonOutputOption);

        generate.SetHandler(async (InvocationContext context) =>
        {
            var projectDir = context.ParseResult.GetValueForOption(projectDirOption)!;
            var inputDir = context.ParseResult.GetValueForOption(inputDirOption);
            var outputDir = context.ParseResult.GetValueForOption(outputDirOption);
            var templateImage = context.ParseResult.GetValueForOption(templateImageOption);
            var resolvedConfigFile = context.ParseResult.GetValueForOption(configFileOption)?.FullName;
            var count = context.ParseResult.GetValueForOption(countOption);
            var overwrite = context.ParseResult.GetValueForOption(overwriteOption);
            var jsonOutput = context.ParseResult.GetValueForOption(jsonOutputOption);

            var templateDir = templateImage?.FullName
                ?? LoadProjectImageTemplateDir(resolvedConfigFile);

            context.ExitCode = await ExecuteAsync(
                projectDir.FullName,
                inputDir?.FullName,
                outputDir?.FullName,
                templateDir,
                resolvedConfigFile,
                count,
                overwrite,
                jsonOutput,
                context.GetCancellationToken());
        });

        projectImage.AddCommand(generate);
        return projectImage;
    }

    public async Task<int> ExecuteAsync(
        string projectDir,
        string? inputDir,
        string? outputDir,
        string? templateImageDir,
        string? configFile,
        int? count,
        bool overwrite,
        bool jsonOutput,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _projectImageGenerator.GenerateAsync(
                new ProjectImageGenerateRequest(
                    projectDir,
                    inputDir ?? Path.Combine(projectDir, "videos"),
                    outputDir ?? projectDir,
                    templateImageDir,
                    configFile,
                    count,
                    overwrite),
                cancellationToken);

            if (jsonOutput)
            {
                var outputs = string.Join(",\n", result.Outputs.Select(path => $"    \"{EscapeJson(path)}\""));
                Console.WriteLine($$"""
                {
                  "ok": true,
                  "count": {{result.Count}},
                  "outputs": [
                {{outputs}}
                  ]
                }
                """);
            }
            else
            {
                _logger.LogInformation("Generated {Count} project images.", result.Count);
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate project images");

            if (jsonOutput)
            {
                Console.WriteLine($$"""
                {
                  "ok": false,
                  "errorCode": "PROJECT_IMAGE_FAILED",
                  "message": "{{EscapeJson(ex.Message)}}"
                }
                """);
            }

            return 1;
        }
    }

    private static string? LoadProjectImageTemplateDir(string? configFile)
    {
        if (string.IsNullOrWhiteSpace(configFile) || !File.Exists(configFile))
        {
            return null;
        }

        foreach (var rawLine in File.ReadAllLines(configFile))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (key.Equals("ProjectImageTemplateDir", StringComparison.OrdinalIgnoreCase))
            {
                return Path.IsPathRooted(value)
                    ? value
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configFile)!, value));
            }
        }

        return null;
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
